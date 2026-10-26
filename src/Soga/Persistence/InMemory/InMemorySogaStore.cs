using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// The in-memory development implementation of <see cref="ISogaStore"/>, used when no durable store is configured.
/// </summary>
/// <remarks>
/// <para>
/// It offers the same guarantees as the Entity Framework Core store: atomic message, change, and outbox writes;
/// idempotency; gapless ordering; and the same group and message rules. All data is lost when the process stops.
/// </para>
/// <para>
/// A single lock serializes every operation, which is what makes each write atomic. That is simple and correct but
/// does not scale, so this store is meant for samples, tests, and local development only.
/// </para>
/// </remarks>
internal sealed class InMemorySogaStore : ISogaStore
{
    /// <summary>
    /// The lock that serializes every read and write, making each operation atomic.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Every conversation, keyed by tenant and conversation ID.
    /// </summary>
    private readonly Dictionary<(string Tenant, Guid Id), ConversationState> _conversations = [];

    /// <summary>
    /// The uniqueness index of direct conversations, keyed by tenant and <see cref="DirectConversationKey"/>.
    /// </summary>
    private readonly Dictionary<(string Tenant, string Key), Guid> _directIndex = [];

    /// <summary>
    /// The change log of each tenant, in sequence order.
    /// </summary>
    private readonly Dictionary<string, List<ChangeEntry>> _changes = [];

    /// <summary>
    /// The outbox of events waiting to be, or already, published.
    /// </summary>
    private readonly List<OutboxEntry> _outbox = [];

    /// <summary>
    /// The previous texts of edited messages, kept when edit history retention is on.
    /// </summary>
    private readonly List<EditAudit> _edits = [];

    /// <inheritdoc />
    public bool IsDurable => false;

    /// <summary>
    /// Gets the previous texts kept for a message when edit history retention is on.
    /// </summary>
    /// <param name="messageId">The ID of the message.</param>
    /// <returns>The previous texts, oldest first.</returns>
    /// <remarks>Exposed for tests and diagnostics.</remarks>
    internal IReadOnlyList<string> GetEditHistory(Guid messageId)
    {
        lock (_gate)
        {
            return [.. _edits.Where(e => e.MessageId == messageId).OrderBy(e => e.Version).Select(e => e.PreviousContent)];
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var head = _changes.TryGetValue(tenantId, out var list) && list.Count > 0 ? list[^1].Sequence : 0;
            return Task.FromResult(head);
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_outbox.RemoveAll(o => o.PublishedAt < publishedBefore));
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    /// <remarks>The in-memory store is always reachable.</remarks>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// Appends a system message to a conversation and publishes its <c>MessageCreated</c> event. The caller must hold
    /// <see cref="_gate"/>.
    /// </summary>
    /// <param name="conversation">The conversation to append to.</param>
    /// <param name="tenantId">The tenant of the conversation.</param>
    /// <param name="ids">Generates the message and event IDs.</param>
    /// <param name="content">The system message body.</param>
    /// <param name="now">The current time.</param>
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

    /// <summary>
    /// Appends an event to the tenant's change log and to the outbox, as part of the current operation. The caller
    /// must hold <see cref="_gate"/>.
    /// </summary>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="evt">The event to record.</param>
    /// <param name="targetUserId">A user who must see the change in sync even without access to the conversation.</param>
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

    /// <summary>
    /// Finds a conversation that a user is an active participant of.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns>
    /// The conversation, or <see langword="null"/> when it does not exist or the user is not an active participant.
    /// </returns>
    private ConversationState? FindAccessible(string tenantId, string userId, Guid conversationId) =>
        _conversations.TryGetValue((tenantId, conversationId), out var conversation) &&
        IsActiveParticipant(conversation, userId)
            ? conversation
            : null;

    /// <summary>
    /// Determines whether a user is an active participant of a conversation.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="userId">The user.</param>
    /// <returns><see langword="true"/> if the user is a participant who has not left; otherwise, <see langword="false"/>.</returns>
    private static bool IsActiveParticipant(ConversationState conversation, string userId) =>
        conversation.Participants.Any(p => p.UserId == userId && p.LeftAt is null);

    /// <summary>
    /// Counts a participant's unread messages in a conversation.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="participant">The participant.</param>
    /// <returns>
    /// The number of undeleted messages after the participant's read position that were sent by someone else.
    /// </returns>
    private static long Unread(ConversationState conversation, ParticipantState participant) =>
        conversation.Messages.Count(m =>
            m.Sequence > participant.LastReadSequence && m.SenderId != participant.UserId && !m.IsDeleted);

    /// <summary>
    /// Adds the reply summary to a message that replies to another.
    /// </summary>
    /// <param name="conversation">The conversation that holds both messages.</param>
    /// <param name="message">The message to complete.</param>
    /// <returns>The message with <see cref="MessageRecord.ReplyTo"/> filled in, or unchanged if it is not a reply.</returns>
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

    /// <summary>
    /// Builds the record of a conversation as seen by one participant.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="userId">The viewing participant, who must be active.</param>
    /// <param name="includeParticipants"><see langword="true"/> to include the participant list.</param>
    /// <returns>The conversation record, with the viewer's role, read position, and unread count.</returns>
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

    /// <summary>
    /// Represents the mutable state of one conversation.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="tenantId">The tenant that owns the conversation.</param>
    /// <param name="kind">The conversation kind.</param>
    /// <param name="title">The group title, if any.</param>
    /// <param name="externalReference">The host reference, if any.</param>
    /// <param name="metadataJson">The canonical metadata JSON, if any.</param>
    /// <param name="createdAt">When the conversation was created.</param>
    private sealed class ConversationState(
        Guid id,
        string tenantId,
        string kind,
        string? title,
        string? externalReference,
        string? metadataJson,
        DateTimeOffset createdAt)
    {
        /// <summary>Gets the conversation ID.</summary>
        public Guid Id { get; } = id;

        /// <summary>Gets the tenant that owns the conversation.</summary>
        public string TenantId { get; } = tenantId;

        /// <summary>Gets the conversation kind.</summary>
        public string Kind { get; } = kind;

        /// <summary>Gets or sets the group title.</summary>
        public string? Title { get; set; } = title;

        /// <summary>Gets the host reference.</summary>
        public string? ExternalReference { get; } = externalReference;

        /// <summary>Gets or sets the canonical metadata JSON.</summary>
        public string? MetadataJson { get; set; } = metadataJson;

        /// <summary>Gets when the conversation was created.</summary>
        public DateTimeOffset CreatedAt { get; } = createdAt;

        /// <summary>Gets or sets when the group was archived, or <see langword="null"/> if it is active.</summary>
        public DateTimeOffset? ArchivedAt { get; set; }

        /// <summary>Gets or sets when the last message was added.</summary>
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;

        /// <summary>Gets or sets the sequence of the newest message.</summary>
        public long LastSequence { get; set; }

        /// <summary>Gets every current and former participant.</summary>
        public List<ParticipantState> Participants { get; } = [];

        /// <summary>Gets every message, including tombstones, in sequence order.</summary>
        public List<MessageRecord> Messages { get; } = [];
    }

    /// <summary>
    /// Represents the mutable state of one participant.
    /// </summary>
    /// <param name="userId">The participant's user ID.</param>
    /// <param name="role">The participant's role.</param>
    /// <param name="joinedAt">When the participant joined.</param>
    private sealed class ParticipantState(string userId, SogaRole role, DateTimeOffset joinedAt)
    {
        /// <summary>Gets the participant's user ID.</summary>
        public string UserId { get; } = userId;

        /// <summary>Gets or sets the participant's role.</summary>
        public SogaRole Role { get; set; } = role;

        /// <summary>Gets or sets when the participant last joined.</summary>
        public DateTimeOffset JoinedAt { get; set; } = joinedAt;

        /// <summary>Gets or sets when the participant left, or <see langword="null"/> if they are active.</summary>
        public DateTimeOffset? LeftAt { get; set; }

        /// <summary>Gets or sets the participant's read position.</summary>
        public long LastReadSequence { get; set; }
    }

    /// <summary>
    /// Represents one entry in a tenant's change log.
    /// </summary>
    /// <param name="Sequence">The entry's position in the tenant's change log.</param>
    /// <param name="EventId">The event ID, shared with the outbox entry.</param>
    /// <param name="ConversationId">The conversation the change concerns.</param>
    /// <param name="Payload">The serialized event.</param>
    /// <param name="TargetUserId">A user who must see the change even without access to the conversation.</param>
    private sealed record ChangeEntry(long Sequence, Guid EventId, Guid ConversationId, string Payload, string? TargetUserId);

    /// <summary>
    /// Represents a previous version of an edited message.
    /// </summary>
    /// <param name="MessageId">The ID of the message.</param>
    /// <param name="Version">The version number, starting at 1 for the original text.</param>
    /// <param name="PreviousContent">The text before the edit.</param>
    /// <param name="EditedAt">When the edit happened.</param>
    private sealed record EditAudit(Guid MessageId, int Version, string PreviousContent, DateTimeOffset EditedAt);

    /// <summary>
    /// Represents one outbox entry and its delivery state.
    /// </summary>
    /// <param name="id">The event ID.</param>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="eventType">The event type.</param>
    /// <param name="payload">The serialized event.</param>
    /// <param name="occurredAt">When the event happened.</param>
    private sealed class OutboxEntry(Guid id, string tenantId, string eventType, string payload, DateTimeOffset occurredAt)
    {
        /// <summary>Gets the event ID.</summary>
        public Guid Id { get; } = id;

        /// <summary>Gets the tenant of the event.</summary>
        public string TenantId { get; } = tenantId;

        /// <summary>Gets the event type.</summary>
        public string EventType { get; } = eventType;

        /// <summary>Gets the serialized event.</summary>
        public string Payload { get; } = payload;

        /// <summary>Gets when the event happened.</summary>
        public DateTimeOffset OccurredAt { get; } = occurredAt;

        /// <summary>Gets or sets the earliest time the event may next be claimed.</summary>
        public DateTimeOffset AvailableAt { get; set; } = occurredAt;

        /// <summary>Gets or sets when the current claim expires, or <see langword="null"/> if unclaimed.</summary>
        public DateTimeOffset? LeaseUntil { get; set; }

        /// <summary>Gets or sets how many times the event has been claimed.</summary>
        public int Attempts { get; set; }

        /// <summary>Gets or sets when the event was published, or <see langword="null"/> if it has not been.</summary>
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>Gets or sets when the event failed for good, or <see langword="null"/> if it has not.</summary>
        public DateTimeOffset? FailedAt { get; set; }

        /// <summary>Gets or sets the exception type name of the last failure.</summary>
        public string? LastFailure { get; set; }
    }
}
