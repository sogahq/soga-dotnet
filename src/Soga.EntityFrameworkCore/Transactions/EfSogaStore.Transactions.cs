using Microsoft.EntityFrameworkCore;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.EntityFrameworkCore.Repositories;

/// <content>
/// Contains transaction handling, sequence allocation, and the change and outbox writes shared by every operation.
/// </content>
internal sealed partial class EfSogaStore<TContext>
{
    /// <summary>
    /// The number of times an operation is attempted when it loses a unique-constraint race.
    /// </summary>
    private const int MaximumConstraintRetries = 3;

    /// <summary>
    /// Runs an operation in one transaction using the provider's execution strategy.
    /// </summary>
    /// <typeparam name="T">The type of the operation's result.</typeparam>
    /// <param name="operation">The operation to run. It must be safe to run again from the start.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the operation's result.</returns>
    /// <remarks>
    /// <para>
    /// If a concurrent request wins a unique constraint (direct conversation, client message ID, or first change
    /// sequence), the operation is retried, and its prechecks then find the winner's committed result.
    /// </para>
    /// <para>
    /// When the host has already opened a transaction, Soga joins it and never commits or retries on the host's behalf.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Detaches every tracked Soga row after a rolled-back attempt, leaving the host's own tracked entities untouched.
    /// </summary>
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
    /// Allocates the next message sequence in a conversation and updates its last activity time (ADR 0006).
    /// </summary>
    /// <param name="tenantId">The tenant of the conversation.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="now">The new last activity time.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the allocated sequence.</returns>
    /// <remarks>
    /// The UPDATE takes a row lock that is held until commit, so concurrent senders serialize per conversation,
    /// sequences have no gaps, and a rollback gives the number back.
    /// </remarks>
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
    /// Allocates the next change-log sequence in a tenant (ADR 0008).
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the allocated sequence.</returns>
    /// <exception cref="DbUpdateException">
    /// Another request created the tenant's counter at the same moment. <see cref="ExecuteInTransactionAsync{T}"/>
    /// retries the operation, which then takes the update path.
    /// </exception>
    /// <remarks>
    /// The sequence is allocated under a row lock held until commit. Because commit order equals sequence order, a
    /// client that has read up to sequence N can never later see a change below N appear.
    /// </remarks>
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
    /// Adds the change-log row and the outbox row for one protocol event to the current transaction.
    /// </summary>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="changeSequence">The change-log sequence allocated for the event.</param>
    /// <param name="evt">The event.</param>
    /// <param name="targetUserId">
    /// A user who keeps seeing the change through sync after losing access to the conversation, if any.
    /// </param>
    /// <remarks>Both rows share the event ID and payload, so clients can deduplicate live and synced copies.</remarks>
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
