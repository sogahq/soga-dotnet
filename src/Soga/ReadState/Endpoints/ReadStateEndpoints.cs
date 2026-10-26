using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soga.Contracts;
using Soga.Extensions;

namespace Soga.ReadState.Endpoints;

/// <summary>
/// Maps the HTTP endpoints for read positions and unread counts.
/// </summary>
internal static class ReadStateEndpoints
{
    /// <summary>
    /// Adds the read-state endpoints to the Soga route group.
    /// </summary>
    /// <param name="group">The Soga route group created by <see cref="SogaEndpointRouteBuilderExtensions.MapSoga"/>.</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/conversations/{id:guid}/read", AdvanceAsync)
            .WithName("SogaMarkRead")
            .WithSummary("Advances the caller's read position through a message. The position never moves backward.")
            .Accepts<MarkReadRequest>("application/json")
            .Produces<ReadStateResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/unread", GetUnreadAsync)
            .WithName("SogaGetUnread")
            .WithSummary("Returns the caller's total unread message count.")
            .Produces<UnreadResponse>();
    }

    /// <summary>
    /// Handles <c>POST /conversations/{id}/read</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The conversation ID from the route.</param>
    /// <param name="workflow">The read-state workflow.</param>
    /// <returns>A task whose result is the caller's read position and unread count after the update.</returns>
    private static async Task<IResult> AdvanceAsync(HttpContext http, Guid id, ReadStateWorkflow workflow)
    {
        var body = await SogaHttp.ReadBodyAsync<MarkReadRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.AdvanceAsync(caller, id, request, ct),
            state => Results.Ok(state));
    }

    /// <summary>
    /// Handles <c>GET /unread</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="workflow">The read-state workflow.</param>
    /// <returns>A task whose result is the caller's total unread count across all conversations.</returns>
    private static Task<IResult> GetUnreadAsync(HttpContext http, ReadStateWorkflow workflow) =>
        SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.GetTotalUnreadAsync(caller, ct),
            unread => Results.Ok(unread));
}
