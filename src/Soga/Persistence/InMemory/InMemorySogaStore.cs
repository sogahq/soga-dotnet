using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// A single-process development provider. It offers the same guarantees as the relational provider
/// (atomic message, change, and outbox writes; idempotency; ordering; the same group and message rules) but loses
/// all data on restart.
/// </summary>
internal sealed class InMemorySogaStore : ISogaStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, Guid Id), ConversationState> _conversations = [];
    private readonly Dictionary<(string Tenant, string Key), Guid> _directIndex = [];
    private readonly Dictionary<string, List<ChangeEntry>> _changes = [];
    private readonly List<OutboxEntry> _outbox = [];
    private readonly List<EditAudit> _edits = [];

    public bool IsDurable => false;

    /// <summary>The previous texts kept for a message when edit history retention is on. For tests and diagnostics.</summary>
    internal IReadOnlyList<string> GetEditHistory(Guid messageId)
    {
        lock (_gate)
        {
            return [.. _edits.Where(e => e.MessageId == messageId).OrderBy(e => e.Version).Select(e => e.PreviousContent)];
        }
    }

    public Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = DirectConversationKey.Create(command.UserId, command.OtherUserId, command.ExternalReference);

        lock (_gate)
        {
            if (_directIndex.TryGetValue((command.TenantId, key), out var existingId))
            {
                var existing = _conversations[(command.TenantId, existingId)];
                return Task.FromResult(new DirectConversationOutcome(ToRecord(existing, command.UserId, includeParticipants: true), false));
            }

            var conversation = new ConversationState(
                command.ConversationId,
                command.TenantId,
                SogaConversationKinds.Direct,
                title: null,
                command.ExternalReference,
                command.MetadataJson,
                command.Now);
            foreach (var participant in new[] { command.UserId, command.OtherUserId }.Order(StringComparer.Ordinal))
            {
                conversation.Participants.Add(new ParticipantState(participant, SogaRole.Member, command.Now));
            }

            _conversations[(command.TenantId, conversation.Id)] = conversation;
            _directIndex[(command.TenantId, key)] = conversation.Id;

            Publish(
                command.TenantId,
                SogaEventFactory.ConversationCreated(
                    command.EventId,
                    conversation.Id,
                    [.. conversation.Participants.Select(p => p.UserId)],
                    conversation.ExternalReference,
                    command.Now));

            return Task.FromResult(new DirectConversationOutcome(ToRecord(conversation, command.UserId, includeParticipants: true), true));
        }
    }

    public Task<ConversationRecord> CreateGroupAsync(CreateGroupCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = new ConversationState(
                command.ConversationId,
                command.TenantId,
                SogaConversationKinds.Group,
                command.Title,
                command.ExternalReference,
                command.MetadataJson,
                command.Now);

            conversation.Participants.Add(new ParticipantState(command.UserId, SogaRole.Owner, command.Now));
            foreach (var other in command.OtherParticipantIds.Distinct(StringComparer.Ordinal).Where(id => id != command.UserId))
            {
                conversation.Participants.Add(new ParticipantState(other, SogaRole.Member, command.Now));
            }

            _conversations[(command.TenantId, conversation.Id)] = conversation;
            Publish(
                command.TenantId,
                SogaEventFactory.ConversationCreated(
                    command.EventId,
                    conversation.Id,
                    [.. conversation.Participants.Select(p => p.UserId).Order(StringComparer.Ordinal)],
                    conversation.ExternalReference,
                    command.Now,
                    SogaConversationKinds.Group,
                    conversation.Title));

            return Task.FromResult(ToRecord(conversation, command.UserId, includeParticipants: true));
        }
    }

    public Task<GroupMutationOutcome> MutateGroupAsync(GroupMutationCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_conversations.TryGetValue((command.TenantId, command.ConversationId), out var conversation))
            {
                return Task.FromResult(new GroupMutationOutcome(GroupError.NotFound, false, null));
            }

            var snapshot = new GroupSnapshot(
                conversation.Kind == SogaConversationKinds.Group,
                conversation.ArchivedAt is not null,
                conversation.Title,
                conversation.MetadataJson,
                [.. conversation.Participants.Select(p => new ParticipantSnapshot(p.UserId, p.Role, p.LeftAt is null))]);

            var decision = GroupRules.Decide(snapshot, command.ActorId, command.Mutation, command.MaximumParticipants);
            if (decision.Error != GroupError.None)
            {
                return Task.FromResult(new GroupMutationOutcome(decision.Error, false, null));
            }

            if (!decision.Changed)
            {
                return Task.FromResult(new GroupMutationOutcome(GroupError.None, false, ToRecord(conversation, command.ActorId, includeParticipants: true)));
            }

            switch (command.Mutation)
            {
                case AddParticipantMutation add:
                    var existing = conversation.Participants.FirstOrDefault(p => p.UserId == add.UserId);
                    if (existing is null)
                    {
                        conversation.Participants.Add(new ParticipantState(add.UserId, add.Role, command.Now) { LastReadSequence = conversation.LastSequence });
                    }
                    else
                    {
                        existing.LeftAt = null;
                        existing.Role = add.Role;
                        existing.JoinedAt = command.Now;
                        existing.LastReadSequence = conversation.LastSequence;
                    }

                    break;
                case RemoveParticipantMutation remove:
                    conversation.Participants.First(p => p.UserId == remove.UserId).LeftAt = command.Now;
                    break;
                case ChangeRoleMutation change:
                    conversation.Participants.First(p => p.UserId == change.UserId && p.LeftAt is null).Role = change.Role;
                    break;
                case UpdateConversationMutation update:
                    if (update.SetTitle)
                    {
                        conversation.Title = update.Title;
                    }

                    if (update.SetMetadata)
                    {
                        conversation.MetadataJson = update.MetadataJson;
                    }

                    break;
                case ArchiveMutation archive:
                    conversation.ArchivedAt = archive.Archived ? command.Now : null;
                    break;
            }

            var first = true;
            foreach (var plan in decision.Events)
            {
                var eventId = first ? command.EventId : command.Ids.NewId();
                first = false;
                Publish(
                    command.TenantId,
                    SogaEventFactory.FromPlan(eventId, conversation.Id, plan, command.Now),
                    plan.TargetUserId);

                if (command.PostSystemMessage && plan.Data is ParticipantChangedData participantChange)
                {
                    AppendSystemMessage(conversation, command.TenantId, command.Ids, SystemMessageContent.ForParticipantChange(participantChange), command.Now);
                }
            }

            var stillActive = conversation.Participants.Any(p => p.UserId == command.ActorId && p.LeftAt is null);
            return Task.FromResult(new GroupMutationOutcome(
                GroupError.None,
                true,
                stillActive ? ToRecord(conversation, command.ActorId, includeParticipants: true) : null));
        }
    }

    public Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            return Task.FromResult(conversation is null ? null : ToRecord(conversation, userId, includeParticipants: true));
        }
    }

    public Task<ConversationAccess?> GetAccessAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            var access = conversation is null
                ? null
                : new ConversationAccess(
                    conversation.Kind,
                    conversation.Participants.Single(p => p.UserId == userId && p.LeftAt is null).Role,
                    conversation.ArchivedAt is not null,
                    conversation.ExternalReference);
            return Task.FromResult(access);
        }
    }

    public Task<ConversationPage> ListConversationsAsync(ListConversationsQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var matches = _conversations.Values
                .Where(c => c.TenantId == query.TenantId && IsActiveParticipant(c, query.UserId))
                .Where(c => query.Kind is null || c.Kind == query.Kind)
                .Where(c => query.ExternalReference is null || c.ExternalReference == query.ExternalReference)
                .Where(c => query.State switch
                {
                    ConversationStateFilter.Active => c.ArchivedAt is null,
                    ConversationStateFilter.Archived => c.ArchivedAt is not null,
                    _ => true,
                })
                .Where(c => query.AfterActivity is null || c.LastActivityAt <= query.AfterActivity)
                .OrderByDescending(c => c.LastActivityAt)
                .ThenByDescending(c => c.Id)
                .Skip(query.SkipTies)
                .Take(query.Limit + 1)
                .ToList();

            var hasMore = matches.Count > query.Limit;
            var items = matches
                .Take(query.Limit)
                .Select(c => ToRecord(c, query.UserId, includeParticipants: c.Kind == SogaConversationKinds.Direct))
                .ToList();
            return Task.FromResult(new ConversationPage(items, hasMore));
        }
    }

    public Task<IReadOnlyList<Guid>> ListActiveConversationIdsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<Guid> ids = _conversations.Values
                .Where(c => c.TenantId == tenantId && IsActiveParticipant(c, userId))
                .OrderByDescending(c => c.LastActivityAt)
                .ThenByDescending(c => c.Id)
                .Take(limit)
                .Select(c => c.Id)
                .ToList();
            return Task.FromResult(ids);
        }
    }

    public Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = command.IsSystem
                ? _conversations.GetValueOrDefault((command.TenantId, command.ConversationId))
                : FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new AppendMessageOutcome(AppendStatus.ConversationNotFound, null));
            }

            var existing = conversation.Messages.FirstOrDefault(m =>
                m.SenderId == command.UserId && m.ClientMessageId == command.ClientMessageId);
            if (existing is not null)
            {
                var status = existing.IsDeleted || string.Equals(existing.Content, command.Content, StringComparison.Ordinal)
                    ? AppendStatus.Duplicate
                    : AppendStatus.IdempotencyConflict;
                return Task.FromResult(new AppendMessageOutcome(status, Hydrate(conversation, existing)));
            }

            if (conversation.ArchivedAt is not null && !command.IsSystem)
            {
                return Task.FromResult(new AppendMessageOutcome(AppendStatus.Archived, null));
            }

            if (command.ReplyToMessageId is { } replyId)
            {
                var target = conversation.Messages.FirstOrDefault(m => m.Id == replyId);
                if (target is null || target.IsDeleted)
                {
                    return Task.FromResult(new AppendMessageOutcome(AppendStatus.ReplyInvalid, null));
                }
            }

            conversation.LastSequence++;
            conversation.LastActivityAt = command.Now;
            var message = new MessageRecord(
                command.MessageId,
                conversation.Id,
                conversation.LastSequence,
                command.UserId,
                command.ClientMessageId,
                command.Content,
                command.Now,
                command.IsSystem ? SogaMessageKinds.System : SogaMessageKinds.Text,
                ReplyToMessageId: command.ReplyToMessageId);
            conversation.Messages.Add(message);

            Publish(command.TenantId, SogaEventFactory.MessageCreated(command.EventId, Hydrate(conversation, message), command.Now));
            return Task.FromResult(new AppendMessageOutcome(AppendStatus.Created, Hydrate(conversation, message)));
        }
    }

    public Task<EditMessageOutcome> EditMessageAsync(EditMessageCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new EditMessageOutcome(EditStatus.ConversationNotFound, null));
            }

            var index = conversation.Messages.FindIndex(m => m.Id == command.MessageId);
            if (index < 0)
            {
                return Task.FromResult(new EditMessageOutcome(EditStatus.MessageNotFound, null));
            }

            var message = conversation.Messages[index];
            var check = MessageRules.CanEdit(
                message.Kind,
                message.SenderId,
                message.CreatedAt,
                message.IsDeleted,
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
                return Task.FromResult(new EditMessageOutcome(status, null));
            }

            if (string.Equals(message.Content, command.Text, StringComparison.Ordinal))
            {
                return Task.FromResult(new EditMessageOutcome(EditStatus.Unchanged, Hydrate(conversation, message)));
            }

            if (command.RetainHistory)
            {
                _edits.Add(new EditAudit(message.Id, _edits.Count(e => e.MessageId == message.Id) + 1, message.Content, command.Now));
            }

            var updated = message with { Content = command.Text, EditedAt = command.Now };
            conversation.Messages[index] = updated;
            Publish(command.TenantId, SogaEventFactory.MessageUpdated(command.EventId, Hydrate(conversation, updated), command.Now));
            return Task.FromResult(new EditMessageOutcome(EditStatus.Updated, Hydrate(conversation, updated)));
        }
    }

    public Task<DeleteMessageOutcome> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.ConversationNotFound, false, null));
            }

            var index = conversation.Messages.FindIndex(m => m.Id == command.MessageId);
            if (index < 0)
            {
                return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.MessageNotFound, false, null));
            }

            var message = conversation.Messages[index];
            var actor = conversation.Participants.Single(p => p.UserId == command.UserId && p.LeftAt is null);
            var check = MessageRules.CanDelete(
                message.Kind,
                message.SenderId,
                message.IsDeleted,
                command.UserId,
                actor.Role,
                conversation.Kind == SogaConversationKinds.Group,
                command.AllowOwnDeletion,
                command.AllowAdminDeletion);

            switch (check)
            {
                case DeleteCheck.Forbidden:
                    return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.Forbidden, false, message.SenderId));
                case DeleteCheck.NotDeletable:
                    return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.NotDeletable, false, message.SenderId));
                case DeleteCheck.AlreadyDeleted:
                    return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.AlreadyDeleted, false, message.SenderId));
            }

            // The text is erased so a deleted message never exposes its content again. The row stays as a tombstone
            // so sequences remain contiguous and replies can say "deleted".
            var deleted = message with { Content = string.Empty, DeletedAt = command.Now, DeletedBy = command.UserId };
            conversation.Messages[index] = deleted;
            _edits.RemoveAll(e => e.MessageId == message.Id); // Retained edit history must not keep deleted text alive.
            Publish(command.TenantId, SogaEventFactory.MessageDeleted(command.EventId, deleted, command.UserId, command.Now));
            return Task.FromResult(new DeleteMessageOutcome(
                DeleteStatus.Deleted,
                !string.Equals(message.SenderId, command.UserId, StringComparison.Ordinal),
                message.SenderId));
        }
    }

    public Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            if (conversation is null)
            {
                return Task.FromResult<IReadOnlyList<MessageRecord>?>(null);
            }

            IReadOnlyList<MessageRecord> page = conversation.Messages
                .Where(m => beforeSequence is null || m.Sequence < beforeSequence)
                .OrderByDescending(m => m.Sequence)
                .Take(limit)
                .Select(m => Hydrate(conversation, m))
                .ToList();
            return Task.FromResult<IReadOnlyList<MessageRecord>?>(page);
        }
    }

    public Task<AdvanceReadOutcome> AdvanceReadPositionAsync(
        AdvanceReadCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new AdvanceReadOutcome(AdvanceStatus.ConversationNotFound, 0, 0));
            }

            var message = conversation.Messages.FirstOrDefault(m => m.Id == command.MessageId);
            if (message is null)
            {
                return Task.FromResult(new AdvanceReadOutcome(AdvanceStatus.MessageNotFound, 0, 0));
            }

            var participant = conversation.Participants.Single(p => p.UserId == command.UserId && p.LeftAt is null);
            var advanced = message.Sequence > participant.LastReadSequence;
            if (advanced)
            {
                participant.LastReadSequence = message.Sequence;
                Publish(
                    command.TenantId,
                    SogaEventFactory.ReadPositionChanged(
                        command.EventId,
                        conversation.Id,
                        command.UserId,
                        participant.LastReadSequence,
                        command.Now));
            }

            return Task.FromResult(new AdvanceReadOutcome(
                advanced ? AdvanceStatus.Advanced : AdvanceStatus.Unchanged,
                participant.LastReadSequence,
                Unread(conversation, participant)));
        }
    }

    public Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var total = _conversations.Values
                .Where(c => c.TenantId == tenantId && IsActiveParticipant(c, userId))
                .Sum(c => Unread(c, c.Participants.Single(p => p.UserId == userId && p.LeftAt is null)));
            return Task.FromResult(total);
        }
    }

    public Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var head = _changes.TryGetValue(tenantId, out var list) && list.Count > 0 ? list[^1].Sequence : 0;
            return Task.FromResult(head);
        }
    }

    public Task<ChangePage> GetChangesAsync(
        string tenantId,
        string userId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_changes.TryGetValue(tenantId, out var list))
            {
                return Task.FromResult(new ChangePage([], false));
            }

            var visible = list
                .Where(c => c.Sequence > afterSequence &&
                    (c.TargetUserId == userId ||
                        (_conversations.TryGetValue((tenantId, c.ConversationId), out var conversation) &&
                            IsActiveParticipant(conversation, userId))))
                .Take(limit + 1)
                .ToList();

            var hasMore = visible.Count > limit;
            var page = visible.Take(limit).Select(c => new ChangeRecord(c.Sequence, c.Payload)).ToList();
            return Task.FromResult(new ChangePage(page, hasMore));
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(
        DateTimeOffset now,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var due = _outbox
                .Where(o => o.PublishedAt is null && o.FailedAt is null && o.AvailableAt <= now &&
                    (o.LeaseUntil is null || o.LeaseUntil <= now))
                .OrderBy(o => o.OccurredAt)
                .ThenBy(o => o.Id)
                .Take(batchSize)
                .ToList();

            foreach (var entry in due)
            {
                entry.LeaseUntil = now + leaseDuration;
                entry.Attempts++;
            }

            IReadOnlyList<OutboxMessage> claimed = due
                .Select(o => new OutboxMessage(o.Id, o.TenantId, o.EventType, o.Payload, o.Attempts))
                .ToList();
            return Task.FromResult(claimed);
        }
    }

    public Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = _outbox.FirstOrDefault(o => o.Id == message.Id && o.TenantId == message.TenantId);
            if (entry is not null)
            {
                entry.PublishedAt = now;
                entry.LeaseUntil = null;
            }
        }

        return Task.CompletedTask;
    }

    public Task FailOutboxAsync(
        OutboxMessage message,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        string failureCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = _outbox.FirstOrDefault(o => o.Id == message.Id && o.TenantId == message.TenantId);
            if (entry is not null)
            {
                entry.LeaseUntil = null;
                entry.LastFailure = failureCode;
                if (retryAt is null)
                {
                    entry.FailedAt = now;
                }
                else
                {
                    entry.AvailableAt = retryAt.Value;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_outbox.RemoveAll(o => o.PublishedAt < publishedBefore));
        }
    }

    public Task<OutboxStats> GetOutboxStatsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var pending = _outbox.Where(o => o.PublishedAt is null && o.FailedAt is null).ToList();
            TimeSpan? oldest = pending.Count == 0 ? null : now - pending.Min(o => o.OccurredAt);
            return Task.FromResult(new OutboxStats(pending.Count, oldest, _outbox.Count(o => o.FailedAt is not null)));
        }
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private void AppendSystemMessage(
        ConversationState conversation,
        string tenantId,
        Abstractions.ISogaIdGenerator ids,
        string content,
        DateTimeOffset now)
    {
        var messageId = ids.NewId();
        conversation.LastSequence++;
        conversation.LastActivityAt = now;
        var message = new MessageRecord(
            messageId,
            conversation.Id,
            conversation.LastSequence,
            SogaSystemSender.UserId,
            $"sys:{messageId:N}",
            content,
            now,
            SogaMessageKinds.System);
        conversation.Messages.Add(message);
        Publish(tenantId, SogaEventFactory.MessageCreated(ids.NewId(), message, now));
    }

    private void Publish(string tenantId, SogaEvent evt, string? targetUserId = null)
    {
        var payload = SogaEventSerializer.Serialize(evt);

        if (!_changes.TryGetValue(tenantId, out var list))
        {
            list = [];
            _changes[tenantId] = list;
        }

        var sequence = list.Count == 0 ? 1 : list[^1].Sequence + 1;
        list.Add(new ChangeEntry(sequence, evt.EventId, evt.ConversationId, payload, targetUserId));
        _outbox.Add(new OutboxEntry(evt.EventId, tenantId, evt.Type, payload, evt.OccurredAt));
    }

    private ConversationState? FindAccessible(string tenantId, string userId, Guid conversationId) =>
        _conversations.TryGetValue((tenantId, conversationId), out var conversation) &&
        IsActiveParticipant(conversation, userId)
            ? conversation
            : null;

    private static bool IsActiveParticipant(ConversationState conversation, string userId) =>
        conversation.Participants.Any(p => p.UserId == userId && p.LeftAt is null);

    private static long Unread(ConversationState conversation, ParticipantState participant) =>
        conversation.Messages.Count(m =>
            m.Sequence > participant.LastReadSequence && m.SenderId != participant.UserId && !m.IsDeleted);

    private static MessageRecord Hydrate(ConversationState conversation, MessageRecord message)
    {
        if (message.ReplyToMessageId is not { } replyId)
        {
            return message;
        }

        var target = conversation.Messages.FirstOrDefault(m => m.Id == replyId);
        return target is null
            ? message
            : message with { ReplyTo = new ReplyInfo(target.Id, target.SenderId, SogaText.Snippet(target.Content), target.IsDeleted) };
    }

    private static ConversationRecord ToRecord(ConversationState conversation, string userId, bool includeParticipants)
    {
        var mine = conversation.Participants.Single(p => p.UserId == userId && p.LeftAt is null);
        var active = conversation.Participants.Where(p => p.LeftAt is null).ToList();
        var last = conversation.Messages.Count == 0 ? null : conversation.Messages.MaxBy(m => m.Sequence);

        return new ConversationRecord(
            conversation.Id,
            conversation.Kind,
            conversation.Title,
            conversation.ExternalReference,
            conversation.MetadataJson,
            includeParticipants
                ? [.. active.OrderBy(p => p.UserId, StringComparer.Ordinal).Select(p => new ParticipantInfo(p.UserId, p.Role, p.JoinedAt))]
                : [],
            active.Count,
            conversation.CreatedAt,
            conversation.LastActivityAt,
            conversation.ArchivedAt,
            conversation.LastSequence,
            mine.LastReadSequence,
            Unread(conversation, mine),
            mine.Role,
            last is null
                ? null
                : new MessageSummary(last.Id, last.Sequence, last.SenderId, last.Kind, SogaText.Snippet(last.Content), last.CreatedAt, last.IsDeleted));
    }

    private sealed class ConversationState(
        Guid id,
        string tenantId,
        string kind,
        string? title,
        string? externalReference,
        string? metadataJson,
        DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;
        public string TenantId { get; } = tenantId;
        public string Kind { get; } = kind;
        public string? Title { get; set; } = title;
        public string? ExternalReference { get; } = externalReference;
        public string? MetadataJson { get; set; } = metadataJson;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? ArchivedAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;
        public long LastSequence { get; set; }
        public List<ParticipantState> Participants { get; } = [];
        public List<MessageRecord> Messages { get; } = [];
    }

    private sealed class ParticipantState(string userId, SogaRole role, DateTimeOffset joinedAt)
    {
        public string UserId { get; } = userId;
        public SogaRole Role { get; set; } = role;
        public DateTimeOffset JoinedAt { get; set; } = joinedAt;
        public DateTimeOffset? LeftAt { get; set; }
        public long LastReadSequence { get; set; }
    }

    private sealed record ChangeEntry(long Sequence, Guid EventId, Guid ConversationId, string Payload, string? TargetUserId);

    private sealed record EditAudit(Guid MessageId, int Version, string PreviousContent, DateTimeOffset EditedAt);

    private sealed class OutboxEntry(Guid id, string tenantId, string eventType, string payload, DateTimeOffset occurredAt)
    {
        public Guid Id { get; } = id;
        public string TenantId { get; } = tenantId;
        public string EventType { get; } = eventType;
        public string Payload { get; } = payload;
        public DateTimeOffset OccurredAt { get; } = occurredAt;
        public DateTimeOffset AvailableAt { get; set; } = occurredAt;
        public DateTimeOffset? LeaseUntil { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public string? LastFailure { get; set; }
    }
}
