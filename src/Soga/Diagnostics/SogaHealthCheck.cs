using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Persistence;

namespace Soga.Diagnostics;

/// <summary>Reports Unhealthy when persistence is unreachable and Degraded when the outbox backlog is stale.</summary>
internal sealed class SogaHealthCheck(ISogaStore store, TimeProvider clock, IOptions<SogaOptions> options) : IHealthCheck
{
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
