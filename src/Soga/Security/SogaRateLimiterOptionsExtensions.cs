using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Soga.Errors;

namespace Soga.Security;

/// <summary>Helpers for hosts that rate-limit Soga endpoints with the ASP.NET Core rate limiter.</summary>
public static class SogaRateLimiterOptionsExtensions
{
    /// <summary>
    /// Makes rejected requests return a Problem Details body with the stable <c>soga.rate_limited</c> code
    /// (and <c>Retry-After</c> when the limiter supplies it), so clients never parse English text.
    /// </summary>
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
