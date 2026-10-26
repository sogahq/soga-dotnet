using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Soga.Errors;

namespace Soga.Security;

/// <summary>
/// Provides extension methods for hosts that protect Soga endpoints with the ASP.NET Core rate limiter.
/// </summary>
public static class SogaRateLimiterOptionsExtensions
{
    /// <summary>
    /// Configures rejected requests to return a Problem Details response with the stable <c>soga.rate_limited</c>
    /// code, so clients never have to parse English text.
    /// </summary>
    /// <param name="options">The rate limiter options to configure.</param>
    /// <returns>The same <paramref name="options"/> instance, so further calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The response status is 429. When the limiter supplies a retry delay, the <c>Retry-After</c> header is set to the
    /// delay in whole seconds, rounded up.
    /// </remarks>
    public static RateLimiterOptions UseSogaRejectionResponse(this RateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                type: $"urn:soga:error:{SogaErrorCodes.RateLimited}",
                extensions: new Dictionary<string, object?> { ["code"] = SogaErrorCodes.RateLimited })
                .ExecuteAsync(context.HttpContext);
        };

        return options;
    }
}
