using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Soga.Configuration;

namespace Soga.Identity;

/// <summary>
/// Default <see cref="ISogaUserContext"/> that reads the user ID from the authenticated
/// principal of the current HTTP request.
/// </summary>
/// <remarks>
/// The claim that holds the ID is configurable through <c>Soga:Identity:UserIdClaimType</c>.
/// </remarks>
internal sealed class HttpSogaUserContext(IHttpContextAccessor httpContextAccessor, IOptions<SogaOptions> options) : ISogaUserContext
{
    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="null"/> when there is no active HTTP request, the user is not
    /// authenticated, or the configured claim is missing or blank.
    /// </remarks>
    public ValueTask<string?> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var principal = httpContextAccessor.HttpContext?.User;
        return ValueTask.FromResult(SogaIdentity.FromPrincipal(principal, options.Value.Identity.UserIdClaimType));
    }
}
