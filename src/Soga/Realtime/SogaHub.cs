using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;

namespace Soga.Realtime;

/// <summary>
/// A thin authenticated hub. It holds no business logic and no durable state: subscriptions are authorized through the
/// same workflow the HTTP API uses, and group membership is never treated as authorization on its own.
/// </summary>
[Authorize]
internal sealed class SogaHub(
    SogaCallerResolver resolver,
    ConversationWorkflow conversations,
    IHttpContextAccessor httpContextAccessor) : Hub
{
    private const string CallerKey = "soga.caller";

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

        // Every connection joins its user group so per-user events (new conversations) arrive without a subscription.
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SogaGroups.User(caller.TenantId, caller.UserId),
            Context.ConnectionAborted);

        SogaDiagnostics.ConnectionOpened();
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.ContainsKey(CallerKey))
        {
            SogaDiagnostics.ConnectionClosed();
        }

        return base.OnDisconnectedAsync(exception);
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
        var result = await conversations.GetAsync(caller, conversationId, Context.ConnectionAborted);
        if (!result.IsSuccess)
        {
            throw new HubException(result.Error!.Code);
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SogaGroups.Conversation(caller.TenantId, conversationId),
            Context.ConnectionAborted);
    }

    /// <summary>Removes this connection from a conversation group.</summary>
    public Task UnsubscribeFromConversation(Guid conversationId)
    {
        var caller = GetCaller();
        return Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            SogaGroups.Conversation(caller.TenantId, conversationId),
            Context.ConnectionAborted);
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
