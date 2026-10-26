using System.Security.Cryptography;
using System.Text;
using Soga.Abstractions;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;

namespace Soga.Persistence;

/// <summary>
/// Represents one active participant of a conversation.
/// </summary>
/// <param name="UserId">The participant's user ID.</param>
/// <param name="Role">The participant's role.</param>
/// <param name="JoinedAt">When the participant joined.</param>
internal sealed record ParticipantInfo(string UserId, SogaRole Role, DateTimeOffset JoinedAt);

/// <summary>
/// Represents the last-message preview shown in a conversation list.
/// </summary>
/// <param name="Id">The message ID.</param>
/// <param name="Sequence">The message sequence.</param>
/// <param name="SenderId">The sender's user ID.</param>
/// <param name="Kind">The message kind, from <see cref="SogaMessageKinds"/>.</param>
/// <param name="Snippet">The start of the text, at most <see cref="SogaText.SnippetLength"/> characters.</param>
/// <param name="CreatedAt">When the message was sent.</param>
/// <param name="Deleted"><see langword="true"/> if the message has been deleted.</param>
internal sealed record MessageSummary(
    Guid Id,
    long Sequence,
    string SenderId,
    string Kind,
    string Snippet,
    DateTimeOffset CreatedAt,
    bool Deleted);

/// <summary>
/// Represents the bounded summary of the message a reply refers to.
/// </summary>
/// <param name="MessageId">The ID of the original message.</param>
/// <param name="SenderId">The sender of the original message.</param>
/// <param name="Snippet">The start of the original text.</param>
/// <param name="Deleted"><see langword="true"/> if the original message has since been deleted.</param>
internal sealed record ReplyInfo(Guid MessageId, string SenderId, string Snippet, bool Deleted);

/// <summary>
/// Represents a conversation as seen by one participant.
/// </summary>
/// <param name="Id">The conversation ID.</param>
/// <param name="Kind">The conversation kind, from <see cref="SogaConversationKinds"/>.</param>
/// <param name="Title">The group title, or <see langword="null"/> for a direct conversation.</param>
/// <param name="ExternalReference">The host reference the conversation is about, if any.</param>
/// <param name="MetadataJson">The canonical metadata JSON, if any.</param>
/// <param name="Participants">The active participants. May be empty when a list query did not load them.</param>
/// <param name="ParticipantCount">The number of active participants.</param>
/// <param name="CreatedAt">When the conversation was created.</param>
/// <param name="LastActivityAt">When the last message or change happened, used for ordering lists.</param>
/// <param name="ArchivedAt">When the group was archived, or <see langword="null"/> if it is active.</param>
/// <param name="LastMessageSequence">The sequence of the newest message, or 0 if there are none.</param>
/// <param name="LastReadSequence">The viewer's read position.</param>
/// <param name="UnreadCount">The number of messages after the viewer's read position.</param>
/// <param name="MyRole">The viewer's role.</param>
/// <param name="LastMessage">The preview of the newest message, if any.</param>
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
    /// <summary>Gets a value indicating whether the conversation is a group.</summary>
    public bool IsGroup => Kind == SogaConversationKinds.Group;

    /// <summary>Gets the user IDs of the loaded participants.</summary>
    public IReadOnlyList<string> ParticipantIds => [.. Participants.Select(p => p.UserId)];
}

/// <summary>
/// Represents a stored message.
/// </summary>
/// <param name="Id">The message ID.</param>
/// <param name="ConversationId">The ID of the conversation.</param>
/// <param name="Sequence">The message's position in the conversation. It never changes, even after an edit or delete.</param>
/// <param name="SenderId">The sender's user ID.</param>
/// <param name="ClientMessageId">The sender's idempotency key.</param>
/// <param name="Content">The text. Empty once the message is deleted.</param>
/// <param name="CreatedAt">When the message was accepted.</param>
/// <param name="Kind">The message kind, from <see cref="SogaMessageKinds"/>.</param>
/// <param name="EditedAt">When the message was last edited, if ever.</param>
/// <param name="DeletedAt">When the message was deleted, if it was.</param>
/// <param name="DeletedBy">Who deleted the message, if it was deleted.</param>
/// <param name="ReplyToMessageId">The ID of the message this one replies to, if any.</param>
/// <param name="ReplyTo">The summary of the message this one replies to, if loaded.</param>
/// <param name="SentFingerprint">
/// The <see cref="SogaText.Fingerprint"/> of the text as first sent, used to recognize retries. Never returned to clients.
/// </param>
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
    ReplyInfo? ReplyTo = null,
    string? SentFingerprint = null)
{
    /// <summary>Gets a value indicating whether the message has been deleted.</summary>
    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>
/// Represents a light membership check: the caller's role and the conversation's kind, without participants or
/// messages.
/// </summary>
/// <param name="Kind">The conversation kind, from <see cref="SogaConversationKinds"/>.</param>
/// <param name="Role">The caller's role.</param>
/// <param name="Archived"><see langword="true"/> if the conversation is archived.</param>
/// <param name="ExternalReference">The host reference the conversation is about, if any.</param>
internal sealed record ConversationAccess(string Kind, SogaRole Role, bool Archived, string? ExternalReference)
{
    /// <summary>Gets a value indicating whether the conversation is a group.</summary>
    public bool IsGroup => Kind == SogaConversationKinds.Group;
}

/// <summary>
/// Specifies which conversations a list query returns.
/// </summary>
internal enum ConversationStateFilter
{
    /// <summary>Only conversations that are not archived.</summary>
    Active,

    /// <summary>Only archived conversations.</summary>
    Archived,

    /// <summary>Every conversation.</summary>
    All,
}

/// <summary>
/// Represents a conversation list query.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The user whose conversations are listed.</param>
/// <param name="Limit">The page size.</param>
/// <param name="Kind">An optional kind filter, from <see cref="SogaConversationKinds"/>.</param>
/// <param name="State">The archive state filter.</param>
/// <param name="ExternalReference">An optional external reference filter.</param>
/// <param name="AfterActivity">Continue with conversations at or before this activity time, or <see langword="null"/> for the first page.</param>
/// <param name="SkipTies">How many conversations with exactly <paramref name="AfterActivity"/> to skip, because they were already returned.</param>
internal sealed record ListConversationsQuery(
    string TenantId,
    string UserId,
    int Limit,
    string? Kind,
    ConversationStateFilter State,
    string? ExternalReference,
    DateTimeOffset? AfterActivity,
    int SkipTies);

/// <summary>
/// Represents one page of conversations.
/// </summary>
/// <param name="Items">The conversations on the page.</param>
/// <param name="HasMore"><see langword="true"/> if more conversations follow.</param>
internal sealed record ConversationPage(IReadOnlyList<ConversationRecord> Items, bool HasMore);

/// <summary>
/// Represents a request to create or get a direct conversation.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The acting user.</param>
/// <param name="OtherUserId">The other participant.</param>
/// <param name="ExternalReference">The host reference the conversation is about, if any.</param>
/// <param name="ConversationId">The ID to use if a new conversation is created.</param>
/// <param name="EventId">The ID of the <c>ConversationCreated</c> event, if one is created.</param>
/// <param name="Now">The current time.</param>
/// <param name="MetadataJson">The canonical metadata JSON for a new conversation, if any.</param>
internal sealed record CreateDirectConversationCommand(
    string TenantId,
    string UserId,
    string OtherUserId,
    string? ExternalReference,
    Guid ConversationId,
    Guid EventId,
    DateTimeOffset Now,
    string? MetadataJson = null);

/// <summary>
/// Represents the result of creating or getting a direct conversation.
/// </summary>
/// <param name="Conversation">The conversation.</param>
/// <param name="Created"><see langword="true"/> if it was created; <see langword="false"/> if it already existed.</param>
internal sealed record DirectConversationOutcome(ConversationRecord Conversation, bool Created);

/// <summary>
/// Represents a request to create a group.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The acting user, who becomes the owner.</param>
/// <param name="Title">The group title.</param>
/// <param name="OtherParticipantIds">The other initial participants, who join as members.</param>
/// <param name="ExternalReference">The host reference the group is about, if any.</param>
/// <param name="MetadataJson">The canonical metadata JSON, if any.</param>
/// <param name="ConversationId">The ID of the new group.</param>
/// <param name="EventId">The ID of the <c>ConversationCreated</c> event.</param>
/// <param name="Now">The current time.</param>
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

/// <summary>
/// Represents a request to change a group.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="ActorId">The user making the change.</param>
/// <param name="ConversationId">The ID of the group.</param>
/// <param name="Mutation">The requested change.</param>
/// <param name="MaximumParticipants">The largest number of active participants allowed.</param>
/// <param name="PostSystemMessage"><see langword="true"/> to post a system message for membership changes.</param>
/// <param name="EventId">The ID of the first event the change publishes.</param>
/// <param name="Ids">Generates IDs for any further events and system messages.</param>
/// <param name="Now">The current time.</param>
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

/// <summary>
/// Represents the result of a group change.
/// </summary>
/// <param name="Error">Why the change was refused, or <see cref="GroupError.None"/>.</param>
/// <param name="Changed"><see langword="true"/> if anything was written.</param>
/// <param name="Conversation">
/// The group after the change, or <see langword="null"/> when the actor no longer has access, for example after leaving.
/// </param>
internal sealed record GroupMutationOutcome(GroupError Error, bool Changed, ConversationRecord? Conversation);

/// <summary>
/// Represents a request to store a message.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The sender.</param>
/// <param name="ConversationId">The ID of the conversation.</param>
/// <param name="ClientMessageId">The sender's idempotency key.</param>
/// <param name="Content">The text.</param>
/// <param name="MessageId">The ID to use if a new message is stored.</param>
/// <param name="EventId">The ID of the <c>MessageCreated</c> event.</param>
/// <param name="Now">The current time.</param>
/// <param name="ReplyToMessageId">The ID of the message being replied to, if any.</param>
/// <param name="IsSystem"><see langword="true"/> for a system message, which skips the membership check.</param>
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

/// <summary>
/// Specifies the result of storing a message.
/// </summary>
internal enum AppendStatus
{
    /// <summary>A new message was stored.</summary>
    Created,

    /// <summary>The same request was already accepted; the stored message is returned.</summary>
    Duplicate,

    /// <summary>The client message ID was already used for different text.</summary>
    IdempotencyConflict,

    /// <summary>The conversation does not exist or the sender is not a participant.</summary>
    ConversationNotFound,

    /// <summary>The conversation is archived.</summary>
    Archived,

    /// <summary>The reply target is missing, deleted, or in another conversation.</summary>
    ReplyInvalid,
}

/// <summary>
/// Represents the result of storing a message.
/// </summary>
/// <param name="Status">The result.</param>
/// <param name="Message">The stored message, for <see cref="AppendStatus.Created"/> and <see cref="AppendStatus.Duplicate"/>.</param>
internal sealed record AppendMessageOutcome(AppendStatus Status, MessageRecord? Message);

/// <summary>
/// Represents a request to edit a message.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The editor.</param>
/// <param name="ConversationId">The ID of the conversation.</param>
/// <param name="MessageId">The ID of the message.</param>
/// <param name="Text">The new text.</param>
/// <param name="EventId">The ID of the <c>MessageUpdated</c> event.</param>
/// <param name="Ids">Generates the ID of the edit-history record.</param>
/// <param name="Now">The current time.</param>
/// <param name="EditWindow">How long after sending a message can be edited, or <see langword="null"/> for no limit.</param>
/// <param name="RetainHistory"><see langword="true"/> to keep the previous text.</param>
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

/// <summary>
/// Specifies the result of editing a message.
/// </summary>
internal enum EditStatus
{
    /// <summary>The text was changed.</summary>
    Updated,

    /// <summary>The new text equals the current text; nothing was written.</summary>
    Unchanged,

    /// <summary>The conversation does not exist or the editor is not a participant.</summary>
    ConversationNotFound,

    /// <summary>The message does not exist in the conversation.</summary>
    MessageNotFound,

    /// <summary>The editor is not the sender.</summary>
    Forbidden,

    /// <summary>The message is deleted or is a system message.</summary>
    NotEditable,

    /// <summary>The edit window has passed.</summary>
    WindowExpired,
}

/// <summary>
/// Represents the result of editing a message.
/// </summary>
/// <param name="Status">The result.</param>
/// <param name="Message">The message after the edit, for <see cref="EditStatus.Updated"/> and <see cref="EditStatus.Unchanged"/>.</param>
internal sealed record EditMessageOutcome(EditStatus Status, MessageRecord? Message);

/// <summary>
/// Represents a request to delete a message.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The user deleting the message.</param>
/// <param name="ConversationId">The ID of the conversation.</param>
/// <param name="MessageId">The ID of the message.</param>
/// <param name="AllowOwnDeletion"><see langword="true"/> if senders may delete their own messages.</param>
/// <param name="AllowAdminDeletion"><see langword="true"/> if group owners and admins may delete others' messages.</param>
/// <param name="EventId">The ID of the <c>MessageDeleted</c> event.</param>
/// <param name="Now">The current time.</param>
internal sealed record DeleteMessageCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    Guid MessageId,
    bool AllowOwnDeletion,
    bool AllowAdminDeletion,
    Guid EventId,
    DateTimeOffset Now);

/// <summary>
/// Specifies the result of deleting a message.
/// </summary>
internal enum DeleteStatus
{
    /// <summary>The message was deleted.</summary>
    Deleted,

    /// <summary>The message was already deleted; nothing was written.</summary>
    AlreadyDeleted,

    /// <summary>The conversation does not exist or the user is not a participant.</summary>
    ConversationNotFound,

    /// <summary>The message does not exist in the conversation.</summary>
    MessageNotFound,

    /// <summary>The user may not delete this message.</summary>
    Forbidden,

    /// <summary>The message is a system message.</summary>
    NotDeletable,
}

/// <summary>
/// Represents the result of deleting a message.
/// </summary>
/// <param name="Status">The result.</param>
/// <param name="ByAdministrator">
/// <see langword="true"/> if someone other than the sender deleted the message, so the workflow can audit it.
/// </param>
/// <param name="SenderId">The sender of the deleted message, if it was found.</param>
internal sealed record DeleteMessageOutcome(DeleteStatus Status, bool ByAdministrator, string? SenderId);

/// <summary>
/// Represents a request to advance a read position.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="UserId">The reader.</param>
/// <param name="ConversationId">The ID of the conversation.</param>
/// <param name="MessageId">The ID of the last message read.</param>
/// <param name="EventId">The ID of the <c>ReadPositionChanged</c> event.</param>
/// <param name="Now">The current time.</param>
internal sealed record AdvanceReadCommand(
    string TenantId,
    string UserId,
    Guid ConversationId,
    Guid MessageId,
    Guid EventId,
    DateTimeOffset Now);

/// <summary>
/// Specifies the result of advancing a read position.
/// </summary>
internal enum AdvanceStatus
{
    /// <summary>The read position moved forward.</summary>
    Advanced,

    /// <summary>The message is at or before the current position; nothing was written.</summary>
    Unchanged,

    /// <summary>The conversation does not exist or the reader is not a participant.</summary>
    ConversationNotFound,

    /// <summary>The message does not exist in the conversation.</summary>
    MessageNotFound,
}

/// <summary>
/// Represents the result of advancing a read position.
/// </summary>
/// <param name="Status">The result.</param>
/// <param name="LastReadSequence">The read position after the request.</param>
/// <param name="UnreadCount">The unread count after the request.</param>
internal sealed record AdvanceReadOutcome(AdvanceStatus Status, long LastReadSequence, long UnreadCount);

/// <summary>
/// Represents one entry in the change log.
/// </summary>
/// <param name="Sequence">The entry's position in the tenant's change log.</param>
/// <param name="Payload">The serialized event.</param>
internal sealed record ChangeRecord(long Sequence, string Payload);

/// <summary>
/// Represents one page of the change log.
/// </summary>
/// <param name="Changes">The changes, in sequence order.</param>
/// <param name="HasMore"><see langword="true"/> if more changes follow.</param>
internal sealed record ChangePage(IReadOnlyList<ChangeRecord> Changes, bool HasMore);

/// <summary>
/// Represents an outbox event claimed by a dispatcher.
/// </summary>
/// <param name="Id">The outbox record ID.</param>
/// <param name="TenantId">The tenant of the event.</param>
/// <param name="EventType">The event type.</param>
/// <param name="Payload">The serialized event.</param>
/// <param name="Attempt">The attempt number of this claim, starting at 1.</param>
internal sealed record OutboxMessage(Guid Id, string TenantId, string EventType, string Payload, int Attempt);

/// <summary>
/// Represents the state of the outbox backlog.
/// </summary>
/// <param name="Pending">The number of events waiting to be published.</param>
/// <param name="OldestPendingAge">How long the oldest pending event has waited, or <see langword="null"/> if none is pending.</param>
/// <param name="Failed">The number of events that failed for good.</param>
internal sealed record OutboxStats(long Pending, TimeSpan? OldestPendingAge, long Failed);

/// <summary>
/// Provides text helpers shared by the stores.
/// </summary>
internal static class SogaText
{
    /// <summary>
    /// The maximum number of characters in a message preview.
    /// </summary>
    public const int SnippetLength = 100;

    /// <summary>
    /// Creates a bounded preview of message text.
    /// </summary>
    /// <param name="content">The full text.</param>
    /// <returns>
    /// The first <see cref="SnippetLength"/> characters of <paramref name="content"/>, shortened by one if needed so a
    /// surrogate pair is never split.
    /// </returns>
    public static string Snippet(string content)
    {
        if (content.Length <= SnippetLength)
        {
            return content;
        }

        var length = char.IsHighSurrogate(content[SnippetLength - 1]) ? SnippetLength - 1 : SnippetLength;
        return content[..length];
    }

    /// <summary>
    /// Computes the fingerprint of message text as it was first sent.
    /// </summary>
    /// <param name="content">The text exactly as the sender submitted it.</param>
    /// <returns>The upper-case hexadecimal SHA-256 hash of the UTF-8 text, 64 characters long.</returns>
    /// <remarks>
    /// The fingerprint is stored with the message and never changes when the message is edited, so a retry of the
    /// original send is still recognized after an edit (ADR 0011). It is erased together with the text on deletion.
    /// </remarks>
    public static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Determines whether a send with an already used client message ID repeats the original request.
    /// </summary>
    /// <param name="storedFingerprint">The fingerprint stored with the message, or <see langword="null"/> for a message stored before fingerprints existed.</param>
    /// <param name="storedContent">The message's current text, used only when there is no fingerprint.</param>
    /// <param name="retryContent">The text of the new request.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="retryContent"/> is exactly the text originally sent; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsSameSend(string? storedFingerprint, string storedContent, string retryContent) =>
        storedFingerprint is not null
            ? string.Equals(storedFingerprint, Fingerprint(retryContent), StringComparison.Ordinal)
            : string.Equals(storedContent, retryContent, StringComparison.Ordinal);
}

/// <summary>
/// Defines the reserved sender of system messages.
/// </summary>
/// <remarks>
/// Participants can never send as this user: the sender always comes from the authenticated principal, and system
/// messages are only created by trusted code through <c>ISogaSystemMessages</c>.
/// </remarks>
internal static class SogaSystemSender
{
    /// <summary>
    /// The user ID that system messages are sent as.
    /// </summary>
    public const string UserId = "$soga:system";
}

/// <summary>
/// Maps store records to public protocol contracts.
/// </summary>
/// <remarks>Store records are never returned to clients directly, so storage can change without changing the protocol.</remarks>
internal static class SogaMappers
{
    /// <summary>
    /// Maps a stored message to its protocol contract.
    /// </summary>
    /// <param name="message">The stored message.</param>
    /// <returns>The message contract. A deleted message, or a deleted reply target, has empty text.</returns>
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

    /// <summary>
    /// Maps a last-message preview to its protocol contract.
    /// </summary>
    /// <param name="summary">The preview.</param>
    /// <returns>The preview contract. A deleted message has an empty snippet.</returns>
    public static MessageSummaryResponse ToResponse(this MessageSummary summary) => new(
        summary.Id,
        summary.Sequence,
        summary.SenderId,
        summary.Kind,
        summary.Deleted ? string.Empty : summary.Snippet,
        summary.CreatedAt,
        summary.Deleted);

    /// <summary>
    /// Maps a conversation record to its protocol contract.
    /// </summary>
    /// <param name="conversation">The conversation record.</param>
    /// <returns>The conversation contract.</returns>
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
