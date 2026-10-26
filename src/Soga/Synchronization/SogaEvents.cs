using System.Text.Json;
using Soga.Contracts;
using Soga.Groups;
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

    public static SogaEvent MessageUpdated(Guid eventId, MessageRecord message, DateTimeOffset occurredAt) => Create(
        eventId,
        SogaEventTypes.MessageUpdated,
        occurredAt,
        message.ConversationId,
        message.Sequence,
        new MessageCreatedData(message.ToResponse()));

    public static SogaEvent MessageDeleted(
        Guid eventId,
        MessageRecord message,
        string deletedBy,
        DateTimeOffset occurredAt) => Create(
        eventId,
        SogaEventTypes.MessageDeleted,
        occurredAt,
        message.ConversationId,
        message.Sequence,
        new MessageDeletedData(message.Id, deletedBy, occurredAt));

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
        DateTimeOffset createdAt,
        string kind = SogaConversationKinds.Direct,
        string? title = null) => Create(
        eventId,
        SogaEventTypes.ConversationCreated,
        createdAt,
        conversationId,
        sequence: null,
        new ConversationCreatedData(participantIds, externalReference, createdAt, kind, title));

    /// <summary>Wraps an event planned by <see cref="GroupRules"/> with its ID and time.</summary>
    public static SogaEvent FromPlan(Guid eventId, Guid conversationId, PlannedEvent plan, DateTimeOffset occurredAt) =>
        Create(eventId, plan.Type, occurredAt, conversationId, sequence: null, plan.Data);

    /// <summary>Builds an ephemeral event. It is broadcast once and never stored.</summary>
    public static SogaEvent Ephemeral(string type, Guid conversationId, object data, DateTimeOffset occurredAt) =>
        Create(Guid.NewGuid(), type, occurredAt, conversationId, sequence: null, data);

    private static SogaEvent Create(
        Guid eventId,
        string type,
        DateTimeOffset occurredAt,
        Guid conversationId,
        long? sequence,
        object data) => new(
        eventId,
        SogaProtocol.Version,
        type,
        occurredAt,
        conversationId,
        sequence,
        JsonSerializer.SerializeToElement(data, data.GetType(), SogaEventSerializer.Options));
}
