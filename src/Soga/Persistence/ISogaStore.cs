namespace Soga.Persistence;

/// <summary>
/// The persistence port. Every method is tenant-scoped, and every write is atomic: the change itself, its durable
/// synchronization change(s), and its outbox event(s) commit together or not at all.
/// </summary>
internal interface ISogaStore
{
    /// <summary>Whether data survives a process restart. The in-memory provider returns <see langword="false"/>.</summary>
    bool IsDurable { get; }

    Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken);

    Task<ConversationRecord> CreateGroupAsync(CreateGroupCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a membership, role, update, or archive change. The store locks the conversation, loads a snapshot,
    /// lets <c>GroupRules</c> decide, and applies the result in one transaction.
    /// </summary>
    Task<GroupMutationOutcome> MutateGroupAsync(GroupMutationCommand command, CancellationToken cancellationToken);

    /// <summary>Returns the conversation with participants for an active participant, or <see langword="null"/> for a missing conversation and for non-participants alike.</summary>
    Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken);

    /// <summary>Returns the caller's role and the conversation kind, or <see langword="null"/> when the caller is not an active participant. Cheap: it loads no participants or messages.</summary>
    Task<ConversationAccess?> GetAccessAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken);

    /// <summary>Lists conversations most recent first. Group participant lists are not loaded.</summary>
    Task<ConversationPage> ListConversationsAsync(ListConversationsQuery query, CancellationToken cancellationToken);

    /// <summary>Returns the IDs of conversations the user is an active participant of, most recent first.</summary>
    Task<IReadOnlyList<Guid>> ListActiveConversationIdsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken);

    Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken);

    Task<EditMessageOutcome> EditMessageAsync(EditMessageCommand command, CancellationToken cancellationToken);

    Task<DeleteMessageOutcome> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken cancellationToken);

    /// <summary>Returns messages with sequence below <paramref name="beforeSequence"/>, newest first (deleted ones as tombstones), or <see langword="null"/> when the user cannot access the conversation.</summary>
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

    /// <summary>
    /// Returns changes after <paramref name="afterSequence"/> for conversations the user can access now, plus any
    /// change addressed directly to the user (so a removed participant still learns they were removed).
    /// </summary>
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
