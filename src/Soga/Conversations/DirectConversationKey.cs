using System.Security.Cryptography;
using System.Text;

namespace Soga.Conversations;

/// <summary>
/// Computes the database-enforced uniqueness key of a direct conversation (ADR 0007): one conversation per
/// unordered user pair and external reference. A missing reference is its own "general" conversation.
/// </summary>
internal static class DirectConversationKey
{
    public static string Create(string userA, string userB, string? externalReference)
    {
        var (first, second) = string.CompareOrdinal(userA, userB) <= 0 ? (userA, userB) : (userB, userA);

        // Length prefixes make the encoding unambiguous without relying on a separator character.
        var canonical = $"{first.Length}:{first}|{second.Length}:{second}|{externalReference?.Length ?? -1}:{externalReference}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
