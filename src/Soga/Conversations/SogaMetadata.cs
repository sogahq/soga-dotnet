using System.Text;
using System.Text.Json;
using Soga.Configuration;

namespace Soga.Conversations;

/// <summary>
/// Validates conversation metadata: a flat object of string, number, or boolean values with bounded size, key format,
/// and property count. No nesting keeps it safe to store, compare, and return without surprises.
/// </summary>
internal static class SogaMetadata
{
    private const int MaximumKeyLength = 50;
    private const int MaximumStringValueLength = 500;

    /// <summary>Returns the canonical JSON (keys sorted, so equal documents compare equal) or an error description.</summary>
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

    public static IReadOnlyDictionary<string, JsonElement>? Parse(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

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
