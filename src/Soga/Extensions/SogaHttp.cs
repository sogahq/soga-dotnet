using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Errors;
using Soga.Identity;

namespace Soga.Extensions;

/// <summary>
/// Provides the HTTP plumbing shared by every Soga endpoint: caller resolution, bounded body reading, query parsing,
/// and mapping of <see cref="SogaError"/> values to Problem Details responses.
/// </summary>
internal static class SogaHttp
{
    /// <summary>
    /// The JSON options used to read request bodies, which use ASP.NET Core web defaults (camelCase, case-insensitive).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The error returned when a request body exceeds <see cref="SogaSecurityOptions.MaximumRequestBodyBytes"/>
    /// (HTTP 413).
    /// </summary>
    private static readonly SogaError BodyTooLarge =
        new(SogaErrorCodes.InvalidRequest, StatusCodes.Status413PayloadTooLarge, "Request body is too large");

    /// <summary>
    /// Creates a Problem Details response for a Soga error.
    /// </summary>
    /// <param name="http">The current HTTP context, used for the trace ID.</param>
    /// <param name="error">The error to describe.</param>
    /// <returns>
    /// An <see cref="IResult"/> that writes the error's status code, title, and detail, a <c>type</c> of
    /// <c>urn:soga:error:&lt;code&gt;</c>, and <c>code</c> and <c>traceId</c> extension members.
    /// </returns>
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

    /// <summary>
    /// Resolves the trusted caller, runs a workflow operation, and maps its result to an HTTP response.
    /// </summary>
    /// <typeparam name="T">The type of the value the workflow returns on success.</typeparam>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="action">The workflow operation to run for the resolved caller.</param>
    /// <param name="onSuccess">Maps a successful value to the HTTP response.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is the success response, or a Problem
    /// Details response when the caller cannot be resolved or the workflow fails.
    /// </returns>
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

    /// <summary>
    /// Reads and deserializes a JSON request body without ever buffering more than the configured limit.
    /// </summary>
    /// <typeparam name="T">The request contract to deserialize.</typeparam>
    /// <param name="http">The current HTTP context.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the deserialized body, or fails
    /// when the content type is not JSON, the body is larger than
    /// <see cref="SogaSecurityOptions.MaximumRequestBodyBytes"/>, or the JSON is invalid.
    /// </returns>
    /// <remarks>
    /// The <c>Content-Length</c> header is checked first, and the body is then read in chunks so that a client that
    /// sends no header, or a false one, still cannot make the server buffer an oversized body.
    /// </remarks>
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

    /// <summary>
    /// Parses the optional <c>limit</c> query string value.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="limit">
    /// When this method returns, contains the parsed limit, or <see langword="null"/> when the value is absent.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the value is absent or a non-negative integer; <see langword="false"/> if it is present
    /// but not a valid integer.
    /// </returns>
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

    /// <summary>
    /// Gets the optional <c>cursor</c> query string value.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <returns>The opaque cursor, or <see langword="null"/> when the value is absent or empty.</returns>
    public static string? GetCursor(HttpContext http)
    {
        var raw = http.Request.Query["cursor"].ToString();
        return raw.Length == 0 ? null : raw;
    }
}
