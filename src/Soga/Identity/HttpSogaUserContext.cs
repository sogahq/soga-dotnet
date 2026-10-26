using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Soga.Configuration;

namespace Soga.Identity;

/// <summary>
/// The default <see cref="ISogaUserContext"/>, which reads the acting user from the authenticated principal of the
/// current HTTP request.
/// </summary>
/// <param name="httpContextAccessor">Provides access to the current <see cref="HttpContext"/>.</param>
/// <param name="options">The Soga options that name the claim holding the user ID.</param>
/// <remarks>
/// The user ID is taken from the claim configured in <see cref="SogaIdentityOptions.UserIdClaimType"/>. An
/// unauthenticated request, or one without that claim, yields <see langword="null"/>.
/// </remarks>
internal sealed class HttpSogaUserContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<SogaOptions> options) : ISogaUserContext
{
    /// <inheritdoc />
    public ValueTask<string?> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContextAccessor.HttpContext?.User;
        return ValueTask.FromResult(SogaIdentity.FromPrincipal(principal, options.Value.Identity.UserIdClaimType));
    }
}
