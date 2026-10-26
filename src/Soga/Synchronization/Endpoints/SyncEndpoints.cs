using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Synchronization.Endpoints;

/// <summary>
/// Maps the HTTP endpoint that reconnecting clients use to recover missed changes.
/// </summary>
internal static class SyncEndpoints
{
    /// <summary>
    /// Adds the sync endpoint to the Soga route group.
    /// </summary>
    /// <param name="group">The Soga route group created by <see cref="SogaEndpointRouteBuilderExtensions.MapSoga"/>.</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/sync", SyncAsync)
            .WithName("SogaSync")
            .WithSummary("Returns durable changes after a cursor. Without a cursor it returns the current head cursor and no events.")
            .Produces<SyncResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Handles <c>GET /sync</c>, reading the <c>limit</c> and <c>cursor</c> query values.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="workflow">The sync workflow.</param>
    /// <returns>A task whose result is the next batch of changes and the cursor to continue from.</returns>
    private static Task<IResult> SyncAsync(HttpContext http, SyncWorkflow workflow)
    {
        if (!SogaHttp.TryGetLimit(http, out var limit))
        {
            return Task.FromResult(SogaHttp.Problem(http, SogaErrors.InvalidRequest("limit must be an integer.")));
        }

        var cursor = SogaHttp.GetCursor(http);
        return SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.SyncAsync(caller, cursor, limit, ct),
            response => Results.Ok(response));
    }
}
