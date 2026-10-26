using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>Delivers a stored event to the SignalR groups that should hear it. Called only by the outbox dispatcher.</summary>
internal sealed class SogaRealtimePublisher(
    IHubContext<SogaHub> hub,
    SogaConnectionRegistry registry,
    SogaTypingTracker typing,
    TimeProvider clock)
{
    private sealed record Target(string Group, IReadOnlyList<string> Except);

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
    /// Members hear a membership change through the conversation group. The affected user also hears it through their own
    /// group, which is how a removed user learns they lost access and a new member learns they gained it. Connections that
    /// are already subscribed to the conversation are excluded from the second send so no device receives it twice.
    /// </summary>
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
    /// When a participant is removed or leaves, take every one of their connections out of the conversation group so they
    /// receive nothing further, on any device, and clear their typing state. Subscribing again is refused by the membership check.
    /// </summary>
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

/// <summary>Sends <c>PresenceChanged</c> to every conversation the user belongs to. Ephemeral and advisory.</summary>
internal sealed class SogaPresenceBroadcaster(
    IHubContext<SogaHub> hub,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<SogaOptions> options)
{
    private const int MaximumConversations = 500;

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
