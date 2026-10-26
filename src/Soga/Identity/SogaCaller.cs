using System.Security.Claims;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Errors;
using Soga.Tenancy;

namespace Soga.Identity;

/// <summary>The trusted acting user and tenant for one operation. Never built from request payloads.</summary>
internal readonly record struct SogaCaller(string TenantId, string UserId);

internal static class SogaIdentity
{
    /// <summary>Trims a host identifier and rejects empty or over-long values. Identifiers are opaque and compared ordinally.</summary>
    public static string? Normalize(string? raw, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        return value.Length <= maximumLength ? value : null;
    }

    public static string? NormalizeUserId(string? raw) => Normalize(raw, SogaOptions.MaximumUserIdLength);

    public static string? FromPrincipal(ClaimsPrincipal? principal, string claimType)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return NormalizeUserId(principal.FindFirstValue(claimType));
    }
}

internal sealed class SogaCallerResolver(
    ISogaUserContext users,
    ISogaTenantContext tenants,
    IOptions<SogaOptions> options)
{
    /// <summary>Resolves the caller. HTTP passes <see langword="null"/>; SignalR passes the connection principal.</summary>
    public async ValueTask<SogaResult<SogaCaller>> ResolveAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken)
    {
        var userId = principal is null
            ? SogaIdentity.NormalizeUserId(await users.GetUserIdAsync(cancellationToken))
            : SogaIdentity.FromPrincipal(principal, options.Value.Identity.UserIdClaimType);

        if (userId is null)
        {
            return SogaResult<SogaCaller>.Fail(SogaErrors.Unauthorized);
        }

        var tenantId = SogaIdentity.Normalize(
            await tenants.GetTenantIdAsync(cancellationToken),
            SogaOptions.MaximumTenantIdLength);

        return tenantId is null
            ? SogaResult<SogaCaller>.Fail(SogaErrors.TenantUnavailable)
            : SogaResult<SogaCaller>.Ok(new SogaCaller(tenantId, userId));
    }
}
