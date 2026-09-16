using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Soga.Configuration;

namespace Soga.Identity;

internal sealed class HttpSogaUserContext(IHttpContextAccessor httpContextAccessor) : ISogaUserContext
{
    public ValueTask<string?> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userId = string.IsNullOrWhiteSpace(rawId) ? null : rawId.Trim();

        return ValueTask.FromResult(userId is { Length: <= SogaOptions.MaximumUserIdLength } ? userId : null);
    }
}
