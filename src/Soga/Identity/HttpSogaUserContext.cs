using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Soga.Configuration;

namespace Soga.Identity;

internal sealed class HttpSogaUserContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<SogaOptions> options) : ISogaUserContext
{
    public ValueTask<string?> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContextAccessor.HttpContext?.User;
        return ValueTask.FromResult(SogaIdentity.FromPrincipal(principal, options.Value.Identity.UserIdClaimType));
    }
}
