using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Soga.Synchronization;

internal enum SogaCursorKind
{
    History,
    Sync,
    ConversationList,
}

/// <summary>
/// Creates opaque, tamper-evident cursors. A cursor is bound to its kind, tenant, and scope, so a cursor
/// issued for one conversation or user cannot be replayed against another. Keys come from ASP.NET Core Data
/// Protection; hosts must persist them in multi-instance or container deployments.
/// </summary>
internal sealed class SogaCursorProtector(IDataProtectionProvider provider)
{
    private const int MaximumCursorLength = 512;
    private readonly IDataProtector _protector = provider.CreateProtector("Soga.Cursor.v1");

    public string Encode(SogaCursorKind kind, string tenantId, string scope, long position) =>
        EncodeValue(kind, tenantId, scope, position.ToString(CultureInfo.InvariantCulture));

    public bool TryDecode(string? cursor, SogaCursorKind kind, string tenantId, string scope, out long position)
    {
        position = 0;
        return TryDecodeValue(cursor, kind, tenantId, scope, out var value) &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out position);
    }

    /// <summary>Encodes a composite position such as an activity time plus an ID.</summary>
    public string EncodeValue(SogaCursorKind kind, string tenantId, string scope, string value) =>
        _protector.Protect(Prefix(kind, tenantId, scope) + value);

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

    private static string Prefix(SogaCursorKind kind, string tenantId, string scope) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)kind}|{tenantId.Length}:{tenantId}|{scope.Length}:{scope}|");
}
