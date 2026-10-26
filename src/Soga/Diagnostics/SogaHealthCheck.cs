using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Persistence;

namespace Soga.Diagnostics;

/// <summary>
/// Reports the health of Soga persistence and of the outbox that feeds realtime delivery.
/// </summary>
/// <param name="store">The persistence store to probe.</param>
/// <param name="clock">The clock used to measure the age of pending outbox events.</param>
/// <param name="options">The Soga options that define when the outbox backlog is too old.</param>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="HealthStatus.Unhealthy"/> when the store cannot be reached.</description></item>
/// <item><description>
/// <see cref="HealthStatus.Degraded"/> when the oldest pending outbox event is older than
/// <see cref="SogaOutboxOptions.UnhealthyBacklogAge"/>, which means realtime delivery is falling behind.
/// </description></item>
/// <item><description><see cref="HealthStatus.Healthy"/> otherwise.</description></item>
/// </list>
/// </remarks>
internal sealed class SogaHealthCheck(ISogaStore store, TimeProvider clock, IOptions<SogaOptions> options) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await store.CheckHealthAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Soga persistence is unavailable.");
            }

            var stats = await store.GetOutboxStatsAsync(clock.GetUtcNow(), cancellationToken);
            var data = new Dictionary<string, object>
            {
                ["outboxPending"] = stats.Pending,
                ["outboxFailed"] = stats.Failed,
            };

            if (stats.OldestPendingAge is { } age && age > options.Value.Outbox.UnhealthyBacklogAge)
            {
                data["outboxOldestPendingSeconds"] = (long)age.TotalSeconds;
                return HealthCheckResult.Degraded("The Soga outbox backlog is stale.", data: data);
            }

            return HealthCheckResult.Healthy("Soga is healthy.", data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Soga persistence is unavailable.", exception);
        }
    }
}
