using Soga.Contracts;

namespace Soga.Persistence.InMemory;


internal sealed class InMemorySogaStore : ISogaStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, Guid Id), ConversationState> _conversations = [];
    private readonly Dictionary<(string Tenant, string Key), Guid> _directIndex = [];
    private readonly Dictionary<string, List<ChangeEntry>> _changes = [];
    private readonly List<OutboxEntry> _outbox = [];

    public bool IsDurable => false;

    public Task<AdvanceReadOutcome> AdvanceReadPositionAsync(AdvanceReadCommand command, CancellationToken cancellationToken)
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

            var participant = conversation.Participants.Single(p => p.UserId == command.UserId);
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

    public Task<AppendMessageOutcome> AppendMessageAsync(AppendMessageCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new AppendMessageOutcome(AppendStatus.ConversationNotFound, null));
            }

            var existing = conversation.Messages.FirstOrDefault(m =>
                m.SenderId == command.UserId && m.ClientMessageId == command.ClientMessageId);
            if (existing is not null)
            {
                var status = string.Equals(existing.Content, command.Content, StringComparison.Ordinal)
                    ? AppendStatus.Duplicate
                    : AppendStatus.IdempotencyConflict;
                return Task.FromResult(new AppendMessageOutcome(status, existing));
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
                command.Now);
            conversation.Messages.Add(message);

            Publish(command.TenantId, SogaEventFactory.MessageCreated(command.EventId, message, command.Now));
            return Task.FromResult(new AppendMessageOutcome(AppendStatus.Created, message));

        }
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);


    public Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(DateTimeOffset now, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
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
                entry.LeaseUntil = null;
                entry.PublishedAt = now;
            }
        }

        return Task.CompletedTask;
    }

    public Task FailOutboxAsync(OutboxMessage message, DateTimeOffset? retryAt, DateTimeOffset now, string failureCode, CancellationToken cancellationToken)
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


    public Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var head = _changes.TryGetValue(tenantId, out var list) && list.Count > 0 ? list[^1].Sequence : 0;
            return Task.FromResult(head);
        }
    }

    public Task<ChangePage> GetChangesAsync(string tenantId, string userId, long afterSequence, int limit, CancellationToken cancellationToken)
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
                    _conversations.TryGetValue((tenantId, c.ConversationId), out var conversation) &&
                    IsActiveParticipant(conversation, userId))
                .Take(limit + 1)
                .ToList();

            var hasMore = visible.Count > limit;
            var page = visible.Take(limit).Select(c => new ChangeRecord(c.Sequence, c.Payload)).ToList();
            return Task.FromResult(new ChangePage(page, hasMore));
        }
    }

    public Task<ConversationRecord?> GetConversationAsync(string tenantId, string userId, Guid conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            return Task.FromResult(conversation is null ? null : ToRecord(conversation, userId));
        }
    }

    public Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(string tenantId, string userId, Guid conversationId, long? beforeSequence, int limit, CancellationToken cancellationToken)
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
                .ToList();
            return Task.FromResult<IReadOnlyList<MessageRecord>?>(page);

        }
    }

    public Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(CreateDirectConversationCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = DirectConversationKey.Create(command.UserId, command.OtherUserId, command.ExternalReference);

        lock (_gate)
        {
            if (_directIndex.TryGetValue((command.TenantId, key), out var existingId))
            {
                var existing = _conversations[(command.TenantId, existingId)];
                return Task.FromResult(new DirectConversationOutcome(ToRecord(existing, command.UserId), false));
            }

            var conversation = new ConversationState(
                command.ConversationId,
                command.TenantId,
                command.ExternalReference,
                command.Now);
            foreach (var participant in new[] { command.UserId, command.OtherUserId }.Order(StringComparer.Ordinal))
            {
                conversation.Participants.Add(new ParticipantState(participant, command.Now));
            }

            _conversations[(command.TenantId, conversation.Id)] = conversation;
            _directIndex[(command.TenantId, key)] = conversation.Id;

            Publish(
                command.TenantId,
                SogaEventFactory.ConversationCreated(
                    command.EventId,
                    conversation.Id,
                    conversation.Participants.Select(p => p.UserId).ToArray(),
                    conversation.ExternalReference,
                    command.Now));

            return Task.FromResult(new DirectConversationOutcome(ToRecord(conversation, command.UserId), true));

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

    public Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var total = _conversations.Values
            .Where(c => c.TenantId == tenantId && IsActiveParticipant(c, userId))
            .Sum(c => Unread(c, c.Participants.Single(p => p.UserId == userId)));
            return Task.FromResult(total);
        }
    }

    public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(string tenantId, string userId, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ConversationRecord> result = _conversations.Values
                .Where(c => c.TenantId == tenantId && IsActiveParticipant(c, userId))
                .OrderByDescending(c => c.LastActivityAt)
                .ThenByDescending(c => c.Id)
                .Take(limit)
                .Select(c => ToRecord(c, userId))
                .ToList();
            return Task.FromResult(result);
        }

    public Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_outbox.RemoveAll(o => o.PublishedAt < publishedBefore));
        }
    }

    private void Publish(string tenantId, SogaEvent evt)
    {
        var payload = SogaEventSerializer.Serialize(evt);

        if (!_changes.TryGetValue(tenantId, out var list))
        {
            list = [];
            _changes[tenantId] = list;
        }

        var sequence = list.Count == 0 ? 1 : list[^1].Sequence + 1;
        list.Add(new ChangeEntry(sequence, evt.EventId, evt.ConversationId, payload));
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
        conversation.Messages.Count(m => m.Sequence > participant.LastReadSequence && m.SenderId != participant.UserId);

    private static ConversationRecord ToRecord(ConversationState conversation, string userId)
    {
        var participant = conversation.Participants.Single(p => p.UserId == userId);
        return new ConversationRecord(
            conversation.Id,
            conversation.ExternalReference,
            conversation.Participants.Where(p => p.LeftAt is null).Select(p => p.UserId).ToArray(),
            conversation.CreatedAt,
            conversation.LastActivityAt,
            conversation.LastSequence,
            participant.LastReadSequence,
            Unread(conversation, participant));
    }

    private sealed class ConversationState(Guid id, string tenantId, string? externalReference, DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;
        public string TenantId { get; } = tenantId;
        public string? ExternalReference { get; } = externalReference;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;
        public long LastSequence { get; set; }
        public List<ParticipantState> Participants { get; } = [];
        public List<MessageRecord> Messages { get; } = [];
    }

    private sealed class ParticipantState(string userId, DateTimeOffset joinedAt)
    {
        public string UserId { get; } = userId;
        public DateTimeOffset JoinedAt { get; } = joinedAt;
        public DateTimeOffset? LeftAt { get; set; }
        public long LastReadSequence { get; set; }
    }

    private sealed record ChangeEntry(long Sequence, Guid EventId, Guid ConversationId, string Payload);

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
