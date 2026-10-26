using Soga.Persistence;
using Soga.Persistence.InMemory;

namespace Soga.Tests.Persistence;

/// <summary>Wraps the in-memory store and injects failures, so tests can prove how Soga behaves when storage misbehaves.</summary>
internal sealed class FaultyStore : ISogaStore
{
    private readonly InMemorySogaStore _inner = new();

    /// <summary>When set, <see cref="CheckHealthAsync"/> returns this value instead of <see langword="true"/>.</summary>
    public bool? HealthResult { get; set; }

    /// <summary>When set, health and outbox-statistics calls throw it.</summary>
    public Exception? HealthFailure { get; set; }

    /// <summary>How many more claim calls throw before claiming works normally.</summary>
    public int ClaimFailuresRemaining { get; set; }

    /// <summary>How many times the outbox was claimed, successfully or not.</summary>
    public int ClaimCalls { get; private set; }

    public bool IsDurable => true;

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        HealthFailure is not null ? Task.FromException<bool>(HealthFailure) : Task.FromResult(HealthResult ?? true);

    public Task<OutboxStats> GetOutboxStatsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        HealthFailure is not null ? Task.FromException<OutboxStats>(HealthFailure) : _inner.GetOutboxStatsAsync(now, cancellationToken);

    public Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(DateTimeOffset now, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ClaimCalls++;
        if (ClaimFailuresRemaining > 0)
        {
            ClaimFailuresRemaining--;
            return Task.FromException<IReadOnlyList<OutboxMessage>>(new InvalidOperationException("The database is unavailable."));
        }

        return _inner.ClaimOutboxAsync(now, batchSize, leaseDuration, cancellationToken);
    }

    public Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(CreateDirectConversationCommand command, CancellationToken cancellationToken) =>
        _inner.GetOrCreateDirectConversationAsync(command, cancellationToken);

    public Task<ConversationRecord> CreateGroupAsync(CreateGroupCommand command, CancellationToken cancellationToken) =>
        _inner.CreateGroupAsync(command, cancellationToken);

    public Task<GroupMutationOutcome> MutateGroupAsync(GroupMutationCommand command, CancellationToken cancellationToken) =>
        _inner.MutateGroupAsync(command, cancellationToken);

    public Task<ConversationRecord?> GetConversationAsync(string tenantId, string userId, Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetConversationAsync(tenantId, userId, conversationId, cancellationToken);

    public Task<ConversationAccess?> GetAccessAsync(string tenantId, string userId, Guid conversationId, CancellationToken cancellationToken) =>
        _inner.GetAccessAsync(tenantId, userId, conversationId, cancellationToken);

    public Task<ConversationPage> ListConversationsAsync(ListConversationsQuery query, CancellationToken cancellationToken) =>
        _inner.ListConversationsAsync(query, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListActiveConversationIdsAsync(string tenantId, string userId, int limit, CancellationToken cancellationToken) =>
        _inner.ListActiveConversationIdsAsync(tenantId, userId, limit, cancellationToken);

    public Task<AppendMessageOutcome> AppendMessageAsync(AppendMessageCommand command, CancellationToken cancellationToken) =>
        _inner.AppendMessageAsync(command, cancellationToken);

    public Task<EditMessageOutcome> EditMessageAsync(EditMessageCommand command, CancellationToken cancellationToken) =>
        _inner.EditMessageAsync(command, cancellationToken);

    public Task<DeleteMessageOutcome> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken cancellationToken) =>
        _inner.DeleteMessageAsync(command, cancellationToken);

    public Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(string tenantId, string userId, Guid conversationId, long? beforeSequence, int limit, CancellationToken cancellationToken) =>
        _inner.GetMessagesAsync(tenantId, userId, conversationId, beforeSequence, limit, cancellationToken);

    public Task<AdvanceReadOutcome> AdvanceReadPositionAsync(AdvanceReadCommand command, CancellationToken cancellationToken) =>
        _inner.AdvanceReadPositionAsync(command, cancellationToken);

    public Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken) =>
        _inner.GetTotalUnreadAsync(tenantId, userId, cancellationToken);

    public Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken) =>
        _inner.GetChangeHeadAsync(tenantId, cancellationToken);

    public Task<ChangePage> GetChangesAsync(string tenantId, string userId, long afterSequence, int limit, CancellationToken cancellationToken) =>
        _inner.GetChangesAsync(tenantId, userId, afterSequence, limit, cancellationToken);

    public Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken) =>
        _inner.CompleteOutboxAsync(message, now, cancellationToken);

    public Task FailOutboxAsync(OutboxMessage message, DateTimeOffset? retryAt, DateTimeOffset now, string failureCode, CancellationToken cancellationToken) =>
        _inner.FailOutboxAsync(message, retryAt, now, failureCode, cancellationToken);

    public Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken) =>
        _inner.PurgeOutboxAsync(publishedBefore, cancellationToken);
}
