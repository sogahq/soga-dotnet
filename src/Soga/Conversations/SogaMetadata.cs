using System.Text;
using System.Text.Json;
using Soga.Configuration;

namespace Soga.Conversations;

/// <summary>
/// Validates and canonicalizes conversation metadata.
/// </summary>
/// <remarks>
/// Metadata is a flat JSON object whose values are strings, numbers, or booleans. Key length, value length, property
/// count, and total size are all bounded. Disallowing nesting keeps metadata safe to store, compare, and return.
/// Metadata is never included in realtime events.
/// </remarks>
internal static class SogaMetadata
{
    /// <summary>
    /// The maximum number of characters in a metadata key.
    /// </summary>
    private const int MaximumKeyLength = 50;

    /// <summary>
    /// The maximum number of characters in a string metadata value.
    /// </summary>
    private const int MaximumStringValueLength = 500;

    /// <summary>
    /// Validates metadata and converts it to its canonical JSON form.
    /// </summary>
    /// <param name="metadata">The metadata supplied by the client, or <see langword="null"/> for none.</param>
    /// <param name="options">The conversation options that hold the property-count and size limits.</param>
    /// <returns>
    /// A tuple whose <c>Json</c> item is the canonical JSON with keys sorted ordinally, so equal documents compare
    /// equal, and whose <c>Error</c> item describes the first rule broken. Both items are <see langword="null"/> when
    /// <paramref name="metadata"/> is empty.
    /// </returns>
    public static (string? Json, string? Error) Validate(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        SogaConversationOptions options)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return (null, null);
        }

        if (metadata.Count > options.MaximumMetadataProperties)
        {
            return (null, $"Metadata cannot have more than {options.MaximumMetadataProperties} properties.");
        }

        var sorted = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (!IsValidKey(key))
            {
                return (null, $"Metadata keys must be 1 to {MaximumKeyLength} characters of letters, digits, '.', '_' or '-'.");
            }

            var valid = value.ValueKind switch
            {
                JsonValueKind.String => (value.GetString()?.Length ?? 0) <= MaximumStringValueLength,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
                _ => false,
            };

            if (!valid)
            {
                return (null, $"Metadata values must be strings up to {MaximumStringValueLength} characters, numbers, or booleans. Nesting and null are not allowed.");
            }

            sorted[key] = value;
        }

        var json = JsonSerializer.Serialize(sorted);
        return Encoding.UTF8.GetByteCount(json) > options.MaximumMetadataBytes
            ? (null, $"Metadata cannot exceed {options.MaximumMetadataBytes} bytes.")
            : (json, null);
    }

    /// <summary>
    /// Parses stored canonical metadata JSON back into a dictionary.
    /// </summary>
    /// <param name="json">The stored JSON, or <see langword="null"/> when the conversation has no metadata.</param>
    /// <returns>The metadata, or <see langword="null"/> when <paramref name="json"/> is <see langword="null"/> or empty.</returns>
    public static IReadOnlyDictionary<string, JsonElement>? Parse(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    /// <summary>
    /// Determines whether a metadata key has a valid length and uses only allowed characters.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="key"/> is 1 to <see cref="MaximumKeyLength"/> ASCII letters, digits,
    /// <c>.</c>, <c>_</c>, or <c>-</c>; otherwise, <see langword="false"/>.
    /// </returns>
    private static bool IsValidKey(string key)
    {
        if (key.Length is 0 or > MaximumKeyLength)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
