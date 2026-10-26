namespace Soga.Persistence;

/// <summary>
/// The persistence port. Every method is tenant-scoped, and every write is atomic: the message (or other change),
/// its durable synchronization change, and its outbox event commit together or not at all.
/// </summary>
internal interface ISogaStore
{
    /// <summary>Whether data survives a process restart. The in-memory provider returns <see langword="false"/>.</summary>
    bool IsDurable { get; }

    Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken);

    /// <summary>Returns the conversation for an active participant, or <see langword="null"/> for a missing conversation and for non-participants alike.</summary>
    Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken);

    Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken);

    /// <summary>Returns messages with sequence below <paramref name="beforeSequence"/>, newest first, or <see langword="null"/> when the user cannot access the conversation.</summary>
    Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken);

    Task<AdvanceReadOutcome> AdvanceReadPositionAsync(
        AdvanceReadCommand command,
        CancellationToken cancellationToken);

    Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken);

    /// <summary>Returns the newest committed change sequence for the tenant, or 0.</summary>
    Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Returns changes after <paramref name="afterSequence"/> for conversations the user can access now.</summary>
    Task<ChangePage> GetChangesAsync(
        string tenantId,
        string userId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Leases due outbox events to the caller and increments their attempt count.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(
        DateTimeOffset now,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Releases a failed event. A <see langword="null"/> <paramref name="retryAt"/> marks it terminally failed.</summary>
    Task FailOutboxAsync(
        OutboxMessage message,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        string failureCode,
        CancellationToken cancellationToken);

    Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken);

    Task<OutboxStats> GetOutboxStatsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Verifies the persistence dependency is reachable.</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
}
