namespace Soga.Abstractions;

/// <summary>Generates public identifiers. Replaced in tests for deterministic output.</summary>
internal interface ISogaIdGenerator
{
    Guid NewId();
}

/// <summary>Generates UUIDv7 values. They sort roughly by creation time but never replace the conversation sequence for ordering.</summary>
internal sealed class SogaIdGenerator : ISogaIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
