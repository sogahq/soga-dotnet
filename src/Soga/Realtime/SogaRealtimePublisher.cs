using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>
/// Delivers a stored event to the SignalR groups that should receive it.
/// </summary>
/// <param name="hub">The hub context used to send events.</param>
/// <param name="registry">The registry of live connections and their subscriptions.</param>
/// <param name="typing">The tracker of who is typing, cleared when a participant is removed.</param>
/// <param name="clock">The clock that timestamps ephemeral events.</param>
/// <remarks>
/// Only <see cref="SogaOutboxDispatcher"/> calls this publisher, so an event is never broadcast before it has been
/// committed to the database.
/// </remarks>
internal sealed class SogaRealtimePublisher(
    IHubContext<SogaHub> hub,
    SogaConnectionRegistry registry,
    SogaTypingTracker typing,
    TimeProvider clock)
{
    /// <summary>
    /// Represents one SignalR group to send to, and the connections in it to skip.
    /// </summary>
    /// <param name="Group">The SignalR group name.</param>
    /// <param name="Except">The connection IDs that must not receive the event from this group.</param>
    private sealed record Target(string Group, IReadOnlyList<string> Except);

    /// <summary>
    /// Publishes an outbox event to every connection that should receive it.
    /// </summary>
    /// <param name="message">The outbox record that holds the event.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// The SignalR client method name equals the event type, for example
    /// <c>connection.on("MessageCreated", handler)</c>. When the event removes a participant, all of that user's
    /// connections are also taken out of the conversation group.
    /// </remarks>
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.realtime.publish");
        var evt = SogaEventSerializer.Deserialize(message.Payload);

        // The client method name equals the event type, e.g. connection.on("MessageCreated", ...).
        foreach (var target in ResolveTargets(message.TenantId, evt))
        {
            var clients = target.Except.Count == 0
                ? hub.Clients.Group(target.Group)
                : hub.Clients.GroupExcept(target.Group, target.Except);
            await clients.SendAsync(evt.Type, evt, cancellationToken);
        }

        if (evt.Type == SogaEventTypes.ParticipantChanged)
        {
            await RevokeAccessIfRemovedAsync(message.TenantId, evt, cancellationToken);
        }
    }

    /// <summary>
    /// Determines which SignalR groups receive an event.
    /// </summary>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="evt">The event to route.</param>
    /// <returns>
    /// The targets. Conversation events go to the conversation group; a new conversation goes to each participant's
    /// user group; membership changes go to both. Unknown event types go nowhere.
    /// </returns>
    private List<Target> ResolveTargets(string tenantId, SogaEvent evt)
    {
        switch (evt.Type)
        {
            case SogaEventTypes.MessageCreated:
            case SogaEventTypes.MessageUpdated:
            case SogaEventTypes.MessageDeleted:
            case SogaEventTypes.ReadPositionChanged:
            case SogaEventTypes.ConversationUpdated:
            case SogaEventTypes.ConversationArchived:
                return [new Target(SogaGroups.Conversation(tenantId, evt.ConversationId), [])];

            case SogaEventTypes.ConversationCreated:
                return
                [
                    .. SogaEventSerializer.ReadData<ConversationCreatedData>(evt).ParticipantUserIds
                        .Select(userId => new Target(SogaGroups.User(tenantId, userId), [])),
                ];

            case SogaEventTypes.ParticipantChanged:
                return MembershipTargets(tenantId, evt, SogaEventSerializer.ReadData<ParticipantChangedData>(evt).UserId);

            case SogaEventTypes.ConversationAccessChanged:
                return MembershipTargets(tenantId, evt, SogaEventSerializer.ReadData<ConversationAccessChangedData>(evt).UserId);

            default:
                return [];
        }
    }

    /// <summary>
    /// Determines the targets of a membership change.
    /// </summary>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="evt">The membership event.</param>
    /// <param name="affectedUserId">The user who was added, removed, or had their role changed.</param>
    /// <returns>The conversation group, and the affected user's own group.</returns>
    /// <remarks>
    /// Members hear the change through the conversation group. The affected user also hears it through their own group,
    /// which is how a removed user learns they lost access and a new member learns they gained it. Connections that are
    /// already subscribed to the conversation are excluded from the second send, so no device receives it twice.
    /// </remarks>
    private List<Target> MembershipTargets(string tenantId, SogaEvent evt, string affectedUserId)
    {
        var alreadyHear = registry.GetConnections(tenantId, affectedUserId)
            .Where(connectionId => registry.IsSubscribed(connectionId, evt.ConversationId))
            .ToList();

        return
        [
            new Target(SogaGroups.Conversation(tenantId, evt.ConversationId), []),
            new Target(SogaGroups.User(tenantId, affectedUserId), alreadyHear),
        ];
    }

    /// <summary>
    /// Cuts off a participant who was removed or left.
    /// </summary>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="evt">The <see cref="SogaEventTypes.ParticipantChanged"/> event.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Every one of the user's connections, on every device, is taken out of the conversation group so it receives
    /// nothing further, and their typing indicator is cleared. Subscribing again is refused by the membership check.
    /// </remarks>
    private async Task RevokeAccessIfRemovedAsync(string tenantId, SogaEvent evt, CancellationToken cancellationToken)
    {
        var data = SogaEventSerializer.ReadData<ParticipantChangedData>(evt);
        if (data.Change is not ("removed" or "left"))
        {
            return;
        }

        var group = SogaGroups.Conversation(tenantId, evt.ConversationId);
        foreach (var connectionId in registry.GetConnections(tenantId, data.UserId))
        {
            registry.Unsubscribe(connectionId, evt.ConversationId);
            await hub.Groups.RemoveFromGroupAsync(connectionId, group, cancellationToken);
        }

        if (typing.Stop(tenantId, evt.ConversationId, data.UserId))
        {
            await hub.Clients.Group(group).SendAsync(
                SogaEventTypes.TypingChanged,
                SogaEventFactory.Ephemeral(SogaEventTypes.TypingChanged, evt.ConversationId, new TypingChangedData(data.UserId, false), clock.GetUtcNow()),
                cancellationToken);
        }
    }
}

/// <summary>
/// Sends <c>PresenceChanged</c> events to every conversation a user belongs to.
/// </summary>
/// <param name="hub">The hub context used to send events.</param>
/// <param name="scopes">Creates the scope used to resolve the store.</param>
/// <param name="clock">The clock that timestamps events.</param>
/// <param name="options">The Soga options that enable presence and last-seen.</param>
/// <remarks>Presence events are ephemeral and advisory: they are never stored and never grant access.</remarks>
internal sealed class SogaPresenceBroadcaster(
    IHubContext<SogaHub> hub,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<SogaOptions> options)
{
    /// <summary>
    /// The largest number of conversations a single presence change is broadcast to.
    /// </summary>
    private const int MaximumConversations = 500;

    /// <summary>
    /// Broadcasts that a user came online or went offline.
    /// </summary>
    /// <param name="tenantId">The tenant of the user.</param>
    /// <param name="userId">The user whose presence changed.</param>
    /// <param name="online"><see langword="true"/> if the user came online; <see langword="false"/> if they went offline.</param>
    /// <param name="lastSeenAt">When the user was last online. Sent only when last-seen is exposed.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task BroadcastAsync(
        string tenantId,
        string userId,
        bool online,
        DateTimeOffset? lastSeenAt,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.Presence;
        if (!settings.Enabled)
        {
            return;
        }

        IReadOnlyList<Guid> conversations;
        using (var scope = scopes.CreateScope())
        {
            conversations = await scope.ServiceProvider.GetRequiredService<ISogaStore>()
                .ListActiveConversationIdsAsync(tenantId, userId, MaximumConversations, cancellationToken);
        }

        foreach (var conversationId in conversations)
        {
            var evt = SogaEventFactory.Ephemeral(
                SogaEventTypes.PresenceChanged,
                conversationId,
                new PresenceChangedData(userId, online, settings.ExposeLastSeen ? lastSeenAt : null),
                clock.GetUtcNow());
            await hub.Clients.Group(SogaGroups.Conversation(tenantId, conversationId)).SendAsync(evt.Type, evt, cancellationToken);
        }
    }
}
