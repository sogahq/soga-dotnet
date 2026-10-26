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
/// The Soga SignalR hub, mapped at <c>{prefix}/hub</c>.
/// </summary>
/// <param name="resolver">Resolves the trusted caller when a connection opens.</param>
/// <param name="authorizer">Checks membership and host policies for every subscription.</param>
/// <param name="store">The persistence store.</param>
/// <param name="registry">Tracks this process's connections and their subscriptions.</param>
/// <param name="typing">Tracks typing indicators.</param>
/// <param name="presence">The presence provider.</param>
/// <param name="presenceBroadcaster">Broadcasts presence changes.</param>
/// <param name="clock">The clock that timestamps ephemeral events.</param>
/// <param name="options">The Soga options that enable typing and presence.</param>
/// <param name="httpContextAccessor">Receives the connection's HTTP context so host tenant resolvers work.</param>
/// <remarks>
/// <para>
/// The hub is thin: it holds no business logic and no durable state. Messages are sent over HTTP; the hub only
/// delivers notifications. Every subscription is authorized through the same membership check and host policies as the
/// HTTP API, and SignalR group membership is never treated as authorization on its own.
/// </para>
/// <para>
/// Hub errors are reported as a <see cref="HubException"/> whose message is a stable code from
/// <see cref="SogaErrorCodes"/>.
/// </para>
/// </remarks>
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
    /// <summary>
    /// The <see cref="HubCallerContext.Items"/> key that holds the connection's <see cref="SogaCaller"/>.
    /// </summary>
    private const string CallerKey = "soga.caller";

    /// <summary>
    /// The largest number of conversations <see cref="SubscribeToAll"/> subscribes to.
    /// </summary>
    private const int MaximumSubscribeAll = 500;

    /// <summary>
    /// Authenticates a new connection, pins its caller, and joins it to the user's own group.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// A connection whose user or tenant cannot be resolved is aborted. The tenant is resolved once here and pinned to
    /// the connection, so later hub calls never re-derive it.
    /// </remarks>
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

    /// <summary>
    /// Cleans up after a connection closes: forgets it, stops its typing indicators, and starts the presence grace period.
    /// </summary>
    /// <param name="exception">The error that closed the connection, or <see langword="null"/> for a normal close.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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

    /// <summary>
    /// Confirms that the client and server use the same protocol version.
    /// </summary>
    /// <param name="clientVersion">The protocol version the client implements.</param>
    /// <returns>The server's protocol version, which equals <paramref name="clientVersion"/>.</returns>
    /// <exception cref="HubException">The versions differ. The message is <c>soga.protocol.mismatch</c>.</exception>
    /// <remarks>
    /// Clients should call this first after connecting. It is answered only once connection setup has finished, so it
    /// also tells the client that the connection is ready to subscribe.
    /// </remarks>
#pragma warning disable CA1822 // SignalR only discovers instance methods as hub methods.
    public int NegotiateProtocol(int clientVersion) =>
#pragma warning restore CA1822
        clientVersion == SogaProtocol.Version
            ? SogaProtocol.Version
            : throw new HubException(SogaErrorCodes.ProtocolMismatch);

    /// <summary>
    /// Subscribes this connection to a conversation's events after checking access.
    /// </summary>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">
    /// The caller is not a participant (<c>soga.conversation.not_found</c>) or a host policy denies it
    /// (<c>soga.authorization.denied</c>).
    /// </exception>
    /// <remarks>Subscriptions do not survive a reconnect. Subscribe again, or call <see cref="SubscribeToAll"/>.</remarks>
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
    /// Subscribes this connection to every conversation the caller can currently access.
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the IDs of the conversations
    /// subscribed to, up to 500, most recent activity first.
    /// </returns>
    /// <remarks>
    /// Use this after a reconnect. Only subscriptions the caller is still authorized for are restored, so a user removed
    /// while offline does not regain access.
    /// </remarks>
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

    /// <summary>
    /// Stops this connection from receiving a conversation's events.
    /// </summary>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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
    /// Tells the other participants that the caller started or stopped typing in a conversation.
    /// </summary>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="typingNow"><see langword="true"/> while typing; <see langword="false"/> to stop.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="HubException">The caller is not a participant, or a host policy denies it.</exception>
    /// <remarks>
    /// Call this repeatedly while the user types. The indicator expires on its own after
    /// <see cref="SogaTypingOptions.Timeout"/>, broadcasts are throttled, and no text is ever sent. The call does
    /// nothing when typing indicators are disabled.
    /// </remarks>
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

    /// <summary>
    /// Gets which participants of a conversation are online.
    /// </summary>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains one entry per participant, or is
    /// empty when presence is disabled.
    /// </returns>
    /// <exception cref="HubException">The caller is not a participant, or a host policy denies it.</exception>
    /// <remarks>Presence is advisory only: it never grants or removes access.</remarks>
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

    /// <summary>
    /// Gets the caller pinned to this connection by <see cref="OnConnectedAsync"/>.
    /// </summary>
    /// <returns>The trusted caller.</returns>
    /// <exception cref="HubException">The connection has no caller. The message is <c>soga.auth.unauthorized</c>.</exception>
    private SogaCaller GetCaller() =>
        Context.Items.TryGetValue(CallerKey, out var value) && value is SogaCaller caller
            ? caller
            : throw new HubException(SogaErrorCodes.Unauthorized);
}

/// <summary>
/// Builds the SignalR group names Soga uses.
/// </summary>
/// <remarks>
/// Names include the tenant, length-prefixed, so groups from different tenants can never collide whatever characters
/// the IDs contain.
/// </remarks>
internal static class SogaGroups
{
    /// <summary>
    /// Gets the name of the group that receives a conversation's events.
    /// </summary>
    /// <param name="tenantId">The tenant of the conversation.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns>The group name.</returns>
    public static string Conversation(string tenantId, Guid conversationId) =>
        $"soga:{tenantId.Length}:{tenantId}:c:{conversationId:N}";

    /// <summary>
    /// Gets the name of the group that receives one user's personal events, on every device.
    /// </summary>
    /// <param name="tenantId">The tenant of the user.</param>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>The group name.</returns>
    public static string User(string tenantId, string userId) =>
        $"soga:{tenantId.Length}:{tenantId}:u:{userId}";
}
