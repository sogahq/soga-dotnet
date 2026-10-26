using Microsoft.EntityFrameworkCore;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;

namespace Soga.EntityFrameworkCore.Repositories;

internal sealed partial class EfSogaStore<TContext>
{
    /// <summary>
    /// Claims due events without provider-specific locking. Candidates are read, then each is leased with a
    /// conditional UPDATE that only succeeds while nobody else holds a live lease, so competing workers never
    /// double-claim, and a crashed worker's lease simply expires (ADR 0009).
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(
        DateTimeOffset now,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var candidates = await _db.Set<OutboxRow>()
            .AsNoTracking()
            .Where(o => o.PublishedAt == null &&
                o.FailedAt == null &&
                o.AvailableAt <= now &&
                (o.LeaseUntil == null || o.LeaseUntil <= now))
            .OrderBy(o => o.OccurredAt)
            .ThenBy(o => o.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var leaseUntil = now + leaseDuration;
        var claimed = new List<OutboxMessage>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var attempts = candidate.AttemptCount;
            var updated = await _db.Set<OutboxRow>()
                .Where(o => o.TenantId == candidate.TenantId &&
                    o.Id == candidate.Id &&
                    o.PublishedAt == null &&
                    o.FailedAt == null &&
                    o.AttemptCount == attempts &&
                    (o.LeaseUntil == null || o.LeaseUntil <= now))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.LeaseUntil, leaseUntil).SetProperty(o => o.AttemptCount, attempts + 1),
                    cancellationToken);

            if (updated == 1)
            {
                claimed.Add(new OutboxMessage(candidate.Id, candidate.TenantId, candidate.EventType, candidate.Payload, attempts + 1));
            }
        }

        return claimed;
    }

    public async Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken) =>
        await _db.Set<OutboxRow>()
            .Where(o => o.TenantId == message.TenantId && o.Id == message.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(o => o.PublishedAt, now).SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);

    public async Task FailOutboxAsync(
        OutboxMessage message,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var failure = failureCode.Length > 200 ? failureCode[..200] : failureCode;
        var rows = _db.Set<OutboxRow>().Where(o => o.TenantId == message.TenantId && o.Id == message.Id);

        if (retryAt is { } next)
        {
            await rows.ExecuteUpdateAsync(
                s => s.SetProperty(o => o.AvailableAt, next)
                    .SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(o => o.LastFailure, failure),
                cancellationToken);
        }
        else
        {
            await rows.ExecuteUpdateAsync(
                s => s.SetProperty(o => o.FailedAt, now)
                    .SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(o => o.LastFailure, failure),
                cancellationToken);
        }
    }

    public async Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken) =>
        await _db.Set<OutboxRow>()
            .Where(o => o.PublishedAt != null && o.PublishedAt < publishedBefore)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<OutboxStats> GetOutboxStatsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = _db.Set<OutboxRow>().AsNoTracking().Where(o => o.PublishedAt == null && o.FailedAt == null);

        var pendingCount = await pending.LongCountAsync(cancellationToken);
        var oldest = await pending
            .OrderBy(o => o.OccurredAt)
            .Select(o => (DateTimeOffset?)o.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        var failed = await _db.Set<OutboxRow>().AsNoTracking().LongCountAsync(o => o.FailedAt != null, cancellationToken);

        return new OutboxStats(pendingCount, oldest is null ? null : now - oldest.Value, failed);
    }
}
