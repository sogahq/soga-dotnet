using System.Security.Cryptography;
using System.Text;

namespace Soga.Conversations;

/// <summary>
/// Computes the uniqueness key that the database uses to guarantee one direct conversation per pair of users and
/// external reference (ADR 0007).
/// </summary>
/// <remarks>
/// The key does not depend on which user opened the conversation. A conversation without an external reference is a
/// separate "general" conversation from any conversation about a specific order, booking, or ticket.
/// </remarks>
internal static class DirectConversationKey
{
    /// <summary>
    /// Creates the uniqueness key for a direct conversation.
    /// </summary>
    /// <param name="userA">The ID of one participant.</param>
    /// <param name="userB">The ID of the other participant.</param>
    /// <param name="externalReference">The optional host reference the conversation is about, such as an order ID.</param>
    /// <returns>An upper-case hexadecimal SHA-256 hash that is identical for both orderings of the two users.</returns>
    public static string Create(string userA, string userB, string? externalReference)
    {
        var (first, second) = string.CompareOrdinal(userA, userB) <= 0 ? (userA, userB) : (userB, userA);

        // Length prefixes make the encoding unambiguous without relying on a separator character.
        var canonical = $"{first.Length}:{first}|{second.Length}:{second}|{externalReference?.Length ?? -1}:{externalReference}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
