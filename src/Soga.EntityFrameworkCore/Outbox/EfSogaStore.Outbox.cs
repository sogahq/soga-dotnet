using Microsoft.EntityFrameworkCore;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;

namespace Soga.EntityFrameworkCore.Repositories;

/// <content>
/// Contains the outbox operations used by the dispatcher and the health check.
/// </content>
internal sealed partial class EfSogaStore<TContext>
{
    /// <inheritdoc />
    /// <remarks>
    /// Claiming needs no provider-specific locking. Candidates are read, then each one is leased with a conditional
    /// UPDATE that only succeeds while nobody else holds a live lease, so competing workers never claim the same event,
    /// and a crashed worker's lease simply expires (ADR 0009).
    /// </remarks>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(
        DateTimeOffset now,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        // Oldest first. Events with the same timestamp are ordered by their change-log sequence, which is commit order,
        // so a conversation is never announced after a message sent in it within the same instant.
        var candidates = await (
            from o in _db.Set<OutboxRow>().AsNoTracking()
            where o.PublishedAt == null &&
                o.FailedAt == null &&
                o.AvailableAt <= now &&
                (o.LeaseUntil == null || o.LeaseUntil <= now)
            join c in _db.Set<ChangeRow>().AsNoTracking()
                on new { o.TenantId, EventId = o.Id } equals new { c.TenantId, c.EventId } into changes
            from c in changes.DefaultIfEmpty()
            orderby o.OccurredAt, o.TenantId, (long?)c!.Sequence, o.Id
            select o)
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

    /// <inheritdoc />
    public async Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken) =>
        await _db.Set<OutboxRow>()
            .Where(o => o.TenantId == message.TenantId && o.Id == message.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(o => o.PublishedAt, now).SetProperty(o => o.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);

    /// <inheritdoc />
    /// <remarks>The failure code is cut to 200 characters to fit the column.</remarks>
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

    /// <inheritdoc />
    public async Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken) =>
        await _db.Set<OutboxRow>()
            .Where(o => o.PublishedAt != null && o.PublishedAt < publishedBefore)
            .ExecuteDeleteAsync(cancellationToken);

    /// <inheritdoc />
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
