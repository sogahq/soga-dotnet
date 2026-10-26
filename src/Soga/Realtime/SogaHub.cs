using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Soga.Authorization;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Identity;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>
/// A thin authenticated hub. It holds no business logic and no durable state: every subscription is authorized through
/// the same membership check and host policies the HTTP API uses, and group membership is never treated as
/// authorization on its own.
/// </summary>
[Authorize]
internal sealed class SogaHub(
    SogaCallerResolver resolver,
    SogaAuthorizer authorizer,
    ISogaStore store,
    SogaConnectionRegistry registry,
    SogaTypingTracker typing,
    ISogaPresenceProvider presence,
    SogaPresenceBroadcaster presenceBroadcaster,
    TimeProvider clock,
    IOptions<SogaOptions> options,
    IHttpContextAccessor httpContextAccessor) : Hub
{
    private const string CallerKey = "soga.caller";
    private const int MaximumSubscribeAll = 500;

    public override async Task OnConnectedAsync()
    {
        // Tenant resolvers written for HTTP (headers, subdomain, route) read IHttpContextAccessor, which SignalR does not
        // populate. Flow the connection's own request into it so the host's resolver works unchanged. The tenant is
        // resolved once here and pinned to the connection, so later invocations never re-derive it.
        httpContextAccessor.HttpContext = Context.GetHttpContext();

        var result = await resolver.ResolveAsync(Context.User ?? new System.Security.Claims.ClaimsPrincipal(), Context.ConnectionAborted);
        if (!result.IsSuccess)
        {
            Context.Abort();
            return;
        }

        var caller = result.Value;
        Context.Items[CallerKey] = caller;
        registry.Add(caller.TenantId, caller.UserId, Context.ConnectionId);

        // Every connection joins its user group so per-user events (new conversations, membership changes) arrive without a subscription.
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SogaGroups.User(caller.TenantId, caller.UserId),
            Context.ConnectionAborted);

        SogaDiagnostics.ConnectionOpened();

        if (options.Value.Presence.Enabled &&
            await presence.ConnectionOpenedAsync(caller.TenantId, caller.UserId, Context.ConnectionId, clock.GetUtcNow(), Context.ConnectionAborted))
        {
            await presenceBroadcaster.BroadcastAsync(caller.TenantId, caller.UserId, online: true, lastSeenAt: null, Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(CallerKey, out var value) && value is SogaCaller caller)
        {
            SogaDiagnostics.ConnectionClosed();
            registry.Remove(Context.ConnectionId);

            // Typing driven by this connection stops with it. Presence goes offline only after the grace period.
            foreach (var (tenant, conversation, user) in typing.RemoveForConnection(Context.ConnectionId))
            {
                await Clients.Group(SogaGroups.Conversation(tenant, conversation)).SendAsync(
                    SogaEventTypes.TypingChanged,
                    SogaEventFactory.Ephemeral(SogaEventTypes.TypingChanged, conversation, new TypingChangedData(user, false), clock.GetUtcNow()));
            }

            if (options.Value.Presence.Enabled)
            {
                await presence.ConnectionClosedAsync(caller.TenantId, caller.UserId, Context.ConnectionId, clock.GetUtcNow(), CancellationToken.None);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Confirms the client and server speak the same protocol version. Throws <c>soga.protocol.mismatch</c> otherwise.</summary>
#pragma warning disable CA1822 // SignalR only discovers instance methods as hub methods.
    public int NegotiateProtocol(int clientVersion) =>
#pragma warning restore CA1822
        clientVersion == SogaProtocol.Version
            ? SogaProtocol.Version
            : throw new HubException(SogaErrorCodes.ProtocolMismatch);

    /// <summary>Subscribes this connection to a conversation after re-checking access. Call it again after every reconnect.</summary>
    public async Task SubscribeToConversation(Guid conversationId)
    {
        var caller = GetCaller();
        var (error, _) = await authorizer.RequireMemberAsync(SogaAction.Subscribe, caller, conversationId, Context.ConnectionAborted);
        if (error is not null)
        {
            throw new HubException(error.Code);
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SogaGroups.Conversation(caller.TenantId, conversationId),
            Context.ConnectionAborted);
        registry.Subscribe(Context.ConnectionId, conversationId);
    }

    /// <summary>
    /// Subscribes to every conversation the user can currently access (up to 500, most recent first) and returns their IDs.
    /// Use it after a reconnect: only still-authorized subscriptions are restored.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SubscribeToAll()
    {
        var caller = GetCaller();
        var ids = await store.ListActiveConversationIdsAsync(caller.TenantId, caller.UserId, MaximumSubscribeAll, Context.ConnectionAborted);

        var subscribed = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            // Without host policies the membership just proven by the list is enough; with policies, ask each one.
            if (authorizer.HasPolicies)
            {
                var (error, _) = await authorizer.RequireMemberAsync(SogaAction.Subscribe, caller, id, Context.ConnectionAborted);
                if (error is not null)
                {
                    continue;
                }
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, SogaGroups.Conversation(caller.TenantId, id), Context.ConnectionAborted);
            registry.Subscribe(Context.ConnectionId, id);
            subscribed.Add(id);
        }

        return subscribed;
    }

    /// <summary>Removes this connection from a conversation group.</summary>
    public Task UnsubscribeFromConversation(Guid conversationId)
    {
        var caller = GetCaller();
        registry.Unsubscribe(Context.ConnectionId, conversationId);
        return Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            SogaGroups.Conversation(caller.TenantId, conversationId),
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Says the caller is (or is no longer) typing in a conversation. Repeat it while typing: it expires on its own after
    /// <c>Typing:Timeout</c>, and broadcasts are throttled. No text is ever sent.
    /// </summary>
    public async Task SetTyping(Guid conversationId, bool typingNow)
    {
        var settings = options.Value.Typing;
        if (!settings.Enabled)
        {
            return;
        }

        var caller = GetCaller();
        var (error, _) = await authorizer.RequireMemberAsync(SogaAction.SetTyping, caller, conversationId, Context.ConnectionAborted);
        if (error is not null)
        {
            throw new HubException(error.Code);
        }

        var broadcast = typingNow
            ? typing.Start(caller.TenantId, conversationId, caller.UserId, Context.ConnectionId)
            : typing.Stop(caller.TenantId, conversationId, caller.UserId);

        if (broadcast)
        {
            await Clients.OthersInGroup(SogaGroups.Conversation(caller.TenantId, conversationId)).SendAsync(
                SogaEventTypes.TypingChanged,
                SogaEventFactory.Ephemeral(SogaEventTypes.TypingChanged, conversationId, new TypingChangedData(caller.UserId, typingNow), clock.GetUtcNow()),
                Context.ConnectionAborted);
        }
    }

    /// <summary>Returns who in a conversation is online. Advisory only: presence never grants or removes access.</summary>
    public async Task<IReadOnlyList<PresenceResponse>> GetPresence(Guid conversationId)
    {
        var settings = options.Value.Presence;
        if (!settings.Enabled)
        {
            return [];
        }

        var caller = GetCaller();
        var (error, _) = await authorizer.RequireMemberAsync(SogaAction.ViewPresence, caller, conversationId, Context.ConnectionAborted);
        if (error is not null)
        {
            throw new HubException(error.Code);
        }

        var conversation = await store.GetConversationAsync(caller.TenantId, caller.UserId, conversationId, Context.ConnectionAborted);
        if (conversation is null)
        {
            throw new HubException(SogaErrorCodes.ConversationNotFound);
        }

        var known = await presence.GetPresenceAsync(
            caller.TenantId,
            [.. conversation.Participants.Select(p => p.UserId)],
            clock.GetUtcNow(),
            Context.ConnectionAborted);

        return [.. known.Select(p => new PresenceResponse(p.UserId, p.Online, settings.ExposeLastSeen ? p.LastSeenAt : null))];
    }

    private SogaCaller GetCaller() =>
        Context.Items.TryGetValue(CallerKey, out var value) && value is SogaCaller caller
            ? caller
            : throw new HubException(SogaErrorCodes.Unauthorized);
}

/// <summary>SignalR group names. They include the tenant so groups from different tenants can never collide.</summary>
internal static class SogaGroups
{
    public static string Conversation(string tenantId, Guid conversationId) =>
        $"soga:{tenantId.Length}:{tenantId}:c:{conversationId:N}";

    public static string User(string tenantId, string userId) =>
        $"soga:{tenantId.Length}:{tenantId}:u:{userId}";
}
