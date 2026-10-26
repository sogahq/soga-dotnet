namespace Soga.Abstractions;

/// <summary>
/// Defines a source of new public identifiers for conversations, messages, events, and outbox records.
/// </summary>
/// <remarks>
/// Workflows depend on this interface instead of calling <see cref="Guid.CreateVersion7()"/> directly so that tests
/// can substitute a deterministic generator.
/// </remarks>
internal interface ISogaIdGenerator
{
    /// <summary>
    /// Creates a new unique identifier.
    /// </summary>
    /// <returns>A new, globally unique <see cref="Guid"/>.</returns>
    Guid NewId();
}

/// <summary>
/// Generates UUIDv7 identifiers (ADR 0001).
/// </summary>
/// <remarks>
/// UUIDv7 values sort roughly by creation time, which keeps database indexes compact. They are never used to order
/// messages: the per-conversation sequence is the only authoritative ordering.
/// </remarks>
internal sealed class SogaIdGenerator : ISogaIdGenerator
{
    /// <inheritdoc />
    public Guid NewId() => Guid.CreateVersion7();
}
