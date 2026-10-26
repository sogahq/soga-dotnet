using Microsoft.EntityFrameworkCore;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Groups;
using Soga.Messages;
using Soga.Persistence;
using Soga.Synchronization;

namespace Soga.EntityFrameworkCore.Repositories;

internal sealed partial class EfSogaStore<TContext>
{
    public Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                // A non-participant and a missing conversation are indistinguishable by design. Trusted system
                // messages skip the participant check: the sender is not a participant.
                var conversation = await _db.Set<ConversationRow>().AsNoTracking()
                    .Where(c => c.TenantId == command.TenantId && c.Id == command.ConversationId)
                    .Select(c => new { c.ArchivedAt })
                    .FirstOrDefaultAsync(token);

                var isParticipant = command.IsSystem || await _db.Set<ParticipantRow>().AsNoTracking().AnyAsync(
                    p => p.TenantId == command.TenantId &&
                        p.ConversationId == command.ConversationId &&
                        p.UserId == command.UserId &&
                        p.LeftAt == null,
                    token);
                if (conversation is null || !isParticipant)
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
                    var status = existing.DeletedAt is not null ||
                        string.Equals(existing.Content, command.Content, StringComparison.Ordinal)
                        ? AppendStatus.Duplicate
                        : AppendStatus.IdempotencyConflict;
                    return new AppendMessageOutcome(status, (await HydrateAsync(command.TenantId, [existing], token))[0]);
                }

                if (conversation.ArchivedAt is not null && !command.IsSystem)
                {
                    return new AppendMessageOutcome(AppendStatus.Archived, null);
                }

                if (command.ReplyToMessageId is { } replyId)
                {
                    var replyable = await _db.Set<MessageRow>().AsNoTracking().AnyAsync(
                        m => m.TenantId == command.TenantId &&
                            m.ConversationId == command.ConversationId &&
                            m.Id == replyId &&
                            m.DeletedAt == null,
                        token);
                    if (!replyable)
                    {
                        return new AppendMessageOutcome(AppendStatus.ReplyInvalid, null);
                    }
                }

                // Lock order is always conversation row, then tenant counter row, so concurrent sends cannot deadlock.
                var sequence = await AllocateMessageSequenceAsync(command.TenantId, command.ConversationId, command.Now, token);
                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);

                var row = new MessageRow
                {
                    Id = command.MessageId,
                    TenantId = command.TenantId,
                    ConversationId = command.ConversationId,
                    Sequence = sequence,
                    SenderId = command.UserId,
                    ClientMessageId = command.ClientMessageId,
                    Kind = command.IsSystem ? SogaMessageKinds.System : SogaMessageKinds.Text,
                    Content = command.Content,
                    CreatedAt = command.Now,
                    ReplyToMessageId = command.ReplyToMessageId,
                };
                _db.Add(row);

                var record = (await HydrateAsync(command.TenantId, [row], token))[0];
                Enqueue(command.TenantId, changeSequence, SogaEventFactory.MessageCreated(command.EventId, record, command.Now));
                await _db.SaveChangesAsync(token);

                return new AppendMessageOutcome(AppendStatus.Created, record);
            },
            cancellationToken);

    public Task<EditMessageOutcome> EditMessageAsync(EditMessageCommand command, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                var isParticipant = await IsActiveParticipantAsync(command.TenantId, command.UserId, command.ConversationId, token);
                if (!isParticipant)
                {
                    return new EditMessageOutcome(EditStatus.ConversationNotFound, null);
                }

                var message = await _db.Set<MessageRow>().AsNoTracking().FirstOrDefaultAsync(
                    m => m.TenantId == command.TenantId && m.ConversationId == command.ConversationId && m.Id == command.MessageId,
                    token);
                if (message is null)
                {
                    return new EditMessageOutcome(EditStatus.MessageNotFound, null);
                }

                var check = MessageRules.CanEdit(
                    message.Kind,
                    message.SenderId,
                    message.CreatedAt,
                    message.DeletedAt is not null,
                    command.UserId,
                    command.Now,
                    command.EditWindow);
                if (check != EditCheck.Allowed)
                {
                    var status = check switch
                    {
                        EditCheck.Forbidden => EditStatus.Forbidden,
                        EditCheck.WindowExpired => EditStatus.WindowExpired,
                        _ => EditStatus.NotEditable,
                    };
                    return new EditMessageOutcome(status, null);
                }

                if (string.Equals(message.Content, command.Text, StringComparison.Ordinal))
                {
                    return new EditMessageOutcome(EditStatus.Unchanged, (await HydrateAsync(command.TenantId, [message], token))[0]);
                }

                // Serialize concurrent edits of one message with a row lock so history versions stay contiguous.
                await _db.Set<MessageRow>()
                    .Where(m => m.TenantId == command.TenantId && m.Id == command.MessageId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(m => m.Content, command.Text).SetProperty(m => m.EditedAt, (DateTimeOffset?)command.Now),
                        token);

                if (command.RetainHistory)
                {
                    var version = await _db.Set<MessageEditRow>().AsNoTracking()
                        .CountAsync(e => e.TenantId == command.TenantId && e.MessageId == command.MessageId, token) + 1;
                    _db.Add(new MessageEditRow
                    {
                        TenantId = command.TenantId,
                        MessageId = command.MessageId,
                        Version = version,
                        PreviousContent = message.Content,
                        EditedAt = command.Now,
                    });
                }

                message.Content = command.Text;
                message.EditedAt = command.Now;
                var record = (await HydrateAsync(command.TenantId, [message], token))[0];

                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                Enqueue(command.TenantId, changeSequence, SogaEventFactory.MessageUpdated(command.EventId, record, command.Now));
                await _db.SaveChangesAsync(token);

                return new EditMessageOutcome(EditStatus.Updated, record);
            },
            cancellationToken);

    public Task<DeleteMessageOutcome> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            async token =>
            {
                var access = await GetAccessAsync(command.TenantId, command.UserId, command.ConversationId, token);
                if (access is null)
                {
                    return new DeleteMessageOutcome(DeleteStatus.ConversationNotFound, false, null);
                }

                var message = await _db.Set<MessageRow>().AsNoTracking().FirstOrDefaultAsync(
                    m => m.TenantId == command.TenantId && m.ConversationId == command.ConversationId && m.Id == command.MessageId,
                    token);
                if (message is null)
                {
                    return new DeleteMessageOutcome(DeleteStatus.MessageNotFound, false, null);
                }

                var check = MessageRules.CanDelete(
                    message.Kind,
                    message.SenderId,
                    message.DeletedAt is not null,
                    command.UserId,
                    access.Role,
                    access.IsGroup,
                    command.AllowOwnDeletion,
                    command.AllowAdminDeletion);

                switch (check)
                {
                    case DeleteCheck.Forbidden:
                        return new DeleteMessageOutcome(DeleteStatus.Forbidden, false, message.SenderId);
                    case DeleteCheck.NotDeletable:
                        return new DeleteMessageOutcome(DeleteStatus.NotDeletable, false, message.SenderId);
                    case DeleteCheck.AlreadyDeleted:
                        return new DeleteMessageOutcome(DeleteStatus.AlreadyDeleted, false, message.SenderId);
                }

                // The text is erased so a deleted message never exposes its content again. The row stays as a
                // tombstone so sequences remain contiguous and replies can say "deleted".
                await _db.Set<MessageRow>()
                    .Where(m => m.TenantId == command.TenantId && m.Id == command.MessageId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(m => m.Content, string.Empty)
                            .SetProperty(m => m.DeletedAt, (DateTimeOffset?)command.Now)
                            .SetProperty(m => m.DeletedBy, command.UserId),
                        token);

                // Retained edit history must not keep deleted text alive.
                await _db.Set<MessageEditRow>()
                    .Where(e => e.TenantId == command.TenantId && e.MessageId == command.MessageId)
                    .ExecuteDeleteAsync(token);

                message.Content = string.Empty;
                message.DeletedAt = command.Now;
                message.DeletedBy = command.UserId;
                var record = new MessageRecord(
                    message.Id,
                    message.ConversationId,
                    message.Sequence,
                    message.SenderId,
                    message.ClientMessageId,
                    string.Empty,
                    message.CreatedAt,
                    message.Kind,
                    message.EditedAt,
                    message.DeletedAt,
                    message.DeletedBy);

                var changeSequence = await AllocateChangeSequenceAsync(command.TenantId, token);
                Enqueue(command.TenantId, changeSequence, SogaEventFactory.MessageDeleted(command.EventId, record, command.UserId, command.Now));
                await _db.SaveChangesAsync(token);

                return new DeleteMessageOutcome(
                    DeleteStatus.Deleted,
                    !string.Equals(message.SenderId, command.UserId, StringComparison.Ordinal),
                    message.SenderId);
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
        return await HydrateAsync(tenantId, rows, cancellationToken);
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
                            s => s.SetProperty(p => p.LastReadSequence, target).SetProperty(p => p.LastReadAt, (DateTimeOffset?)command.Now),
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
                        m.SenderId != command.UserId &&
                        m.DeletedAt == null,
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
                message.SenderId != userId &&
                message.DeletedAt == null
            select message.Id;

        return await unread.LongCountAsync(cancellationToken);
    }

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

    /// <summary>Converts rows to records and attaches a bounded summary of each replied-to message with one batched query.</summary>
    private async Task<List<MessageRecord>> HydrateAsync(
        string tenantId,
        IReadOnlyList<MessageRow> rows,
        CancellationToken cancellationToken)
    {
        var replyIds = rows.Where(r => r.ReplyToMessageId is not null).Select(r => r.ReplyToMessageId!.Value).Distinct().ToList();
        var replies = replyIds.Count == 0
            ? []
            : await _db.Set<MessageRow>().AsNoTracking()
                .Where(m => m.TenantId == tenantId && replyIds.Contains(m.Id))
                .Select(m => new { m.Id, m.ConversationId, m.SenderId, m.Content, Deleted = m.DeletedAt != null })
                .ToDictionaryAsync(m => m.Id, cancellationToken);

        return [.. rows.Select(r =>
        {
            ReplyInfo? reply = null;
            if (r.ReplyToMessageId is { } replyId && replies.TryGetValue(replyId, out var target) && target.ConversationId == r.ConversationId)
            {
                reply = new ReplyInfo(target.Id, target.SenderId, SogaText.Snippet(target.Content), target.Deleted);
            }

            return new MessageRecord(
                r.Id,
                r.ConversationId,
                r.Sequence,
                r.SenderId,
                r.ClientMessageId,
                r.Content,
                r.CreatedAt,
                r.Kind,
                r.EditedAt,
                r.DeletedAt,
                r.DeletedBy,
                r.ReplyToMessageId,
                reply);
        })];
    }
}
