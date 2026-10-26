using System.Text.Json;

namespace Soga.Contracts;

/// <summary>Protocol constants.</summary>
public static class SogaProtocol
{
    /// <summary>The current wire protocol version.</summary>
    public const int Version = 1;
}

/// <summary>Names of the realtime and synchronization event types. The realtime client method name equals the event type.</summary>
public static class SogaEventTypes
{
    /// <summary>A message was accepted into a conversation.</summary>
    public const string MessageCreated = "MessageCreated";

    /// <summary>A participant's read position advanced.</summary>
    public const string ReadPositionChanged = "ReadPositionChanged";

    /// <summary>A conversation was created for the recipient.</summary>
    public const string ConversationCreated = "ConversationCreated";

    /// <summary>A participant's access to a conversation changed.</summary>
    public const string ConversationAccessChanged = "ConversationAccessChanged";
}

/// <summary>Request to create or retrieve a direct conversation.</summary>
/// <param name="OtherUserId">The host user ID of the other participant.</param>
/// <param name="ExternalReference">An optional host reference such as an order or booking ID.</param>
public sealed record CreateDirectConversationRequest(string OtherUserId, string? ExternalReference = null);

/// <summary>A conversation visible to the caller.</summary>
/// <param name="Id">The conversation ID.</param>
/// <param name="Kind">The conversation kind. Always <c>Direct</c> in V1.</param>
/// <param name="ParticipantUserIds">The active participants.</param>
/// <param name="ExternalReference">The optional host reference.</param>
/// <param name="CreatedAt">When the conversation was created (UTC).</param>
/// <param name="LastActivityAt">When the last message was accepted (UTC).</param>
/// <param name="LastMessageSequence">The sequence of the newest message, or 0.</param>
/// <param name="LastReadSequence">The caller's last-read sequence.</param>
/// <param name="UnreadCount">Messages from others after the caller's last-read sequence.</param>
public sealed record ConversationResponse(
    Guid Id,
    string Kind,
    IReadOnlyList<string> ParticipantUserIds,
    string? ExternalReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    long LastMessageSequence,
    long LastReadSequence,
    long UnreadCount);

/// <summary>Request to send a text message.</summary>
/// <param name="ClientMessageId">A client-generated idempotency key, unique per sender and conversation.</param>
/// <param name="Text">The plain-text content. Clients must encode it when rendering.</param>
public sealed record SendMessageRequest(string ClientMessageId, string Text);

/// <summary>A stored message.</summary>
/// <param name="Id">The server message ID.</param>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="Sequence">The authoritative per-conversation order.</param>
/// <param name="SenderId">The host user ID of the sender.</param>
/// <param name="ClientMessageId">The sender's idempotency key.</param>
/// <param name="Kind">The message kind. Always <c>Text</c> in V1.</param>
/// <param name="Content">The plain-text content.</param>
/// <param name="CreatedAt">When the server accepted the message (UTC).</param>
public sealed record MessageResponse(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    string SenderId,
    string ClientMessageId,
    string Kind,
    string Content,
    DateTimeOffset CreatedAt);

/// <summary>A page of message history, newest first.</summary>
/// <param name="Items">The messages, ordered by descending sequence.</param>
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

/// <summary>Payload of <see cref="SogaEventTypes.MessageCreated"/>.</summary>
/// <param name="Message">The stored message.</param>
public sealed record MessageCreatedData(MessageResponse Message);

/// <summary>Payload of <see cref="SogaEventTypes.ReadPositionChanged"/>.</summary>
/// <param name="UserId">The participant whose position advanced.</param>
/// <param name="LastReadSequence">The new last-read sequence.</param>
public sealed record ReadPositionChangedData(string UserId, long LastReadSequence);

/// <summary>Payload of <see cref="SogaEventTypes.ConversationCreated"/>.</summary>
/// <param name="ParticipantUserIds">The participants.</param>
/// <param name="ExternalReference">The optional host reference.</param>
/// <param name="CreatedAt">When the conversation was created (UTC).</param>
public sealed record ConversationCreatedData(
    IReadOnlyList<string> ParticipantUserIds,
    string? ExternalReference,
    DateTimeOffset CreatedAt);

/// <summary>Payload of <see cref="SogaEventTypes.ConversationAccessChanged"/>. Reserved; V1 has no access-change workflow.</summary>
/// <param name="UserId">The affected participant.</param>
/// <param name="Access">Either <c>granted</c> or <c>revoked</c>.</param>
public sealed record ConversationAccessChangedData(string UserId, string Access);
