using System.Text.Json;

namespace Soga.Contracts;

/// <summary>
/// Provides constants that describe the Soga wire protocol.
/// </summary>
/// <remarks>
/// Every request, response, and event in this namespace is part of the protocol. Changes within a version are
/// additive only, so clients must ignore fields, event types, and error codes they do not recognize (ADR 0010).
/// </remarks>
public static class SogaProtocol
{
    /// <summary>The current wire protocol version.</summary>
    public const int Version = 1;
}

/// <summary>Names of the realtime and synchronization event types. The realtime client method name equals the event type.</summary>
public static class SogaEventTypes
{
    /// <summary>A message was accepted into a conversation. Durable.</summary>
    public const string MessageCreated = "MessageCreated";

    /// <summary>A message was edited. Durable.</summary>
    public const string MessageUpdated = "MessageUpdated";

    /// <summary>A message was deleted. Durable.</summary>
    public const string MessageDeleted = "MessageDeleted";

    /// <summary>A participant's read position advanced. Durable.</summary>
    public const string ReadPositionChanged = "ReadPositionChanged";

    /// <summary>A conversation was created for the recipient. Durable.</summary>
    public const string ConversationCreated = "ConversationCreated";

    /// <summary>A group's title or metadata changed. Durable.</summary>
    public const string ConversationUpdated = "ConversationUpdated";

    /// <summary>A group was archived or unarchived. Durable.</summary>
    public const string ConversationArchived = "ConversationArchived";

    /// <summary>A participant was added, removed, left, or had their role changed. Durable.</summary>
    public const string ParticipantChanged = "ParticipantChanged";

    /// <summary>Reserved. Access changes are announced with <see cref="ParticipantChanged"/>.</summary>
    public const string ConversationAccessChanged = "ConversationAccessChanged";

    /// <summary>A participant started or stopped typing. Ephemeral: never stored and never recovered by sync.</summary>
    public const string TypingChanged = "TypingChanged";

    /// <summary>A participant went online or offline. Ephemeral and advisory: never stored and never grants access.</summary>
    public const string PresenceChanged = "PresenceChanged";
}

/// <summary>Participant role names used on the wire.</summary>
public static class SogaRoleNames
{
    /// <summary>An ordinary participant.</summary>
    public const string Member = "member";

    /// <summary>Can manage members and the group.</summary>
    public const string Admin = "admin";

    /// <summary>Full control. A group always keeps at least one owner.</summary>
    public const string Owner = "owner";
}

/// <summary>Message kind names used on the wire.</summary>
public static class SogaMessageKinds
{
    /// <summary>A message written by a participant.</summary>
    public const string Text = "Text";

    /// <summary>A message created by the server or the host. Participants cannot create, edit, or delete it.</summary>
    public const string System = "System";
}

/// <summary>Conversation kind names used on the wire.</summary>
public static class SogaConversationKinds
{
    /// <summary>A conversation between exactly two users.</summary>
    public const string Direct = "Direct";

    /// <summary>A conversation with several participants and roles.</summary>
    public const string Group = "Group";
}

/// <summary>Request to create or retrieve a direct conversation.</summary>
/// <param name="OtherUserId">The host user ID of the other participant.</param>
/// <param name="ExternalReference">An optional host reference such as an order or booking ID.</param>
/// <param name="Metadata">Optional flat metadata, used only when the conversation is created.</param>
public sealed record CreateDirectConversationRequest(
    string OtherUserId,
    string? ExternalReference = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary>Request to create a group. The caller becomes its owner.</summary>
/// <param name="Title">The group title.</param>
/// <param name="ParticipantUserIds">The other participants. They join as members.</param>
/// <param name="ExternalReference">An optional host reference such as a project or case ID.</param>
/// <param name="Metadata">Optional flat metadata.</param>
public sealed record CreateGroupRequest(
    string Title,
    IReadOnlyList<string> ParticipantUserIds,
    string? ExternalReference = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary>Request to update a group. A property left out is unchanged. Metadata replaces the whole document.</summary>
/// <param name="Title">The new title.</param>
/// <param name="Metadata">The new metadata document.</param>
public sealed record UpdateConversationRequest(
    string? Title = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

/// <summary>Request to add a participant to a group.</summary>
/// <param name="UserId">The user to add.</param>
/// <param name="Role">The role to grant: <c>member</c> (default), <c>admin</c>, or <c>owner</c>. Only an owner can grant admin or owner.</param>
public sealed record AddParticipantRequest(string UserId, string? Role = null);

/// <summary>Request to change a participant's role. Only an owner can do this.</summary>
/// <param name="Role">The new role: <c>member</c>, <c>admin</c>, or <c>owner</c>.</param>
public sealed record ChangeRoleRequest(string Role);

/// <summary>A participant of a conversation.</summary>
/// <param name="UserId">The host user ID.</param>
/// <param name="Role">The participant's role.</param>
/// <param name="JoinedAt">When the participant joined (UTC).</param>
public sealed record ParticipantResponse(string UserId, string Role, DateTimeOffset JoinedAt);

/// <summary>A bounded preview of the newest message in a conversation.</summary>
/// <param name="Id">The message ID.</param>
/// <param name="Sequence">The message sequence.</param>
/// <param name="SenderId">The sender.</param>
/// <param name="Kind">The message kind.</param>
/// <param name="Snippet">The first characters of the text, or empty when the message was deleted.</param>
/// <param name="CreatedAt">When the message was accepted (UTC).</param>
/// <param name="Deleted">Whether the message was deleted.</param>
public sealed record MessageSummaryResponse(
    Guid Id,
    long Sequence,
    string SenderId,
    string Kind,
    string Snippet,
    DateTimeOffset CreatedAt,
    bool Deleted);

/// <summary>A conversation visible to the caller.</summary>
/// <param name="Id">The conversation ID.</param>
/// <param name="Kind">Either <c>Direct</c> or <c>Group</c>.</param>
/// <param name="ParticipantUserIds">The active participants. Empty in list results for groups: use <see cref="ParticipantCount"/> or fetch the conversation.</param>
/// <param name="ExternalReference">The optional host reference.</param>
/// <param name="CreatedAt">When the conversation was created (UTC).</param>
/// <param name="LastActivityAt">When the last message was accepted (UTC).</param>
/// <param name="LastMessageSequence">The sequence of the newest message, or 0.</param>
/// <param name="LastReadSequence">The caller's last-read sequence.</param>
/// <param name="UnreadCount">Messages from others after the caller's last-read sequence, not counting deleted ones.</param>
/// <param name="Title">The group title, or <see langword="null"/> for a direct conversation.</param>
/// <param name="Metadata">The conversation metadata, or <see langword="null"/>.</param>
/// <param name="ArchivedAt">When the conversation was archived, or <see langword="null"/>.</param>
/// <param name="MyRole">The caller's role. Direct conversation participants are <c>member</c>.</param>
/// <param name="ParticipantCount">The number of active participants.</param>
/// <param name="Participants">The active participants with roles. Empty in list results for groups.</param>
/// <param name="LastMessage">A bounded preview of the newest message, or <see langword="null"/>.</param>
public sealed record ConversationResponse(
    Guid Id,
    string Kind,
    IReadOnlyList<string> ParticipantUserIds,
    string? ExternalReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    long LastMessageSequence,
    long LastReadSequence,
    long UnreadCount,
    string? Title = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    DateTimeOffset? ArchivedAt = null,
    string MyRole = SogaRoleNames.Member,
    int ParticipantCount = 0,
    IReadOnlyList<ParticipantResponse>? Participants = null,
    MessageSummaryResponse? LastMessage = null);

/// <summary>A page of conversations, most recent activity first.</summary>
/// <param name="Items">The conversations.</param>
/// <param name="NextCursor">An opaque cursor for the next page, or <see langword="null"/> at the end.</param>
public sealed record ConversationPageResponse(IReadOnlyList<ConversationResponse> Items, string? NextCursor);

/// <summary>Request to send a text message.</summary>
/// <param name="ClientMessageId">A client-generated idempotency key, unique per sender and conversation.</param>
/// <param name="Text">The plain-text content. Clients must encode it when rendering.</param>
/// <param name="ReplyToMessageId">An optional message in the same conversation that this message replies to.</param>
public sealed record SendMessageRequest(string ClientMessageId, string Text, Guid? ReplyToMessageId = null);

/// <summary>Request to edit the text of the caller's own message.</summary>
/// <param name="Text">The new plain-text content.</param>
public sealed record EditMessageRequest(string Text);

/// <summary>A bounded summary of the message a reply refers to.</summary>
/// <param name="MessageId">The original message ID.</param>
/// <param name="SenderId">The original sender.</param>
/// <param name="Snippet">The first characters of the original text, or empty when it was deleted.</param>
/// <param name="Deleted">Whether the original was deleted.</param>
public sealed record ReplySummaryResponse(Guid MessageId, string SenderId, string Snippet, bool Deleted);

/// <summary>A stored message.</summary>
/// <param name="Id">The server message ID.</param>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="Sequence">The authoritative per-conversation order. Editing and deleting never change it.</param>
/// <param name="SenderId">The host user ID of the sender, or the reserved system sender for system messages.</param>
/// <param name="ClientMessageId">The sender's idempotency key.</param>
/// <param name="Kind">Either <c>Text</c> or <c>System</c>.</param>
/// <param name="Content">The plain-text content. Empty when the message was deleted.</param>
/// <param name="CreatedAt">When the server accepted the message (UTC). Never changes.</param>
/// <param name="EditedAt">When the message was last edited, or <see langword="null"/>.</param>
/// <param name="Deleted">Whether the message was deleted. The content is then empty.</param>
/// <param name="ReplyTo">A bounded summary of the replied-to message, or <see langword="null"/>.</param>
public sealed record MessageResponse(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    string SenderId,
    string ClientMessageId,
    string Kind,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt = null,
    bool Deleted = false,
    ReplySummaryResponse? ReplyTo = null);

/// <summary>A page of message history, newest first.</summary>
/// <param name="Items">The messages, ordered by descending sequence. Deleted messages remain as empty tombstones so sequences stay contiguous.</param>
/// <param name="NextCursor">An opaque cursor for older messages, or <see langword="null"/> when history is exhausted.</param>
public sealed record MessagePageResponse(IReadOnlyList<MessageResponse> Items, string? NextCursor);

/// <summary>Request to advance the caller's read position.</summary>
/// <param name="MessageId">The message the caller has read through.</param>
public sealed record MarkReadRequest(Guid MessageId);

/// <summary>The caller's read state after an update.</summary>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="LastReadSequence">The caller's last-read sequence. It never decreases.</param>
/// <param name="UnreadCount">The remaining unread count.</param>
public sealed record ReadStateResponse(Guid ConversationId, long LastReadSequence, long UnreadCount);

/// <summary>The caller's total unread count.</summary>
/// <param name="TotalUnread">Unread messages across all conversations.</param>
public sealed record UnreadResponse(long TotalUnread);

/// <summary>Durable changes after a synchronization cursor.</summary>
/// <param name="Events">Events in commit order. Deduplicate by event ID.</param>
/// <param name="Cursor">The page-boundary cursor. Pass it to the next request.</param>
/// <param name="HasMore">Whether more changes are available immediately.</param>
public sealed record SyncResponse(IReadOnlyList<SogaEvent> Events, string Cursor, bool HasMore);

/// <summary>The envelope shared by realtime events and synchronization results.</summary>
/// <param name="EventId">A stable ID for client deduplication. Realtime and sync deliver the same ID.</param>
/// <param name="ProtocolVersion">The protocol version.</param>
/// <param name="Type">One of <see cref="SogaEventTypes"/>.</param>
/// <param name="OccurredAt">When the change committed (UTC).</param>
/// <param name="ConversationId">The conversation the event belongs to.</param>
/// <param name="Sequence">The conversation sequence when the event has one.</param>
/// <param name="Data">The event-specific payload.</param>
public sealed record SogaEvent(
    Guid EventId,
    int ProtocolVersion,
    string Type,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    long? Sequence,
    JsonElement Data);

/// <summary>Payload of <see cref="SogaEventTypes.MessageCreated"/> and <see cref="SogaEventTypes.MessageUpdated"/>.</summary>
/// <param name="Message">The stored message.</param>
public sealed record MessageCreatedData(MessageResponse Message);

/// <summary>Payload of <see cref="SogaEventTypes.MessageDeleted"/>.</summary>
/// <param name="MessageId">The deleted message.</param>
/// <param name="DeletedBy">The user who deleted it. It differs from the sender for an administrative deletion.</param>
/// <param name="DeletedAt">When it was deleted (UTC).</param>
public sealed record MessageDeletedData(Guid MessageId, string DeletedBy, DateTimeOffset DeletedAt);

/// <summary>Payload of <see cref="SogaEventTypes.ReadPositionChanged"/>.</summary>
/// <param name="UserId">The participant whose position advanced.</param>
/// <param name="LastReadSequence">The new last-read sequence.</param>
public sealed record ReadPositionChangedData(string UserId, long LastReadSequence);

/// <summary>Payload of <see cref="SogaEventTypes.ConversationCreated"/>.</summary>
/// <param name="ParticipantUserIds">The participants.</param>
/// <param name="ExternalReference">The optional host reference.</param>
/// <param name="CreatedAt">When the conversation was created (UTC).</param>
/// <param name="Kind">Either <c>Direct</c> or <c>Group</c>.</param>
/// <param name="Title">The group title, or <see langword="null"/>.</param>
public sealed record ConversationCreatedData(
    IReadOnlyList<string> ParticipantUserIds,
    string? ExternalReference,
    DateTimeOffset CreatedAt,
    string Kind = SogaConversationKinds.Direct,
    string? Title = null);

/// <summary>Payload of <see cref="SogaEventTypes.ConversationUpdated"/>. Metadata values are never sent in events: fetch the conversation.</summary>
/// <param name="Title">The current title.</param>
/// <param name="UpdatedBy">The user who made the change.</param>
/// <param name="MetadataChanged">Whether the metadata document changed.</param>
public sealed record ConversationUpdatedData(string? Title, string UpdatedBy, bool MetadataChanged);

/// <summary>Payload of <see cref="SogaEventTypes.ConversationArchived"/>.</summary>
/// <param name="Archived"><see langword="true"/> when archived, <see langword="false"/> when restored.</param>
/// <param name="ActorUserId">The user who made the change.</param>
public sealed record ConversationArchivedData(bool Archived, string ActorUserId);

/// <summary>Payload of <see cref="SogaEventTypes.ParticipantChanged"/>.</summary>
/// <param name="UserId">The affected participant.</param>
/// <param name="Change">One of <c>added</c>, <c>removed</c>, <c>left</c>, or <c>role_changed</c>.</param>
/// <param name="Role">The participant's role after the change, or <see langword="null"/> when they lost access.</param>
/// <param name="ActorUserId">The user who made the change.</param>
public sealed record ParticipantChangedData(string UserId, string Change, string? Role, string ActorUserId);

/// <summary>Payload of <see cref="SogaEventTypes.ConversationAccessChanged"/>. Reserved; V2 announces access changes with <see cref="ParticipantChangedData"/>.</summary>
/// <param name="UserId">The affected participant.</param>
/// <param name="Access">Either <c>granted</c> or <c>revoked</c>.</param>
public sealed record ConversationAccessChangedData(string UserId, string Access);

/// <summary>Payload of <see cref="SogaEventTypes.TypingChanged"/>. No text is ever transmitted.</summary>
/// <param name="UserId">The user who is typing.</param>
/// <param name="Typing">Whether they are typing now.</param>
public sealed record TypingChangedData(string UserId, bool Typing);

/// <summary>Payload of <see cref="SogaEventTypes.PresenceChanged"/>.</summary>
/// <param name="UserId">The user whose presence changed.</param>
/// <param name="Online">Whether they are online.</param>
/// <param name="LastSeenAt">When they were last online. Only present when the host enables last-seen.</param>
public sealed record PresenceChangedData(string UserId, bool Online, DateTimeOffset? LastSeenAt);

/// <summary>A user's presence, as returned by the hub.</summary>
/// <param name="UserId">The user.</param>
/// <param name="Online">Whether they are online.</param>
/// <param name="LastSeenAt">When they were last online, when enabled.</param>
public sealed record PresenceResponse(string UserId, bool Online, DateTimeOffset? LastSeenAt);
