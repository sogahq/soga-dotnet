namespace Soga.EntityFrameworkCore.Entities;

internal sealed class ConversationRow
{
    public Guid Id { get; set; }
    public required string TenantId { get; set; }
    public long NextSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
}

internal sealed class ParticipantRow
{
    public Guid ConversationId { get; set; }
    public required string TenantId { get; set; }
    public required string UserId { get; set; }
    public long LastReadSequence { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

internal sealed class MessageRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string TenantId { get; set; }
    public required string SenderId { get; set; }
    public required string ClientMessageId { get; set; }
    public required string Content { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class OutboxRow
{
    public Guid Id { get; set; }
    public required string TenantId { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
}
