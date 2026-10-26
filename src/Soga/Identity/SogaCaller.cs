using System.Security.Claims;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Errors;
using Soga.Tenancy;

namespace Soga.Identity;

/// <summary>
/// Represents the trusted user and tenant performing one operation.
/// </summary>
/// <param name="TenantId">The normalized ID of the tenant the operation runs in.</param>
/// <param name="UserId">The normalized ID of the acting user.</param>
/// <remarks>
/// A caller is only ever built by <see cref="SogaCallerResolver"/> from server-side context, never from a request
/// payload, so a client cannot impersonate another user or reach another tenant.
/// </remarks>
internal readonly record struct SogaCaller(string TenantId, string UserId);

/// <summary>
/// Provides normalization rules for user and tenant identifiers supplied by the host.
/// </summary>
/// <remarks>
/// Identifiers are opaque strings. They are trimmed but never lower-cased, and are always compared ordinally, so
/// <c>Alice</c> and <c>alice</c> are different users.
/// </remarks>
internal static class SogaIdentity
{
    /// <summary>
    /// Normalizes a host-supplied identifier.
    /// </summary>
    /// <param name="raw">The identifier as supplied by the host.</param>
    /// <param name="maximumLength">The maximum number of characters allowed after trimming.</param>
    /// <returns>
    /// The trimmed identifier, or <see langword="null"/> when <paramref name="raw"/> is empty, whitespace, or longer
    /// than <paramref name="maximumLength"/>.
    /// </returns>
    public static string? Normalize(string? raw, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        return value.Length <= maximumLength ? value : null;
    }

    /// <summary>
    /// The prefix of user IDs reserved for Soga itself, such as the system message sender.
    /// </summary>
    public const string ReservedUserIdPrefix = "$soga:";

    /// <summary>
    /// Normalizes a user ID using the <see cref="SogaOptions.MaximumUserIdLength"/> limit.
    /// </summary>
    /// <param name="raw">The user ID as supplied by the host.</param>
    /// <returns>
    /// The normalized user ID, or <see langword="null"/> when it is missing, too long, or starts with
    /// <see cref="ReservedUserIdPrefix"/>.
    /// </returns>
    /// <remarks>
    /// Reserved IDs are refused so that no real user can act as, or be confused with, the system message sender.
    /// </remarks>
    public static string? NormalizeUserId(string? raw)
    {
        var value = Normalize(raw, SogaOptions.MaximumUserIdLength);
        return value is not null && value.StartsWith(ReservedUserIdPrefix, StringComparison.Ordinal) ? null : value;
    }

    /// <summary>
    /// Reads and normalizes the user ID from an authenticated principal.
    /// </summary>
    /// <param name="principal">The principal to read from.</param>
    /// <param name="claimType">The claim type that holds the user ID.</param>
    /// <returns>
    /// The normalized user ID, or <see langword="null"/> when the principal is not authenticated or has no usable
    /// claim of type <paramref name="claimType"/>.
    /// </returns>
    public static string? FromPrincipal(ClaimsPrincipal? principal, string claimType)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return NormalizeUserId(principal.FindFirstValue(claimType));
    }
}

/// <summary>
/// Builds the <see cref="SogaCaller"/> for an operation from the host's user and tenant resolvers.
/// </summary>
/// <param name="users">The resolver for the acting user on HTTP requests.</param>
/// <param name="tenants">The resolver for the active tenant.</param>
/// <param name="options">The Soga options that name the user ID claim type.</param>
internal sealed class SogaCallerResolver(
    ISogaUserContext users,
    ISogaTenantContext tenants,
    IOptions<SogaOptions> options)
{
    /// <summary>
    /// Resolves the trusted caller for the current operation.
    /// </summary>
    /// <param name="principal">
    /// The SignalR connection principal, or <see langword="null"/> for HTTP requests, which use
    /// <see cref="ISogaUserContext"/> instead.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the caller, or fails with
    /// <see cref="SogaErrors.Unauthorized"/> when there is no user and <see cref="SogaErrors.TenantUnavailable"/>
    /// when the tenant cannot be resolved.
    /// </returns>
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
