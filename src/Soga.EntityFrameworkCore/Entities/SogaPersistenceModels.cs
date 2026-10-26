using Soga.Groups;

namespace Soga.EntityFrameworkCore.Entities;

internal sealed class ConversationRow
{
    public Guid Id { get; set; }
    public required string TenantId { get; set; }

    /// <summary>
    /// For direct conversations, the SHA-256 of the unordered user pair and external reference; a unique index makes
    /// creation concurrency-safe. For groups, <c>g:{id}</c>, which is unique by construction.
    /// </summary>
    public required string DirectKey { get; set; }

    /// <summary><c>Direct</c> or <c>Group</c>.</summary>
    public required string Kind { get; set; }

    public string? Title { get; set; }
    public string? ExternalReference { get; set; }

    /// <summary>Canonical flat JSON (sorted keys), validated and bounded before it is stored. Never sent in events.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>The last allocated message sequence. Advanced atomically inside the sending transaction.</summary>
    public long LastSequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

internal sealed class ParticipantRow
{
    public Guid ConversationId { get; set; }
    public required string TenantId { get; set; }
    public required string UserId { get; set; }
    public SogaRole Role { get; set; }
    public long LastReadSequence { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>Set when the participant left or was removed. The row is kept so the change is auditable and re-adding is idempotent.</summary>
    public DateTimeOffset? LeftAt { get; set; }
}

internal sealed class MessageRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string TenantId { get; set; }
    public required string SenderId { get; set; }
    public required string ClientMessageId { get; set; }

    /// <summary><c>Text</c> or <c>System</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The text. Erased (empty) when the message is deleted, so deleted content is never returned again.</summary>
    public required string Content { get; set; }

    public long Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public Guid? ReplyToMessageId { get; set; }
}

/// <summary>The previous text of an edited message. Only written when <c>Messages:RetainEditHistory</c> is on; erased when the message is deleted.</summary>
internal sealed class MessageEditRow
{
    public required string TenantId { get; set; }
    public Guid MessageId { get; set; }
    public int Version { get; set; }
    public required string PreviousContent { get; set; }
    public DateTimeOffset EditedAt { get; set; }
}

/// <summary>The durable, ordered change stream that reconnecting clients synchronize from.</summary>
internal sealed class ChangeRow
{
    public required string TenantId { get; set; }

    /// <summary>A per-tenant sequence allocated under a row lock, so commit order equals sequence order and a cursor can never skip a change.</summary>
    public long Sequence { get; set; }

    public Guid EventId { get; set; }
    public required string ChangeType { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>When set, the change is also visible to this user even if they no longer have access, so a removed participant learns they were removed.</summary>
    public string? TargetUserId { get; set; }

    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class TenantStateRow
{
    public required string TenantId { get; set; }
    public long LastChangeSequence { get; set; }
}

internal sealed class OutboxRow
{
    public Guid Id { get; set; }
    public required string TenantId { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>The failure exception type name only. Message text is never stored because it may contain private content.</summary>
    public string? LastFailure { get; set; }
}
