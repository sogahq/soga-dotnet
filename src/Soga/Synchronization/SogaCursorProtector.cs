using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Soga.Synchronization;

internal enum SogaCursorKind
{
    History,
    Sync,
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

    public string Encode(SogaCursorKind kind, string tenantId, string scope, long position)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)kind}|{tenantId.Length}:{tenantId}|{scope.Length}:{scope}|{position}");
        return _protector.Protect(payload);
    }

    public bool TryDecode(string? cursor, SogaCursorKind kind, string tenantId, string scope, out long position)
    {
        position = 0;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumCursorLength)
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(cursor);
            var expectedPrefix = string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)kind}|{tenantId.Length}:{tenantId}|{scope.Length}:{scope}|");

            return payload.StartsWith(expectedPrefix, StringComparison.Ordinal) &&
                long.TryParse(
                    payload.AsSpan(expectedPrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out position);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
