using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soga.Contracts;
using Soga.Extensions;

namespace Soga.ReadState.Endpoints;

internal static class ReadStateEndpoints
{
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

    private static Task<IResult> GetUnreadAsync(HttpContext http, ReadStateWorkflow workflow) =>
        SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.GetTotalUnreadAsync(caller, ct),
            unread => Results.Ok(unread));
}
