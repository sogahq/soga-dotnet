using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Synchronization.Endpoints;

internal static class SyncEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/sync", SyncAsync)
            .WithName("SogaSync")
            .WithSummary("Returns durable changes after a cursor. Without a cursor it returns the current head cursor and no events.")
            .Produces<SyncResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

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
