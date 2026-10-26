namespace Soga.Persistence;

/// <summary>
/// Defines the persistence port: the only way workflows and the outbox dispatcher read or write Soga data.
/// </summary>
/// <remarks>
/// <para>
/// Every method is scoped to a tenant. Every write is atomic: the change itself, its durable sync change, and its
/// outbox event commit together or not at all.
/// </para>
/// <para>
/// Every read and write that concerns a conversation checks that the user is an active participant, and treats a
/// missing conversation and a non-participant identically, so outsiders cannot discover private conversations.
/// </para>
/// <para>
/// Soga ships two implementations: the in-memory development store and the Entity Framework Core store. Both must
/// produce the same results.
/// </para>
/// </remarks>
internal interface ISogaStore
{
    /// <summary>
    /// Gets a value indicating whether data survives a process restart.
    /// </summary>
    /// <value><see langword="false"/> for the in-memory store; <see langword="true"/> for a database store.</value>
    bool IsDurable { get; }

    /// <summary>
    /// Creates a direct conversation, or returns the existing one for the same user pair and external reference.
    /// </summary>
    /// <param name="command">The users, external reference, and new IDs.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the conversation and whether it was
    /// created.
    /// </returns>
    /// <remarks>Uniqueness must hold even when two requests race to create the same conversation.</remarks>
    Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a group with the acting user as its owner.
    /// </summary>
    /// <param name="command">The title, participants, and new IDs.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the new group.</returns>
    Task<ConversationRecord> CreateGroupAsync(CreateGroupCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a membership, role, details, or archive change to a group.
    /// </summary>
    /// <param name="command">The group, the acting user, and the requested change.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result describes the outcome.</returns>
    /// <remarks>
    /// The store locks the conversation, loads a <c>GroupSnapshot</c>, lets <c>GroupRules</c> decide, and applies the
    /// result in one transaction, so concurrent changes are serialized.
    /// </remarks>
    Task<GroupMutationOutcome> MutateGroupAsync(GroupMutationCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a conversation, with its participants, for one of its active participants.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user requesting the conversation.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the conversation, or
    /// <see langword="null"/> when it does not exist or the user is not an active participant.
    /// </returns>
    Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the user's role and the conversation's kind, without loading participants or messages.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the access information, or
    /// <see langword="null"/> when the user is not an active participant.
    /// </returns>
    Task<ConversationAccess?> GetAccessAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists a user's conversations, most recent activity first.
    /// </summary>
    /// <param name="query">The user, filters, and paging position.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains one page of conversations. Group
    /// participant lists are not loaded, to keep the query cheap.
    /// </returns>
    Task<ConversationPage> ListConversationsAsync(ListConversationsQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the IDs of the conversations a user is an active participant of, most recent activity first.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="limit">The largest number of IDs to return.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the conversation IDs.</returns>
    Task<IReadOnlyList<Guid>> ListActiveConversationIdsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new message with the next conversation sequence, or returns the stored message for a retry.
    /// </summary>
    /// <param name="command">The sender, conversation, idempotency key, text, and new IDs.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result describes the outcome.</returns>
    /// <remarks>
    /// Sequences must be gapless and unique per conversation, even under concurrent sends (ADR 0006). The idempotency
    /// key is unique per tenant, conversation, and sender.
    /// </remarks>
    Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the text of a message, applying <c>MessageRules.CanEdit</c>.
    /// </summary>
    /// <param name="command">The editor, message, new text, and edit rules.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result describes the outcome.</returns>
    Task<EditMessageOutcome> EditMessageAsync(EditMessageCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a message, applying <c>MessageRules.CanDelete</c>, and leaves a tombstone that keeps its sequence.
    /// </summary>
    /// <param name="command">The actor, message, and deletion rules.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result describes the outcome.</returns>
    Task<DeleteMessageOutcome> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Gets messages older than a sequence, newest first.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user requesting the history.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="beforeSequence">Only messages with a lower sequence are returned, or <see langword="null"/> for the newest.</param>
    /// <param name="limit">The largest number of messages to return.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the messages, with deleted ones as
    /// tombstones, or <see langword="null"/> when the user cannot access the conversation.
    /// </returns>
    Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Advances a participant's read position through a message. The position never moves backward.
    /// </summary>
    /// <param name="command">The reader, conversation, and message read.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result describes the outcome.</returns>
    Task<AdvanceReadOutcome> AdvanceReadPositionAsync(
        AdvanceReadCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a user's total number of unread messages across all their conversations.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the unread count.</returns>
    Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the newest committed change sequence in a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the sequence, or 0 if there is none.</returns>
    Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the changes after a sequence that a user is allowed to see.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="afterSequence">Only changes with a higher sequence are returned.</param>
    /// <param name="limit">The largest number of changes to return.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains changes, in sequence order, for
    /// conversations the user can access now, plus any change addressed directly to the user, so a removed participant
    /// still learns they were removed.
    /// </returns>
    Task<ChangePage> GetChangesAsync(
        string tenantId,
        string userId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leases outbox events that are due, across all tenants, and increments their attempt count.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="batchSize">The largest number of events to claim.</param>
    /// <param name="leaseDuration">How long the claim lasts before another dispatcher may take the events.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the claimed events.</returns>
    /// <remarks>Competing dispatchers must never claim the same event at the same time.</remarks>
    Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(
        DateTimeOffset now,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a claimed outbox event as published.
    /// </summary>
    /// <param name="message">The claimed event.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Releases a claimed outbox event that failed to publish.
    /// </summary>
    /// <param name="message">The claimed event.</param>
    /// <param name="retryAt">When to retry, or <see langword="null"/> to mark the event as failed for good.</param>
    /// <param name="now">The current time.</param>
    /// <param name="failureCode">The exception type name. Never the exception message, which may hold private data.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task FailOutboxAsync(
        OutboxMessage message,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        string failureCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes outbox events that were published before a given time.
    /// </summary>
    /// <param name="publishedBefore">Events published before this time are deleted.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the number of events deleted.</returns>
    Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken);

    /// <summary>
    /// Gets counts that describe the outbox backlog, for the health check.
    /// </summary>
    /// <param name="now">The current time, used to compute the age of the oldest pending event.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the statistics.</returns>
    Task<OutboxStats> GetOutboxStatsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Checks that the persistence dependency is reachable.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="true"/> if the store can be
    /// reached; otherwise, <see langword="false"/>.
    /// </returns>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
}
