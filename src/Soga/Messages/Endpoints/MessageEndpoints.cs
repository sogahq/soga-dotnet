using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Messages.Endpoints;

internal static class MessageEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/conversations/{id:guid}/messages", GetHistoryAsync)
            .WithName("SogaGetMessages")
            .WithSummary("Returns message history newest first. Pass the returned cursor to load older messages.")
            .Produces<MessagePageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/conversations/{id:guid}/messages", SendAsync)
            .WithName("SogaSendMessage")
            .WithSummary("Sends a text message. Retrying with the same clientMessageId returns the stored message with 200.")
            .Accepts<SendMessageRequest>("application/json")
            .Produces<MessageResponse>(StatusCodes.Status201Created)
            .Produces<MessageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static Task<IResult> GetHistoryAsync(HttpContext http, Guid id, MessageWorkflow workflow)
    {
        if (!SogaHttp.TryGetLimit(http, out var limit))
        {
            return Task.FromResult(SogaHttp.Problem(http, SogaErrors.InvalidRequest("limit must be an integer.")));
        }

        var cursor = SogaHttp.GetCursor(http);
        return SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.GetHistoryAsync(caller, id, cursor, limit, ct),
            page => Results.Ok(page));
    }

    private static async Task<IResult> SendAsync(HttpContext http, Guid id, MessageWorkflow workflow)
    {
        var body = await SogaHttp.ReadBodyAsync<SendMessageRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.SendAsync(caller, id, request, ct),
            result => result.Created
                ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
                : Results.Ok(result.Value));
    }
}
