using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Errors;
using Soga.Identity;

namespace Soga.Extensions;

/// <summary>Shared HTTP plumbing: caller resolution, bounded body reading, and Problem Details mapping.</summary>
internal static class SogaHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly SogaError BodyTooLarge =
        new(SogaErrorCodes.InvalidRequest, StatusCodes.Status413PayloadTooLarge, "Request body is too large");

    public static IResult Problem(HttpContext http, SogaError error)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = error.Code,
            ["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier,
        };

        return Results.Problem(
            statusCode: error.StatusCode,
            title: error.Title,
            detail: error.Detail,
            type: $"urn:soga:error:{error.Code}",
            extensions: extensions);
    }

    /// <summary>Resolves the trusted caller, runs the workflow, and maps the result.</summary>
    public static async Task<IResult> RunAsync<T>(
        HttpContext http,
        Func<SogaCaller, CancellationToken, Task<SogaResult<T>>> action,
        Func<T, IResult> onSuccess)
    {
        var resolver = http.RequestServices.GetRequiredService<SogaCallerResolver>();
        var caller = await resolver.ResolveAsync(principal: null, http.RequestAborted);
        if (!caller.IsSuccess)
        {
            return Problem(http, caller.Error!);
        }

        var result = await action(caller.Value, http.RequestAborted);
        return result.IsSuccess ? onSuccess(result.Value) : Problem(http, result.Error!);
    }

    /// <summary>Reads a JSON body without ever buffering more than the configured limit.</summary>
    public static async Task<SogaResult<T?>> ReadBodyAsync<T>(HttpContext http)
        where T : class
    {
        var maximum = http.RequestServices.GetRequiredService<IOptions<SogaOptions>>().Value.Security.MaximumRequestBodyBytes;

        if (!http.Request.HasJsonContentType())
        {
            return SogaResult<T?>.Fail(SogaErrors.InvalidRequest("Content-Type must be application/json."));
        }

        if (http.Request.ContentLength > maximum)
        {
            return SogaResult<T?>.Fail(BodyTooLarge);
        }

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
            {
                if (buffer.Length + read > maximum)
                {
                    return SogaResult<T?>.Fail(BodyTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            return SogaResult<T?>.Ok(JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions));
        }
        catch (JsonException)
        {
            return SogaResult<T?>.Fail(SogaErrors.InvalidRequest("The request body is not valid JSON for this operation."));
        }
    }

    /// <summary>Parses the optional <c>limit</c> query value. Returns <see langword="false"/> when it is present but not an integer.</summary>
    public static bool TryGetLimit(HttpContext http, out int? limit)
    {
        limit = null;
        var raw = http.Request.Query["limit"].ToString();
        if (raw.Length == 0)
        {
            return true;
        }

        if (int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            limit = parsed;
            return true;
        }

        return false;
    }

    public static string? GetCursor(HttpContext http)
    {
        var raw = http.Request.Query["cursor"].ToString();
        return raw.Length == 0 ? null : raw;
    }
}
