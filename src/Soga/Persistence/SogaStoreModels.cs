using Soga.Contracts;

namespace Soga.Persistence;

internal sealed record ConversationRecord(
    Guid Id,
    string? ExternalReference,
    IReadOnlyList<string> ParticipantIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    long LastMessageSequence,
    long LastReadSequence,
    long UnreadCount);

internal sealed record MessageRecord(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    string SenderId,
    string ClientMessageId,
    string Content,
    DateTimeOffset CreatedAt);

internal sealed record CreateDirectConversationCommand(
    string TenantId,
    string UserId,
    string OtherUserId,
    string? ExternalReference,
    Guid ConversationId,
    Guid EventId,
    DateTimeOffset Now);

internal sealed record DirectConversationOutcome(ConversationRecord Conversation, bool Created);

internal sealed record AppendMessageCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    string ClientMessageId,
    string Content,
    Guid MessageId,
    Guid EventId,
    DateTimeOffset Now);

internal enum AppendStatus
{
    Created,
    Duplicate,
    IdempotencyConflict,
    ConversationNotFound,
}

internal sealed record AppendMessageOutcome(AppendStatus Status, MessageRecord? Message);

internal sealed record AdvanceReadCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    Guid MessageId,
    Guid EventId,
    DateTimeOffset Now);

internal enum AdvanceStatus
{
    Advanced,
    Unchanged,
    ConversationNotFound,
    MessageNotFound,
}

internal sealed record AdvanceReadOutcome(AdvanceStatus Status, long LastReadSequence, long UnreadCount);

internal sealed record ChangeRecord(long Sequence, string Payload);

internal sealed record ChangePage(IReadOnlyList<ChangeRecord> Changes, bool HasMore);

internal sealed record OutboxMessage(Guid Id, string TenantId, string EventType, string Payload, int Attempt);

internal sealed record OutboxStats(long Pending, TimeSpan? OldestPendingAge, long Failed);

internal static class SogaMappers
{
    public const string ConversationKind = "Direct";
    public const string MessageKind = "Text";

    public static MessageResponse ToResponse(this MessageRecord message) => new(
        message.Id,
        message.ConversationId,
        message.Sequence,
        message.SenderId,
        message.ClientMessageId,
        MessageKind,
        message.Content,
        message.CreatedAt);

    public static ConversationResponse ToResponse(this ConversationRecord conversation) => new(
        conversation.Id,
        ConversationKind,
        conversation.ParticipantIds,
        conversation.ExternalReference,
        conversation.CreatedAt,
        conversation.LastActivityAt,
        conversation.LastMessageSequence,
        conversation.LastReadSequence,
        conversation.UnreadCount);
}
