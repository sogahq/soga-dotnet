using Microsoft.EntityFrameworkCore;
using Soga.Conversations;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.EntityFrameworkCore.Repositories;

/// <summary>
/// The relational Soga store. Every write commits the entity change, its durable synchronization change, and its
/// outbox event in one transaction. Ordering and idempotency are enforced by database constraints; application
/// prechecks only make the common path cheap.
/// </summary>
internal sealed partial class EfSogaStore<TContext> : ISogaStore
    where TContext : DbContext
{
    private readonly TContext _db;

    public EfSogaStore(TContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (db.Model.FindEntityType(typeof(ConversationRow)) is null)
        {
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} does not include the Soga model. Call modelBuilder.AddSoga() from OnModelCreating and add a migration.");
        }

        _db = db;
    }

    public bool IsDurable => true;

    public Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                var key = DirectConversationKey.Create(command.UserId, command.OtherUserId, command.ExternalReference);

                var existingId = await _db.Set<ConversationRow>()
                    .AsNoTracking()
                    .Where(c => c.TenantId == command.TenantId && c.DirectKey == key)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync(token);

                if (existingId is { } id)
                {
                    var existing = await LoadConversationsAsync(command.TenantId, command.UserId, id, 1, token);
                    return new DirectConversationOutcome(existing[0], false);
                }

                // Allocate the change sequence before adding entities: the first write for a tenant saves its counter row.
                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                var participants = new[] { command.UserId, command.OtherUserId }.Order(StringComparer.Ordinal).ToArray();

                _db.Add(new ConversationRow
                {
                    Id = command.ConversationId,
                    TenantId = command.TenantId,
                    DirectKey = key,
                    ExternalReference = command.ExternalReference,
                    CreatedAt = command.Now,
                    LastActivityAt = command.Now,
                });

                foreach (var participant in participants)
                {
                    _db.Add(new ParticipantRow
                    {
                        TenantId = command.TenantId,
                        ConversationId = command.ConversationId,
                        UserId = participant,
                        JoinedAt = command.Now,
                    });
                }

                Enqueue(
                    command.TenantId,
                    changeSequence,
                    SogaEventFactory.ConversationCreated(
                        command.EventId,
                        command.ConversationId,
                        participants,
                        command.ExternalReference,
                        command.Now));

                await _db.SaveChangesAsync(token);

                var created = await LoadConversationsAsync(command.TenantId, command.UserId, command.ConversationId, 1, token);
                return new DirectConversationOutcome(created[0], true);
            },
            cancellationToken);

    public async Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversations = await LoadConversationsAsync(tenantId, userId, conversationId, 1, cancellationToken);
        return conversations.Count == 0 ? null : conversations[0];
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken) =>
        await LoadConversationsAsync(tenantId, userId, null, limit, cancellationToken);

    public Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                // A non-participant and a missing conversation are indistinguishable by design.
                var isParticipant = await _db.Set<ParticipantRow>().AsNoTracking().AnyAsync(
                    p => p.TenantId == command.TenantId &&
                        p.ConversationId == command.ConversationId &&
                        p.UserId == command.UserId &&
                        p.LeftAt == null,
                    token);
                if (!isParticipant)
                {
                    return new AppendMessageOutcome(AppendStatus.ConversationNotFound, null);
                }

                var existing = await _db.Set<MessageRow>().AsNoTracking().FirstOrDefaultAsync(
                    m => m.TenantId == command.TenantId &&
                        m.ConversationId == command.ConversationId &&
                        m.SenderId == command.UserId &&
                        m.ClientMessageId == command.ClientMessageId,
                    token);
                if (existing is not null)
                {
                    var status = string.Equals(existing.Content, command.Content, StringComparison.Ordinal)
                        ? AppendStatus.Duplicate
                        : AppendStatus.IdempotencyConflict;
                    return new AppendMessageOutcome(status, ToRecord(existing));
                }

                // Lock order is always conversation row, then tenant counter row, so concurrent sends cannot deadlock.
                var sequence = await AllocateMessageSequenceAsync(command.TenantId, command.ConversationId, command.Now, token);
                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);

                var message = new MessageRecord(
                    command.MessageId,
                    command.ConversationId,
                    sequence,
                    command.UserId,
                    command.ClientMessageId,
                    command.Content,
                    command.Now);

                _db.Add(new MessageRow
                {
                    Id = message.Id,
                    TenantId = command.TenantId,
                    ConversationId = message.ConversationId,
                    Sequence = message.Sequence,
                    SenderId = message.SenderId,
                    ClientMessageId = message.ClientMessageId,
                    Kind = SogaMappers.MessageKind,
                    Content = message.Content,
                    CreatedAt = message.CreatedAt,
                });

                Enqueue(command.TenantId, changeSequence, SogaEventFactory.MessageCreated(command.EventId, message, command.Now));
                await _db.SaveChangesAsync(token);

                return new AppendMessageOutcome(AppendStatus.Created, message);
            },
            cancellationToken);

    public async Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        var isParticipant = await IsActiveParticipantAsync(tenantId, userId, conversationId, cancellationToken);
        if (!isParticipant)
        {
            return null;
        }

        var query = _db.Set<MessageRow>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId);

        if (beforeSequence is { } before)
        {
            query = query.Where(m => m.Sequence < before);
        }

        var rows = await query.OrderByDescending(m => m.Sequence).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(ToRecord).ToList();
    }

    public Task<AdvanceReadOutcome> AdvanceReadPositionAsync(
        AdvanceReadCommand command,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                var lastRead = await _db.Set<ParticipantRow>()
                    .AsNoTracking()
                    .Where(p => p.TenantId == command.TenantId &&
                        p.ConversationId == command.ConversationId &&
                        p.UserId == command.UserId &&
                        p.LeftAt == null)
                    .Select(p => (long?)p.LastReadSequence)
                    .FirstOrDefaultAsync(token);
                if (lastRead is null)
                {
                    return new AdvanceReadOutcome(AdvanceStatus.ConversationNotFound, 0, 0);
                }

                var messageSequence = await _db.Set<MessageRow>()
                    .AsNoTracking()
                    .Where(m => m.TenantId == command.TenantId &&
                        m.ConversationId == command.ConversationId &&
                        m.Id == command.MessageId)
                    .Select(m => (long?)m.Sequence)
                    .FirstOrDefaultAsync(token);
                if (messageSequence is null)
                {
                    return new AdvanceReadOutcome(AdvanceStatus.MessageNotFound, 0, 0);
                }

                var target = messageSequence.Value;
                var advanced = false;
                if (target > lastRead.Value)
                {
                    // The WHERE guard makes the update monotonic even when two devices race: a stale writer changes nothing.
                    var updated = await _db.Set<ParticipantRow>()
                        .Where(p => p.TenantId == command.TenantId &&
                            p.ConversationId == command.ConversationId &&
                            p.UserId == command.UserId &&
                            p.LastReadSequence < target)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(p => p.LastReadSequence, target).SetProperty(p => p.LastReadAt, command.Now),
                            token);
                    advanced = updated == 1;
                }

                if (advanced)
                {
                    var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                    Enqueue(
                        command.TenantId,
                        changeSequence,
                        SogaEventFactory.ReadPositionChanged(
                            command.EventId,
                            command.ConversationId,
                            command.UserId,
                            target,
                            command.Now));
                    await _db.SaveChangesAsync(token);
                }

                var current = await _db.Set<ParticipantRow>()
                    .AsNoTracking()
                    .Where(p => p.TenantId == command.TenantId &&
                        p.ConversationId == command.ConversationId &&
                        p.UserId == command.UserId)
                    .Select(p => p.LastReadSequence)
                    .SingleAsync(token);

                var unread = await _db.Set<MessageRow>().AsNoTracking().CountAsync(
                    m => m.TenantId == command.TenantId &&
                        m.ConversationId == command.ConversationId &&
                        m.Sequence > current &&
                        m.SenderId != command.UserId,
                    token);

                return new AdvanceReadOutcome(
                    advanced ? AdvanceStatus.Advanced : AdvanceStatus.Unchanged,
                    current,
                    unread);
            },
            cancellationToken);

    public async Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        var unread =
            from message in _db.Set<MessageRow>().AsNoTracking()
            join participant in _db.Set<ParticipantRow>().AsNoTracking()
                on new { message.TenantId, message.ConversationId }
                equals new { participant.TenantId, participant.ConversationId }
            where participant.TenantId == tenantId &&
                participant.UserId == userId &&
                participant.LeftAt == null &&
                message.Sequence > participant.LastReadSequence &&
                message.SenderId != userId
            select message.Id;

        return await unread.LongCountAsync(cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        await _db.Database.CanConnectAsync(cancellationToken);

    private async Task<bool> IsActiveParticipantAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken) =>
        await _db.Set<ParticipantRow>().AsNoTracking().AnyAsync(
            p => p.TenantId == tenantId &&
                p.ConversationId == conversationId &&
                p.UserId == userId &&
                p.LeftAt == null,
            cancellationToken);

    /// <summary>Loads conversation summaries in two queries: one projection with unread counts, one batched participant lookup.</summary>
    private async Task<List<ConversationRecord>> LoadConversationsAsync(
        string tenantId,
        string userId,
        Guid? conversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        var summaries =
            from participant in _db.Set<ParticipantRow>().AsNoTracking()
            join conversation in _db.Set<ConversationRow>().AsNoTracking()
                on new { participant.TenantId, Id = participant.ConversationId }
                equals new { conversation.TenantId, conversation.Id }
            where participant.TenantId == tenantId &&
                participant.UserId == userId &&
                participant.LeftAt == null &&
                (conversationId == null || conversation.Id == conversationId)
            orderby conversation.LastActivityAt descending, conversation.Id descending
            select new
            {
                conversation.Id,
                conversation.ExternalReference,
                conversation.CreatedAt,
                conversation.LastActivityAt,
                conversation.LastSequence,
                participant.LastReadSequence,
                Unread = _db.Set<MessageRow>().Count(m =>
                    m.TenantId == conversation.TenantId &&
                    m.ConversationId == conversation.Id &&
                    m.Sequence > participant.LastReadSequence &&
                    m.SenderId != userId),
            };

        var rows = await summaries.Take(limit).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var participants = await _db.Set<ParticipantRow>()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && ids.Contains(p.ConversationId) && p.LeftAt == null)
            .Select(p => new { p.ConversationId, p.UserId })
            .ToListAsync(cancellationToken);

        var byConversation = participants
            .GroupBy(p => p.ConversationId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.UserId).Order(StringComparer.Ordinal).ToArray());

        return rows.Select(r => new ConversationRecord(
            r.Id,
            r.ExternalReference,
            byConversation.GetValueOrDefault(r.Id, []),
            r.CreatedAt,
            r.LastActivityAt,
            r.LastSequence,
            r.LastReadSequence,
            r.Unread)).ToList();
    }

    private static MessageRecord ToRecord(MessageRow row) => new(
        row.Id,
        row.ConversationId,
        row.Sequence,
        row.SenderId,
        row.ClientMessageId,
        row.Content,
        row.CreatedAt);
}
