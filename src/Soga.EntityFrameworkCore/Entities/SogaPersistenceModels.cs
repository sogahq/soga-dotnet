using Soga.Groups;

namespace Soga.EntityFrameworkCore.Entities;

/// <summary>
/// Represents a row in the <c>SogaConversations</c> table.
/// </summary>
internal sealed class ConversationRow
{
    /// <summary>Gets or sets the conversation ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns the conversation.</summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// Gets or sets the uniqueness key of the conversation.
    /// </summary>
    /// <remarks>
    /// For a direct conversation, the SHA-256 of the unordered user pair and external reference; a unique index makes
    /// creation concurrency-safe. For a group, <c>g:{id}</c>, which is unique by construction.
    /// </remarks>
    public required string DirectKey { get; set; }

    /// <summary>Gets or sets the conversation kind: <c>Direct</c> or <c>Group</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>Gets or sets the group title, or <see langword="null"/> for a direct conversation.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the host reference the conversation is about, such as an order ID.</summary>
    public string? ExternalReference { get; set; }

    /// <summary>
    /// Gets or sets the canonical flat metadata JSON, with keys sorted, validated and bounded before it is stored.
    /// </summary>
    /// <remarks>Metadata is never sent in events.</remarks>
    public string? MetadataJson { get; set; }

    /// <summary>
    /// Gets or sets the last allocated message sequence.
    /// </summary>
    /// <remarks>It is advanced atomically inside the sending transaction, under a row lock (ADR 0006).</remarks>
    public long LastSequence { get; set; }

    /// <summary>Gets or sets when the conversation was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets when the last message was added, used to order conversation lists.</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Gets or sets when the group was archived, or <see langword="null"/> if it is active.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// Represents a row in the <c>SogaParticipants</c> table.
/// </summary>
internal sealed class ParticipantRow
{
    /// <summary>Gets or sets the ID of the conversation.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>Gets or sets the tenant that owns the conversation.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the participant's user ID. Compared case-sensitively.</summary>
    public required string UserId { get; set; }

    /// <summary>Gets or sets the participant's role, stored as its name.</summary>
    public SogaRole Role { get; set; }

    /// <summary>Gets or sets the participant's read position: the sequence of the last message they read.</summary>
    public long LastReadSequence { get; set; }

    /// <summary>Gets or sets when the read position last moved.</summary>
    public DateTimeOffset? LastReadAt { get; set; }

    /// <summary>Gets or sets when the participant last joined.</summary>
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>
    /// Gets or sets when the participant left or was removed, or <see langword="null"/> if they are active.
    /// </summary>
    /// <remarks>The row is kept so the change is auditable and re-adding the user is idempotent.</remarks>
    public DateTimeOffset? LeftAt { get; set; }
}

/// <summary>
/// Represents a row in the <c>SogaMessages</c> table.
/// </summary>
internal sealed class MessageRow
{
    /// <summary>Gets or sets the message ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the ID of the conversation.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>Gets or sets the tenant that owns the conversation.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the sender's user ID.</summary>
    public required string SenderId { get; set; }

    /// <summary>Gets or sets the sender's idempotency key, unique per tenant, conversation, and sender.</summary>
    public required string ClientMessageId { get; set; }

    /// <summary>Gets or sets the message kind: <c>Text</c> or <c>System</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>
    /// Gets or sets the message text.
    /// </summary>
    /// <remarks>It is erased (set to empty) when the message is deleted, so deleted content is never returned again.</remarks>
    public required string Content { get; set; }

    /// <summary>Gets or sets the message's position in the conversation, unique and gapless.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets when the message was accepted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets when the message was last edited.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>Gets or sets when the message was deleted.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Gets or sets who deleted the message.</summary>
    public string? DeletedBy { get; set; }

    /// <summary>Gets or sets the ID of the message this one replies to.</summary>
    public Guid? ReplyToMessageId { get; set; }
}

/// <summary>
/// Represents a row in the <c>SogaMessageEdits</c> table: the previous text of an edited message.
/// </summary>
/// <remarks>
/// Rows are written only when <c>Messages:RetainEditHistory</c> is on, and are erased when the message is deleted.
/// </remarks>
internal sealed class MessageEditRow
{
    /// <summary>Gets or sets the tenant that owns the message.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the ID of the edited message.</summary>
    public Guid MessageId { get; set; }

    /// <summary>Gets or sets the version number, starting at 1 for the original text.</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets the text before the edit.</summary>
    public required string PreviousContent { get; set; }

    /// <summary>Gets or sets when the edit happened.</summary>
    public DateTimeOffset EditedAt { get; set; }
}

/// <summary>
/// Represents a row in the <c>SogaChanges</c> table: the durable, ordered change log that reconnecting clients
/// synchronize from.
/// </summary>
internal sealed class ChangeRow
{
    /// <summary>Gets or sets the tenant the change belongs to.</summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// Gets or sets the change's position in the tenant's change log.
    /// </summary>
    /// <remarks>
    /// The sequence is allocated under a row lock on <see cref="TenantStateRow"/>, so commit order equals sequence
    /// order and a sync cursor can never skip a change.
    /// </remarks>
    public long Sequence { get; set; }

    /// <summary>Gets or sets the event ID, shared with the outbox row so clients can deduplicate.</summary>
    public Guid EventId { get; set; }

    /// <summary>Gets or sets the event type.</summary>
    public required string ChangeType { get; set; }

    /// <summary>Gets or sets the ID of the conversation the change concerns.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Gets or sets a user who can see the change even without access to the conversation.
    /// </summary>
    /// <remarks>This is how a removed participant learns, through sync, that they were removed.</remarks>
    public string? TargetUserId { get; set; }

    /// <summary>Gets or sets the serialized event.</summary>
    public required string Payload { get; set; }

    /// <summary>Gets or sets when the change happened.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Represents a row in the <c>SogaTenantState</c> table, which holds per-tenant counters.
/// </summary>
internal sealed class TenantStateRow
{
    /// <summary>Gets or sets the tenant ID.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the last allocated change-log sequence for the tenant.</summary>
    public long LastChangeSequence { get; set; }
}

/// <summary>
/// Represents a row in the <c>SogaOutbox</c> table: an event waiting to be, or already, published to realtime clients.
/// </summary>
internal sealed class OutboxRow
{
    /// <summary>Gets or sets the event ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant of the event.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the event type.</summary>
    public required string EventType { get; set; }

    /// <summary>Gets or sets the serialized event.</summary>
    public required string Payload { get; set; }

    /// <summary>Gets or sets when the event happened.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Gets or sets the earliest time the event may next be claimed.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>Gets or sets when the current claim expires, or <see langword="null"/> if unclaimed.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Gets or sets how many times the event has been claimed.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets when the event was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Gets or sets when the event failed for good.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>
    /// Gets or sets the exception type name of the last failure.
    /// </summary>
    /// <remarks>Exception messages are never stored, because they may contain private content.</remarks>
    public string? LastFailure { get; set; }
}
