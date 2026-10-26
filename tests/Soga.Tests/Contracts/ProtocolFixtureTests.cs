using System.Text.Json;
using Soga.Contracts;
using Soga.Synchronization;

namespace Soga.Tests.Contracts;

/// <summary>
/// Canonical JSON fixtures. A change to any expected string is a protocol change and must be reviewed as one.
/// </summary>
public sealed class ProtocolFixtureTests
{
    private static readonly Guid MessageId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid ConversationId = Guid.Parse("018f0000-0000-7000-8000-000000000002");
    private static readonly Guid EventId = Guid.Parse("018f0000-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset At = new(2026, 9, 18, 10, 30, 0, TimeSpan.Zero);

    private static readonly MessageResponse Message = new(
        MessageId, ConversationId, 3, "alice", "client-1", "Text", "hello", At);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SogaEventSerializer.Options);

    // Event data is held as a JsonElement, which re-encodes '+' as +. Both forms are equivalent JSON;
    // this fixture pins the exact bytes clients receive.
    private static string EscapedInsideEventData(string json) => json.Replace("+", "\\u002B", StringComparison.Ordinal);

    [Fact]
    public void MessageResponse_HasCanonicalShape()
    {
        Assert.Equal(
            "{\"id\":\"018f0000-0000-7000-8000-000000000001\",\"conversationId\":\"018f0000-0000-7000-8000-000000000002\"," +
            "\"sequence\":3,\"senderId\":\"alice\",\"clientMessageId\":\"client-1\",\"kind\":\"Text\",\"content\":\"hello\"," +
            "\"createdAt\":\"2026-09-18T10:30:00+00:00\",\"editedAt\":null,\"deleted\":false,\"replyTo\":null}",
            Serialize(Message));
    }

    [Fact]
    public void MessageCreatedEvent_HasCanonicalShape()
    {
        var evt = SogaEventFactory.MessageCreated(
            EventId,
            new Soga.Persistence.MessageRecord(MessageId, ConversationId, 3, "alice", "client-1", "hello", At),
            At);

        Assert.Equal(
            "{\"eventId\":\"018f0000-0000-7000-8000-000000000003\",\"protocolVersion\":1,\"type\":\"MessageCreated\"," +
            "\"occurredAt\":\"2026-09-18T10:30:00+00:00\",\"conversationId\":\"018f0000-0000-7000-8000-000000000002\"," +
            "\"sequence\":3,\"data\":{\"message\":" + EscapedInsideEventData(Serialize(Message)) + "}}",
            Serialize(evt));
    }

    [Fact]
    public void ReadPositionChangedEvent_HasCanonicalShape()
    {
        var evt = SogaEventFactory.ReadPositionChanged(EventId, ConversationId, "bob", 3, At);

        Assert.Equal(
            "{\"eventId\":\"018f0000-0000-7000-8000-000000000003\",\"protocolVersion\":1,\"type\":\"ReadPositionChanged\"," +
            "\"occurredAt\":\"2026-09-18T10:30:00+00:00\",\"conversationId\":\"018f0000-0000-7000-8000-000000000002\"," +
            "\"sequence\":3,\"data\":{\"userId\":\"bob\",\"lastReadSequence\":3}}",
            Serialize(evt));
    }

    [Fact]
    public void ConversationCreatedEvent_HasCanonicalShape()
    {
        var evt = SogaEventFactory.ConversationCreated(EventId, ConversationId, ["alice", "bob"], "order:1", At);

        Assert.Equal(
            "{\"eventId\":\"018f0000-0000-7000-8000-000000000003\",\"protocolVersion\":1,\"type\":\"ConversationCreated\"," +
            "\"occurredAt\":\"2026-09-18T10:30:00+00:00\",\"conversationId\":\"018f0000-0000-7000-8000-000000000002\"," +
            "\"sequence\":null,\"data\":{\"participantUserIds\":[\"alice\",\"bob\"],\"externalReference\":\"order:1\"," +
            EscapedInsideEventData("\"createdAt\":\"2026-09-18T10:30:00+00:00\",\"kind\":\"Direct\",\"title\":null}}"),
            Serialize(evt));
    }

    [Fact]
    public void MessagePageResponse_HasCanonicalShape()
    {
        Assert.Equal(
            "{\"items\":[" + Serialize(Message) + "],\"nextCursor\":\"opaque\"}",
            Serialize(new MessagePageResponse([Message], "opaque")));
    }

    [Fact]
    public void SyncResponse_HasCanonicalShape()
    {
        Assert.Equal(
            "{\"events\":[],\"cursor\":\"opaque\",\"hasMore\":false}",
            Serialize(new SyncResponse([], "opaque", false)));
    }

    [Fact]
    public void SendMessageRequest_DeserializesCamelCase()
    {
        var request = JsonSerializer.Deserialize<SendMessageRequest>(
            "{\"clientMessageId\":\"c1\",\"text\":\"hi\"}",
            SogaEventSerializer.Options);

        Assert.Equal(new SendMessageRequest("c1", "hi"), request);
    }

    [Fact]
    public void EventEnvelope_RoundTripsThroughStoredPayload()
    {
        var evt = SogaEventFactory.ReadPositionChanged(EventId, ConversationId, "bob", 3, At);

        var restored = SogaEventSerializer.Deserialize(SogaEventSerializer.Serialize(evt));

        Assert.Equal(evt.EventId, restored.EventId);
        Assert.Equal(new ReadPositionChangedData("bob", 3), SogaEventSerializer.ReadData<ReadPositionChangedData>(restored));
    }
}
