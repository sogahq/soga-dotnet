using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Extensibility;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>
/// Expires stale typing indicators and ends presence after the grace period. Both are in-memory and advisory: losing
/// them (a restart, a crashed tick) can leave an indicator briefly wrong but never affects durable correctness.
/// </summary>
internal sealed class SogaEphemeralService(
    IHubContext<SogaHub> hub,
    SogaTypingTracker typing,
    ISogaPresenceProvider presence,
    SogaPresenceBroadcaster presenceBroadcaster,
    TimeProvider clock,
    IOptions<SogaOptions> options) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A failing tick must never stop the loop; ephemeral state is best effort.
            catch (Exception)
#pragma warning restore CA1031
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
    }

    /// <summary>Runs one expiry pass. Exposed for tests.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (settings.Typing.Enabled)
        {
            foreach (var (tenant, conversation, user) in typing.Expire())
            {
                var evt = SogaEventFactory.Ephemeral(
                    SogaEventTypes.TypingChanged,
                    conversation,
                    new TypingChangedData(user, false),
                    clock.GetUtcNow());
                await hub.Clients.Group(SogaGroups.Conversation(tenant, conversation)).SendAsync(evt.Type, evt, cancellationToken);
            }
        }

        if (settings.Presence.Enabled)
        {
            foreach (var (tenant, expired) in await presence.CollectExpiredAsync(clock.GetUtcNow(), cancellationToken))
            {
                await presenceBroadcaster.BroadcastAsync(tenant, expired.UserId, online: false, expired.LastSeenAt, cancellationToken);
            }
        }
    }
}
