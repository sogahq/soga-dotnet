using System.Text.Json;
using Soga.Contracts;
using Soga.Persistence;

namespace Soga.Synchronization;

internal static class SogaEventSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(SogaEvent evt) => JsonSerializer.Serialize(evt, Options);

    public static SogaEvent Deserialize(string payload) =>
        JsonSerializer.Deserialize<SogaEvent>(payload, Options)
        ?? throw new JsonException("The stored event payload is empty.");

    public static T ReadData<T>(SogaEvent evt) =>
        evt.Data.Deserialize<T>(Options) ?? throw new JsonException("The event data is empty.");
}

/// <summary>Builds protocol events. Stores call it so sync and realtime deliver the same event ID and payload.</summary>
internal static class SogaEventFactory
{
    public static SogaEvent MessageCreated(Guid eventId, MessageRecord message, DateTimeOffset occurredAt) => Create(
        eventId,
        SogaEventTypes.MessageCreated,
        occurredAt,
        message.ConversationId,
        message.Sequence,
        new MessageCreatedData(message.ToResponse()));

    public static SogaEvent ReadPositionChanged(
        Guid eventId,
        Guid conversationId,
        string userId,
        long lastReadSequence,
        DateTimeOffset occurredAt) => Create(
        eventId,
        SogaEventTypes.ReadPositionChanged,
        occurredAt,
        conversationId,
        lastReadSequence,
        new ReadPositionChangedData(userId, lastReadSequence));

    public static SogaEvent ConversationCreated(
        Guid eventId,
        Guid conversationId,
        IReadOnlyList<string> participantIds,
        string? externalReference,
        DateTimeOffset createdAt) => Create(
        eventId,
        SogaEventTypes.ConversationCreated,
        createdAt,
        conversationId,
        sequence: null,
        new ConversationCreatedData(participantIds, externalReference, createdAt));

    private static SogaEvent Create<T>(
        Guid eventId,
        string type,
        DateTimeOffset occurredAt,
        Guid conversationId,
        long? sequence,
        T data) => new(
        eventId,
        SogaProtocol.Version,
        type,
        occurredAt,
        conversationId,
        sequence,
        JsonSerializer.SerializeToElement(data, SogaEventSerializer.Options));
}
