using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Diagnostics;
using Soga.Extensibility;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.Realtime;

/// <summary>
/// Wakes the outbox dispatcher as soon as an event is committed, so realtime latency does not depend on the poll
/// interval.
/// </summary>
/// <remarks>
/// The signal is a single-slot channel: many notifications between two dispatcher passes collapse into one wake-up.
/// </remarks>
internal sealed class SogaOutboxSignal
{
    /// <summary>
    /// The single-slot channel that carries wake-ups. Extra writes are dropped while a wake-up is pending.
    /// </summary>
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// Signals that new outbox events are ready.
    /// </summary>
    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>
    /// Waits until <see cref="Notify"/> is called or the timeout elapses, whichever comes first.
    /// </summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <param name="clock">The clock used for the timeout.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that completes when the dispatcher should run another pass.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _channel.Reader.WaitToReadAsync(linked.Token);
            _channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The poll interval elapsed.
        }
    }
}

/// <summary>
/// Computes retry delays for failed outbox events.
/// </summary>
internal static class SogaBackoff
{
    /// <summary>
    /// Computes an exponential retry delay with jitter.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, starting at 1.</param>
    /// <param name="baseDelay">The delay after the first failure.</param>
    /// <param name="maximum">The largest delay.</param>
    /// <param name="jitterSample">A random value in [0, 1).</param>
    /// <returns>
    /// <paramref name="baseDelay"/> doubled for each previous attempt, adjusted by plus or minus 20 percent, and capped
    /// at <paramref name="maximum"/>.
    /// </returns>
    /// <remarks>Jitter spreads out retries so many failed events do not all retry at the same moment.</remarks>
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maximum, double jitterSample)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var uncapped = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var jittered = Math.Min(uncapped, maximum.TotalMilliseconds) * (0.8 + (0.4 * jitterSample));
        return TimeSpan.FromMilliseconds(Math.Min(jittered, maximum.TotalMilliseconds));
    }
}

/// <summary>
/// A background service that publishes committed outbox events to SignalR and to host event subscribers (ADR 0009).
/// </summary>
/// <param name="scopes">Creates a scope for each pass, used to resolve the store and event subscribers.</param>
/// <param name="publisher">Sends events to SignalR groups.</param>
/// <param name="signal">Wakes the dispatcher when a new event is committed.</param>
/// <param name="clock">The clock used for leases, retries, and purging.</param>
/// <param name="options">The Soga options that configure the outbox.</param>
/// <param name="logger">The logger for dispatcher diagnostics.</param>
/// <remarks>
/// <para>
/// Events are claimed with a time-limited lease, so several server instances can run the dispatcher without
/// publishing the same event at the same time. A failed event is retried with exponential backoff until
/// <see cref="SogaOutboxOptions.MaximumAttempts"/>.
/// </para>
/// <para>
/// Delivery is at least once. A crash after publishing but before completing the record republishes the event, which
/// is why every event carries a stable ID that clients use to deduplicate.
/// </para>
/// </remarks>
internal sealed partial class SogaOutboxDispatcher(
    IServiceScopeFactory scopes,
    SogaRealtimePublisher publisher,
    SogaOutboxSignal signal,
    TimeProvider clock,
    IOptions<SogaOptions> options,
    ILogger<SogaOutboxDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// When the next purge of old completed outbox records is due.
    /// </summary>
    private DateTimeOffset _nextPurgeAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Runs dispatch passes until the host stops.
    /// </summary>
    /// <param name="stoppingToken">Triggered when the host is shutting down.</param>
    /// <returns>A task that completes when the service stops.</returns>
    /// <remarks>
    /// After a full batch the next pass starts immediately. Otherwise the dispatcher waits for a signal or for
    /// <see cref="SogaOutboxOptions.PollInterval"/>.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Outbox;
        if (!settings.DispatcherEnabled)
        {
            LogDispatcherDisabled(logger);
            return;
        }

        using (var scope = scopes.CreateScope())
        {
            if (!scope.ServiceProvider.GetRequiredService<ISogaStore>().IsDurable)
            {
                LogNonDurableStore(logger);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await DispatchOnceAsync(stoppingToken);
                await PurgeIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogPassFailed(logger, exception.GetType().Name);
            }

            if (processed < settings.BatchSize)
            {
                try
                {
                    await signal.WaitAsync(settings.PollInterval, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Claims and publishes one batch of outbox events.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the number of events claimed.</returns>
    /// <remarks>
    /// Each event is published, then handed to host subscribers, then marked complete. A failure schedules a retry, or
    /// marks the event as failed for good after the last attempt. Internal so tests can run a pass directly.
    /// </remarks>
    internal async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Outbox;
        var started = clock.GetTimestamp();

        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISogaStore>();
        var claimed = await store.ClaimOutboxAsync(
            clock.GetUtcNow(),
            settings.BatchSize,
            settings.LeaseDuration,
            cancellationToken);

        foreach (var message in claimed)
        {
            using var activity = SogaDiagnostics.StartActivity("soga.outbox.dispatch");
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                await RunHandlersAsync(scope.ServiceProvider, message, cancellationToken);
                await store.CompleteOutboxAsync(message, clock.GetUtcNow(), cancellationToken);
                SogaDiagnostics.RecordOutboxPublished();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var now = clock.GetUtcNow();
                var terminal = message.Attempt >= settings.MaximumAttempts;
                DateTimeOffset? retryAt = terminal
                    ? null
                    : now + SogaBackoff.Compute(
                        message.Attempt,
                        settings.BaseRetryDelay,
                        settings.MaximumRetryDelay,
                        Random.Shared.NextDouble());

                // Only the exception type is stored: messages may embed private content.
                await store.FailOutboxAsync(message, retryAt, now, exception.GetType().Name, cancellationToken);
                SogaDiagnostics.RecordOutboxFailed(terminal);
                LogPublishFailed(logger, message.Id, message.EventType, message.Attempt, terminal, exception.GetType().Name);
            }
        }

        SogaDiagnostics.RecordDispatchDuration(clock.GetElapsedTime(started));
        return claimed.Count;
    }

    /// <summary>
    /// Runs every host <see cref="ISogaEventSubscriber"/> for an event that has just been published.
    /// </summary>
    /// <param name="services">The scoped service provider to resolve subscribers from.</param>
    /// <param name="message">The outbox record that holds the event.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// A subscriber failure fails the whole event, which is then retried. Subscribers must therefore be idempotent and
    /// deduplicate by event ID. Realtime delivery may be repeated as a result, but is never skipped.
    /// </remarks>
    private async Task RunHandlersAsync(IServiceProvider services, OutboxMessage message, CancellationToken cancellationToken)
    {
        var handlers = services.GetServices<ISogaEventSubscriber>().ToArray();
        if (handlers.Length == 0)
        {
            return;
        }

        var notification = new SogaEventNotification(message.TenantId, SogaEventSerializer.Deserialize(message.Payload));
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(notification, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SogaLog.EventSubscriberFailure(logger, handler.GetType().Name, exception.GetType().Name);
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes completed outbox records older than <see cref="SogaOutboxOptions.Retention"/>, at most once every ten
    /// minutes.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now < _nextPurgeAt)
        {
            return;
        }

        _nextPurgeAt = now + TimeSpan.FromMinutes(10);
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISogaStore>();
        await store.PurgeOutboxAsync(now - options.Value.Outbox.Retention, cancellationToken);
    }

    /// <summary>
    /// Logs that the dispatcher is disabled on this instance.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Soga outbox dispatcher is disabled on this instance.")]
    private static partial void LogDispatcherDisabled(ILogger logger);

    /// <summary>
    /// Logs a warning that the in-memory store is in use and data will be lost on restart.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Soga is using a non-durable store. Messages are lost on restart. Use Soga.EntityFrameworkCore in production.")]
    private static partial void LogNonDurableStore(ILogger logger);

    /// <summary>
    /// Logs that a whole dispatch pass failed unexpectedly.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="failureType">The type name of the exception.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Soga outbox pass failed with {FailureType}.")]
    private static partial void LogPassFailed(ILogger logger, string failureType);

    /// <summary>
    /// Logs that publishing one outbox event failed.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="outboxId">The ID of the outbox record.</param>
    /// <param name="eventType">The event type.</param>
    /// <param name="attempt">The attempt that failed.</param>
    /// <param name="terminal"><see langword="true"/> if the event will not be retried.</param>
    /// <param name="failureType">The type name of the exception.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Soga outbox event {OutboxId} ({EventType}) failed on attempt {Attempt}; terminal={Terminal}; failure={FailureType}.")]
    private static partial void LogPublishFailed(
        ILogger logger,
        Guid outboxId,
        string eventType,
        int attempt,
        bool terminal,
        string failureType);
}
