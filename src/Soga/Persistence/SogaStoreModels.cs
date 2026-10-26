using Soga.Abstractions;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;

namespace Soga.Persistence;

internal sealed record ParticipantInfo(string UserId, SogaRole Role, DateTimeOffset JoinedAt);

internal sealed record MessageSummary(
    Guid Id,
    long Sequence,
    string SenderId,
    string Kind,
    string Snippet,
    DateTimeOffset CreatedAt,
    bool Deleted);

internal sealed record ReplyInfo(Guid MessageId, string SenderId, string Snippet, bool Deleted);

internal sealed record ConversationRecord(
    Guid Id,
    string Kind,
    string? Title,
    string? ExternalReference,
    string? MetadataJson,
    IReadOnlyList<ParticipantInfo> Participants,
    int ParticipantCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? ArchivedAt,
    long LastMessageSequence,
    long LastReadSequence,
    long UnreadCount,
    SogaRole MyRole,
    MessageSummary? LastMessage)
{
    public bool IsGroup => Kind == SogaConversationKinds.Group;

    public IReadOnlyList<string> ParticipantIds => [.. Participants.Select(p => p.UserId)];
}

internal sealed record MessageRecord(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    string SenderId,
    string ClientMessageId,
    string Content,
    DateTimeOffset CreatedAt,
    string Kind = SogaMessageKinds.Text,
    DateTimeOffset? EditedAt = null,
    DateTimeOffset? DeletedAt = null,
    string? DeletedBy = null,
    Guid? ReplyToMessageId = null,
    ReplyInfo? ReplyTo = null)
{
    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>A light membership check: the caller's role and the conversation's kind, without loading participants or messages.</summary>
internal sealed record ConversationAccess(string Kind, SogaRole Role, bool Archived, string? ExternalReference)
{
    public bool IsGroup => Kind == SogaConversationKinds.Group;
}

internal enum ConversationStateFilter
{
    Active,
    Archived,
    All,
}

internal sealed record ListConversationsQuery(
    string TenantId,
    string UserId,
    int Limit,
    string? Kind,
    ConversationStateFilter State,
    string? ExternalReference,
    DateTimeOffset? AfterActivity,
    int SkipTies);

internal sealed record ConversationPage(IReadOnlyList<ConversationRecord> Items, bool HasMore);

internal sealed record CreateDirectConversationCommand(
    string TenantId,
    string UserId,
    string OtherUserId,
    string? ExternalReference,
    Guid ConversationId,
    Guid EventId,
    DateTimeOffset Now,
    string? MetadataJson = null);

internal sealed record DirectConversationOutcome(ConversationRecord Conversation, bool Created);

internal sealed record CreateGroupCommand(
    string TenantId,
    string UserId,
    string Title,
    IReadOnlyList<string> OtherParticipantIds,
    string? ExternalReference,
    string? MetadataJson,
    Guid ConversationId,
    Guid EventId,
    DateTimeOffset Now);

internal sealed record GroupMutationCommand(
    string TenantId,
    string ActorId,
    Guid ConversationId,
    GroupMutation Mutation,
    int MaximumParticipants,
    bool PostSystemMessage,
    Guid EventId,
    ISogaIdGenerator Ids,
    DateTimeOffset Now);

/// <summary><see cref="Conversation"/> is the state after the change, or <see langword="null"/> when the caller lost access.</summary>
internal sealed record GroupMutationOutcome(GroupError Error, bool Changed, ConversationRecord? Conversation);

internal sealed record AppendMessageCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    string ClientMessageId,
    string Content,
    Guid MessageId,
    Guid EventId,
    DateTimeOffset Now,
    Guid? ReplyToMessageId = null,
    bool IsSystem = false);

internal enum AppendStatus
{
    Created,
    Duplicate,
    IdempotencyConflict,
    ConversationNotFound,
    Archived,
    ReplyInvalid,
}

internal sealed record AppendMessageOutcome(AppendStatus Status, MessageRecord? Message);

internal sealed record EditMessageCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    Guid MessageId,
    string Text,
    Guid EventId,
    ISogaIdGenerator Ids,
    DateTimeOffset Now,
    TimeSpan? EditWindow,
    bool RetainHistory);

internal enum EditStatus
{
    Updated,
    Unchanged,
    ConversationNotFound,
    MessageNotFound,
    Forbidden,
    NotEditable,
    WindowExpired,
}

internal sealed record EditMessageOutcome(EditStatus Status, MessageRecord? Message);

internal sealed record DeleteMessageCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    Guid MessageId,
    bool AllowOwnDeletion,
    bool AllowAdminDeletion,
    Guid EventId,
    DateTimeOffset Now);

internal enum DeleteStatus
{
    Deleted,
    AlreadyDeleted,
    ConversationNotFound,
    MessageNotFound,
    Forbidden,
    NotDeletable,
}

/// <summary><see cref="ByAdministrator"/> is true when someone other than the sender deleted the message, so the workflow can audit it.</summary>
internal sealed record DeleteMessageOutcome(DeleteStatus Status, bool ByAdministrator, string? SenderId);

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

internal static class SogaText
{
    public const int SnippetLength = 100;

    /// <summary>Returns a bounded preview that never splits a surrogate pair.</summary>
    public static string Snippet(string content)
    {
        if (content.Length <= SnippetLength)
        {
            return content;
        }

        var length = char.IsHighSurrogate(content[SnippetLength - 1]) ? SnippetLength - 1 : SnippetLength;
        return content[..length];
    }
}

/// <summary>The reserved sender of system messages. Participants can never send as it: the sender always comes from the principal and system messages are only created by trusted code.</summary>
internal static class SogaSystemSender
{
    public const string UserId = "$soga:system";
}

internal static class SogaMappers
{
    public static MessageResponse ToResponse(this MessageRecord message) => new(
        message.Id,
        message.ConversationId,
        message.Sequence,
        message.SenderId,
        message.ClientMessageId,
        message.Kind,
        message.IsDeleted ? string.Empty : message.Content,
        message.CreatedAt,
        message.EditedAt,
        message.IsDeleted,
        message.ReplyTo is null
            ? null
            : new ReplySummaryResponse(
                message.ReplyTo.MessageId,
                message.ReplyTo.SenderId,
                message.ReplyTo.Deleted ? string.Empty : message.ReplyTo.Snippet,
                message.ReplyTo.Deleted));

    public static MessageSummaryResponse ToResponse(this MessageSummary summary) => new(
        summary.Id,
        summary.Sequence,
        summary.SenderId,
        summary.Kind,
        summary.Deleted ? string.Empty : summary.Snippet,
        summary.CreatedAt,
        summary.Deleted);

    public static ConversationResponse ToResponse(this ConversationRecord conversation) => new(
        conversation.Id,
        conversation.Kind,
        conversation.ParticipantIds,
        conversation.ExternalReference,
        conversation.CreatedAt,
        conversation.LastActivityAt,
        conversation.LastMessageSequence,
        conversation.LastReadSequence,
        conversation.UnreadCount,
        conversation.Title,
        SogaMetadata.Parse(conversation.MetadataJson),
        conversation.ArchivedAt,
        SogaRoles.ToWire(conversation.MyRole),
        conversation.ParticipantCount,
        [.. conversation.Participants.Select(p => new ParticipantResponse(p.UserId, SogaRoles.ToWire(p.Role), p.JoinedAt))],
        conversation.LastMessage?.ToResponse());
}
