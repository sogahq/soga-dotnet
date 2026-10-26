using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Diagnostics;
using Soga.Persistence;

namespace Soga.Realtime;

/// <summary>Wakes the dispatcher immediately after a commit so realtime latency does not depend on the poll interval.</summary>
internal sealed class SogaOutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

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

internal static class SogaBackoff
{
    /// <summary>Exponential delay with plus or minus 20 percent jitter, capped at <paramref name="maximum"/>.</summary>
    /// <param name="attempt">The attempt that just failed, starting at 1.</param>
    /// <param name="baseDelay">The delay after the first failure.</param>
    /// <param name="maximum">The largest delay.</param>
    /// <param name="jitterSample">A random value in [0, 1).</param>
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maximum, double jitterSample)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var uncapped = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var jittered = Math.Min(uncapped, maximum.TotalMilliseconds) * (0.8 + (0.4 * jitterSample));
        return TimeSpan.FromMilliseconds(Math.Min(jittered, maximum.TotalMilliseconds));
    }
}

/// <summary>
/// Publishes committed events to SignalR. Delivery is at least once: a crash after publishing and before completing
/// republishes the event, which is why every event carries a stable ID for client deduplication.
/// </summary>
internal sealed partial class SogaOutboxDispatcher(
    IServiceScopeFactory scopes,
    SogaRealtimePublisher publisher,
    SogaOutboxSignal signal,
    TimeProvider clock,
    IOptions<SogaOptions> options,
    ILogger<SogaOutboxDispatcher> logger) : BackgroundService
{
    private DateTimeOffset _nextPurgeAt = DateTimeOffset.MinValue;

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

    /// <summary>Claims and publishes one batch. Returns the number of events claimed.</summary>
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Soga outbox dispatcher is disabled on this instance.")]
    private static partial void LogDispatcherDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Soga is using a non-durable store. Messages are lost on restart. Use Soga.EntityFrameworkCore in production.")]
    private static partial void LogNonDurableStore(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Soga outbox pass failed with {FailureType}.")]
    private static partial void LogPassFailed(ILogger logger, string failureType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Soga outbox event {OutboxId} ({EventType}) failed on attempt {Attempt}; terminal={Terminal}; failure={FailureType}.")]
    private static partial void LogPublishFailed(
        ILogger logger,
        Guid outboxId,
        string eventType,
        int attempt,
        bool terminal,
        string failureType);
}
