using System.Security.Claims;
using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Configuration;
using Soga.Errors;
using Soga.Tenancy;

namespace Soga.Identity;

/// <summary>The resolved, trusted identity of whoever is making a request.</summary>
/// <param name="TenantId">The normalized tenant the caller belongs to.</param>
/// <param name="UserId">The normalized host user ID of the caller.</param>
internal readonly record struct SogaCaller(string TenantId, string UserId);

/// <summary>Helpers for turning raw identity values into the normalized form Soga stores and compares.</summary>
internal static class SogaIdentity
{
    /// <summary>Trims <paramref name="raw"/> and cuts it to at most <paramref name="maxLength"/> characters.</summary>
    /// <param name="raw">The raw value to normalize.</param>
    /// <param name="maxLength">The maximum length of the result.</param>
    /// <returns>The normalized value, or <see langword="null"/> when <paramref name="raw"/> is null, empty or whitespace.</returns>
    /// <remarks>
    /// Over-long values are truncated rather than rejected. Two different IDs that share the same
    /// first <paramref name="maxLength"/> characters therefore become the same ID.
    /// </remarks>
    public static string? Normalize(string? raw, int maxLength)
    {
        // Whitespace-only input must count as "no value"; otherwise it would trim to "" and
        // be treated as a valid (empty) ID.
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    /// <summary>Normalizes a host user ID to the maximum supported user ID length.</summary>
    public static string? NormalizeUserId(string? raw) => Normalize(raw, SogaOptions.MaximumUserIdLength);

    /// <summary>Reads the normalized user ID from <paramref name="principal"/>.</summary>
    /// <param name="principal">The principal to read from; may be <see langword="null"/>.</param>
    /// <param name="claimType">The claim type that carries the user ID.</param>
    /// <returns>The user ID, or <see langword="null"/> when the principal is missing, unauthenticated, or has no usable claim.</returns>
    public static string? FromPrincipal(ClaimsPrincipal? principal, string claimType)
    {
        // Only authenticated principals are trusted; an anonymous principal can still carry claims.
        if (principal?.Identity?.IsAuthenticated != true) return null;

        return NormalizeUserId(principal.FindFirst(claimType)?.Value);
    }
}

/// <summary>Builds a <see cref="SogaCaller"/> (tenant and user) for the current request.</summary>
internal sealed class SogaCallerResolver(
    ISogaUserContext users,
    ISogaTenantContext tenants,
    IOptions<SogaOptions> options)
{
    /// <summary>Resolves the caller.</summary>
    /// <param name="principal">
    /// The principal to read the user from. When <see langword="null"/>, the user is taken from
    /// <see cref="ISogaUserContext"/> instead (for example when there is no direct access to the HTTP user).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The caller on success; <see cref="SogaErrorFactory.Unauthorized"/> when no user can be determined, or
    /// <see cref="SogaErrorFactory.TenantUnavailable"/> when no tenant can be determined.
    /// </returns>
    public async ValueTask<SogaResult<SogaCaller>> ResolveAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        var userId = principal is null
            ? SogaIdentity.NormalizeUserId(await users.GetUserIdAsync(cancellationToken))
            : SogaIdentity.FromPrincipal(principal, options.Value.Identity.UserIdClaimType);

        if (userId is null)
        {
            return SogaResult<SogaCaller>.Fail(SogaErrorFactory.Unauthorized);
        }

        // The tenant always comes from trusted host logic, never from the request.
        var tenantId = SogaIdentity.Normalize(
            await tenants.GetTenantIdAsync(cancellationToken),
            SogaOptions.MaximumTenantIdLength);

        return tenantId is null
            ? SogaResult<SogaCaller>.Fail(SogaErrorFactory.TenantUnavailable)
            : SogaResult<SogaCaller>.Ok(new SogaCaller(tenantId, userId));
    }
}
