using System.Text.Json;
using Soga.Contracts;
using Soga.Groups;
using Soga.Persistence;

namespace Soga.Synchronization;

/// <summary>
/// Serializes protocol events to and from the JSON stored in the change log and the outbox.
/// </summary>
/// <remarks>
/// The same <see cref="Options"/> are used everywhere, so an event read back from storage is byte-for-byte the event
/// that realtime clients received.
/// </remarks>
internal static class SogaEventSerializer
{
    /// <summary>
    /// The JSON options for every stored and transmitted event, which use ASP.NET Core web defaults (camelCase).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes an event to JSON for storage.
    /// </summary>
    /// <param name="evt">The event to serialize.</param>
    /// <returns>The JSON representation of <paramref name="evt"/>.</returns>
    public static string Serialize(SogaEvent evt) => JsonSerializer.Serialize(evt, Options);

    /// <summary>
    /// Deserializes a stored event.
    /// </summary>
    /// <param name="payload">The JSON written by <see cref="Serialize(SogaEvent)"/>.</param>
    /// <returns>The deserialized event.</returns>
    /// <exception cref="JsonException">The payload is empty or not a valid event.</exception>
    public static SogaEvent Deserialize(string payload) =>
        JsonSerializer.Deserialize<SogaEvent>(payload, Options)
        ?? throw new JsonException("The stored event payload is empty.");

    /// <summary>
    /// Reads the typed data of an event.
    /// </summary>
    /// <typeparam name="T">The data contract of the event type, such as <see cref="MessageCreatedData"/>.</typeparam>
    /// <param name="evt">The event to read.</param>
    /// <returns>The event data.</returns>
    /// <exception cref="JsonException">The data is empty or does not match <typeparamref name="T"/>.</exception>
    public static T ReadData<T>(SogaEvent evt) =>
        evt.Data.Deserialize<T>(Options) ?? throw new JsonException("The event data is empty.");
}

/// <summary>
/// Creates protocol events.
/// </summary>
/// <remarks>
/// Stores call this factory inside the same transaction that writes the change, so the change-log row and the outbox
/// row share one event ID and payload. That is what lets a client deduplicate an event it received both live and
/// from sync.
/// </remarks>
internal static class SogaEventFactory
{
    /// <summary>
    /// Creates a <see cref="SogaEventTypes.MessageCreated"/> event.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="message">The stored message.</param>
    /// <param name="occurredAt">The time the message was accepted.</param>
    /// <returns>The event, carrying the message and its sequence.</returns>
    public static SogaEvent MessageCreated(Guid eventId, MessageRecord message, DateTimeOffset occurredAt) => Create(
        eventId,
        SogaEventTypes.MessageCreated,
        occurredAt,
        message.ConversationId,
        message.Sequence,
        new MessageCreatedData(message.ToResponse()));

    /// <summary>
    /// Creates a <see cref="SogaEventTypes.MessageUpdated"/> event for an edited message.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="message">The message after the edit.</param>
    /// <param name="occurredAt">The time of the edit.</param>
    /// <returns>The event, carrying the edited message and its unchanged sequence.</returns>
    public static SogaEvent MessageUpdated(Guid eventId, MessageRecord message, DateTimeOffset occurredAt) => Create(
        eventId,
        SogaEventTypes.MessageUpdated,
        occurredAt,
        message.ConversationId,
        message.Sequence,
        new MessageCreatedData(message.ToResponse()));

    /// <summary>
    /// Creates a <see cref="SogaEventTypes.MessageDeleted"/> event.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="message">The deleted message.</param>
    /// <param name="deletedBy">The ID of the user who deleted the message.</param>
    /// <param name="occurredAt">The time of the deletion.</param>
    /// <returns>The event. It carries the message ID but never the deleted text.</returns>
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

    /// <summary>
    /// Creates a <see cref="SogaEventTypes.ReadPositionChanged"/> event.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="userId">The ID of the participant whose read position moved.</param>
    /// <param name="lastReadSequence">The new read position.</param>
    /// <param name="occurredAt">The time of the change.</param>
    /// <returns>The event.</returns>
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

    /// <summary>
    /// Creates a <see cref="SogaEventTypes.ConversationCreated"/> event.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="conversationId">The ID of the new conversation.</param>
    /// <param name="participantIds">The IDs of the participants.</param>
    /// <param name="externalReference">The optional host reference the conversation is about.</param>
    /// <param name="createdAt">The time the conversation was created.</param>
    /// <param name="kind">The conversation kind, from <see cref="SogaConversationKinds"/>.</param>
    /// <param name="title">The group title, or <see langword="null"/> for a direct conversation.</param>
    /// <returns>The event.</returns>
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

    /// <summary>
    /// Creates an event that was planned by <see cref="GroupRules"/>.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="plan">The planned event type and data.</param>
    /// <param name="occurredAt">The time of the change.</param>
    /// <returns>The event.</returns>
    public static SogaEvent FromPlan(Guid eventId, Guid conversationId, PlannedEvent plan, DateTimeOffset occurredAt) =>
        Create(eventId, plan.Type, occurredAt, conversationId, sequence: null, plan.Data);

    /// <summary>
    /// Creates an ephemeral event, such as a typing or presence change.
    /// </summary>
    /// <param name="type">The event type, from <see cref="SogaEventTypes"/>.</param>
    /// <param name="conversationId">The ID of the conversation the event concerns.</param>
    /// <param name="data">The event data contract.</param>
    /// <param name="occurredAt">The time of the change.</param>
    /// <returns>The event, with a newly generated ID.</returns>
    /// <remarks>Ephemeral events are broadcast once. They are never stored and never returned by sync.</remarks>
    public static SogaEvent Ephemeral(string type, Guid conversationId, object data, DateTimeOffset occurredAt) =>
        Create(Guid.NewGuid(), type, occurredAt, conversationId, sequence: null, data);

    /// <summary>
    /// Creates an event envelope with the current protocol version.
    /// </summary>
    /// <param name="eventId">The unique ID of the event.</param>
    /// <param name="type">The event type, from <see cref="SogaEventTypes"/>.</param>
    /// <param name="occurredAt">The time of the change.</param>
    /// <param name="conversationId">The ID of the conversation the event concerns.</param>
    /// <param name="sequence">The message or read sequence the event refers to, if any.</param>
    /// <param name="data">The event data contract, serialized using its runtime type.</param>
    /// <returns>The event.</returns>
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
