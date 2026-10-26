using Microsoft.AspNetCore.SignalR;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>Delivers a stored event to the SignalR groups that should hear it. Called only by the outbox dispatcher.</summary>
internal sealed class SogaRealtimePublisher(IHubContext<SogaHub> hub)
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.realtime.publish");
        var evt = SogaEventSerializer.Deserialize(message.Payload);
        var groups = ResolveGroups(message.TenantId, evt);
        if (groups.Count == 0)
        {
            return;
        }

        // The client method name equals the event type, e.g. connection.on("MessageCreated", ...).
        await hub.Clients.Groups(groups).SendAsync(evt.Type, evt, cancellationToken);
    }

    private static List<string> ResolveGroups(string tenantId, SogaEvent evt)
    {
        switch (evt.Type)
        {
            case SogaEventTypes.MessageCreated:
            case SogaEventTypes.ReadPositionChanged:
                return [SogaGroups.Conversation(tenantId, evt.ConversationId)];

            case SogaEventTypes.ConversationCreated:
                return SogaEventSerializer.ReadData<ConversationCreatedData>(evt).ParticipantUserIds
                    .Select(userId => SogaGroups.User(tenantId, userId))
                    .ToList();

            case SogaEventTypes.ConversationAccessChanged:
                return
                [
                    SogaGroups.Conversation(tenantId, evt.ConversationId),
                    SogaGroups.User(tenantId, SogaEventSerializer.ReadData<ConversationAccessChangedData>(evt).UserId),
                ];

            default:
                return [];
        }
    }
}
