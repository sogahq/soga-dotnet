using Microsoft.EntityFrameworkCore;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.EntityFrameworkCore.Repositories;

internal sealed partial class EfSogaStore<TContext>
{
    private const int MaximumConstraintRetries = 3;

    /// <summary>
    /// Runs an operation in one transaction with the provider's execution strategy. If a concurrent request wins a
    /// unique constraint (direct conversation, client message ID, first change sequence), the operation is retried:
    /// its prechecks then resolve to the winner's committed result. When the host already opened a transaction, Soga
    /// joins it and never commits or retries on the host's behalf.
    /// </summary>
    private Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
            async token =>
            {
                for (var attempt = 1; ; attempt++)
                {
                    var ownsTransaction = _db.Database.CurrentTransaction is null;
                    await using var transaction = ownsTransaction
                        ? await _db.Database.BeginTransactionAsync(token)
                        : null;

                    try
                    {
                        var result = await operation(token);
                        if (transaction is not null)
                        {
                            await transaction.CommitAsync(token);
                        }

                        return result;
                    }
                    catch (DbUpdateException) when (ownsTransaction && attempt < MaximumConstraintRetries)
                    {
                        // Disposing the transaction rolls it back. Detach only Soga rows so unrelated tracked host entities survive.
                        DetachSogaEntities();
                    }
                }
            },
            cancellationToken);
    }

    private void DetachSogaEntities()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is ConversationRow or ParticipantRow or MessageRow or MessageEditRow or ChangeRow or TenantStateRow or OutboxRow)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Atomically advances the conversation counter. The UPDATE takes a row lock that is held until commit, so
    /// concurrent senders serialize per conversation, sequences have no gaps, and a rollback returns the number.
    /// </summary>
    private async Task<long> AllocateMessageSequenceAsync(
        string tenantId,
        Guid conversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _db.Set<ConversationRow>()
            .Where(c => c.TenantId == tenantId && c.Id == conversationId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.LastSequence, c => c.LastSequence + 1).SetProperty(c => c.LastActivityAt, now),
                cancellationToken);

        return await _db.Set<ConversationRow>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == conversationId)
            .Select(c => c.LastSequence)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Allocates the next per-tenant change sequence under a row lock held until commit. Because commit order equals
    /// sequence order, a client that read up to sequence N can never later see a change below N appear (ADR 0008).
    /// </summary>
    private async Task<long> AllocateChangeSequenceAsync(string tenantId, CancellationToken cancellationToken)
    {
        var updated = await _db.Set<TenantStateRow>()
            .Where(t => t.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastChangeSequence, t => t.LastChangeSequence + 1), cancellationToken);

        if (updated == 0)
        {
            // First write for this tenant. A concurrent first writer makes this insert fail; the caller's retry then takes the UPDATE path.
            _db.Add(new TenantStateRow { TenantId = tenantId, LastChangeSequence = 1 });
            await _db.SaveChangesAsync(cancellationToken);
            return 1;
        }

        return await _db.Set<TenantStateRow>()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => t.LastChangeSequence)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Adds the durable change and the outbox event for one protocol event. Both share the event ID and payload.
    /// <paramref name="targetUserId"/> keeps the change visible to that user through sync after they lose access.
    /// </summary>
    private void Enqueue(string tenantId, long changeSequence, SogaEvent evt, string? targetUserId = null)
    {
        var payload = SogaEventSerializer.Serialize(evt);

        _db.Add(new ChangeRow
        {
            TenantId = tenantId,
            Sequence = changeSequence,
            EventId = evt.EventId,
            ChangeType = evt.Type,
            ConversationId = evt.ConversationId,
            TargetUserId = targetUserId,
            Payload = payload,
            OccurredAt = evt.OccurredAt,
        });

        _db.Add(new OutboxRow
        {
            Id = evt.EventId,
            TenantId = tenantId,
            EventType = evt.Type,
            Payload = payload,
            OccurredAt = evt.OccurredAt,
            AvailableAt = evt.OccurredAt,
        });
    }
}
