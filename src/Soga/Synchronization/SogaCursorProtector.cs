using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Soga.Synchronization;

/// <summary>
/// Specifies what a cursor pages through. A cursor of one kind is never accepted as another.
/// </summary>
internal enum SogaCursorKind
{
    /// <summary>A position in one conversation's message history.</summary>
    History,

    /// <summary>A position in the tenant's change log, used by sync.</summary>
    Sync,

    /// <summary>A position in the caller's conversation list.</summary>
    ConversationList,
}

/// <summary>
/// Creates and validates opaque, tamper-proof cursors (ADR 0008).
/// </summary>
/// <param name="provider">The ASP.NET Core Data Protection provider that supplies the encryption keys.</param>
/// <remarks>
/// <para>
/// Each cursor is encrypted and bound to its <see cref="SogaCursorKind"/>, tenant, and scope (a conversation ID or a
/// user ID). A cursor issued for one conversation or user is rejected for any other, so cursors cannot be forged or
/// replayed to read data the caller cannot otherwise see.
/// </para>
/// <para>
/// In container or multi-instance deployments the host must persist and share the Data Protection key ring. Otherwise
/// cursors become invalid after a restart and clients must start a fresh sync.
/// </para>
/// </remarks>
internal sealed class SogaCursorProtector(IDataProtectionProvider provider)
{
    /// <summary>
    /// The longest cursor accepted. Longer input is rejected before decryption is attempted.
    /// </summary>
    private const int MaximumCursorLength = 512;

    /// <summary>
    /// The protector for the <c>Soga.Cursor.v1</c> purpose, which isolates cursor keys from other Data Protection uses.
    /// </summary>
    private readonly IDataProtector _protector = provider.CreateProtector("Soga.Cursor.v1");

    /// <summary>
    /// Creates a cursor for a numeric position.
    /// </summary>
    /// <param name="kind">What the cursor pages through.</param>
    /// <param name="tenantId">The tenant the cursor belongs to.</param>
    /// <param name="scope">The conversation or user the cursor belongs to.</param>
    /// <param name="position">The position, such as a message or change sequence.</param>
    /// <returns>An opaque cursor string.</returns>
    public string Encode(SogaCursorKind kind, string tenantId, string scope, long position) =>
        EncodeValue(kind, tenantId, scope, position.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Validates a cursor and reads its numeric position.
    /// </summary>
    /// <param name="cursor">The cursor supplied by the client.</param>
    /// <param name="kind">The kind the cursor must have.</param>
    /// <param name="tenantId">The tenant the cursor must belong to.</param>
    /// <param name="scope">The conversation or user the cursor must belong to.</param>
    /// <param name="position">When this method returns <see langword="true"/>, contains the decoded position.</param>
    /// <returns>
    /// <see langword="true"/> if the cursor is authentic, matches <paramref name="kind"/>, <paramref name="tenantId"/>,
    /// and <paramref name="scope"/>, and holds a valid number; otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryDecode(string? cursor, SogaCursorKind kind, string tenantId, string scope, out long position)
    {
        position = 0;
        return TryDecodeValue(cursor, kind, tenantId, scope, out var value) &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out position);
    }

    /// <summary>
    /// Creates a cursor for a composite position, such as an activity time combined with an ID.
    /// </summary>
    /// <param name="kind">What the cursor pages through.</param>
    /// <param name="tenantId">The tenant the cursor belongs to.</param>
    /// <param name="scope">The conversation or user the cursor belongs to.</param>
    /// <param name="value">The position, encoded as a string by the caller.</param>
    /// <returns>An opaque cursor string.</returns>
    public string EncodeValue(SogaCursorKind kind, string tenantId, string scope, string value) =>
        _protector.Protect(Prefix(kind, tenantId, scope) + value);

    /// <summary>
    /// Validates a cursor and reads its position as a string.
    /// </summary>
    /// <param name="cursor">The cursor supplied by the client.</param>
    /// <param name="kind">The kind the cursor must have.</param>
    /// <param name="tenantId">The tenant the cursor must belong to.</param>
    /// <param name="scope">The conversation or user the cursor must belong to.</param>
    /// <param name="value">When this method returns <see langword="true"/>, contains the decoded position.</param>
    /// <returns>
    /// <see langword="true"/> if the cursor is authentic, not too long, and matches <paramref name="kind"/>,
    /// <paramref name="tenantId"/>, and <paramref name="scope"/>; otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryDecodeValue(string? cursor, SogaCursorKind kind, string tenantId, string scope, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumCursorLength)
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(cursor);
            var prefix = Prefix(kind, tenantId, scope);
            if (!payload.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            value = payload[prefix.Length..];
            return value.Length > 0;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the binding prefix that is encrypted in front of every cursor position.
    /// </summary>
    /// <param name="kind">The cursor kind.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="scope">The conversation or user scope.</param>
    /// <returns>
    /// A prefix such as <c>1|7:default|5:alice|</c>. Length prefixes make the encoding unambiguous whatever characters
    /// the IDs contain.
    /// </returns>
    private static string Prefix(SogaCursorKind kind, string tenantId, string scope) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)kind}|{tenantId.Length}:{tenantId}|{scope.Length}:{scope}|");
}
