using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Extensibility;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>
/// A background service that expires stale typing indicators and marks users offline after their grace period.
/// </summary>
/// <param name="hub">The hub context used to broadcast typing changes.</param>
/// <param name="typing">The tracker of who is typing.</param>
/// <param name="presence">The presence provider.</param>
/// <param name="presenceBroadcaster">Broadcasts users who have just gone offline.</param>
/// <param name="clock">The clock used for expiry.</param>
/// <param name="options">The Soga options that enable typing and presence.</param>
/// <remarks>
/// Typing and presence are in memory and advisory. Losing them, through a restart or a failed pass, can leave an
/// indicator briefly wrong but never affects durable messages.
/// </remarks>
internal sealed class SogaEphemeralService(
    IHubContext<SogaHub> hub,
    SogaTypingTracker typing,
    ISogaPresenceProvider presence,
    SogaPresenceBroadcaster presenceBroadcaster,
    TimeProvider clock,
    IOptions<SogaOptions> options) : BackgroundService
{
    /// <summary>
    /// How often an expiry pass runs.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs expiry passes until the host stops.
    /// </summary>
    /// <param name="stoppingToken">Triggered when the host is shutting down.</param>
    /// <returns>A task that completes when the service stops.</returns>
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

    /// <summary>
    /// Runs one expiry pass: broadcasts typing indicators that timed out and users who have just gone offline.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>Internal so that tests can drive expiry without waiting for the timer.</remarks>
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
