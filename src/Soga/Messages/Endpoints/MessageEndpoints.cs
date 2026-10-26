using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Messages.Endpoints;

/// <summary>
/// Maps the HTTP endpoints for reading, sending, editing, and deleting messages.
/// </summary>
/// <remarks>
/// Endpoints only translate HTTP to workflow calls. Validation and authorization live in <see cref="MessageWorkflow"/>.
/// </remarks>
internal static class MessageEndpoints
{
    /// <summary>
    /// Adds the message endpoints to the Soga route group.
    /// </summary>
    /// <param name="group">The Soga route group created by <see cref="SogaEndpointRouteBuilderExtensions.MapSoga"/>.</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/conversations/{id:guid}/messages", GetHistoryAsync)
            .WithName("SogaGetMessages")
            .WithSummary("Returns message history newest first. Deleted messages appear as empty tombstones. Pass the returned cursor to load older messages.")
            .Produces<MessagePageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/conversations/{id:guid}/messages", SendAsync)
            .WithName("SogaSendMessage")
            .WithSummary("Sends a text message, optionally as a reply. Retrying with the same clientMessageId returns the stored message with 200.")
            .Accepts<SendMessageRequest>("application/json")
            .Produces<MessageResponse>(StatusCodes.Status201Created)
            .Produces<MessageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/conversations/{id:guid}/messages/{messageId:guid}", EditAsync)
            .WithName("SogaEditMessage")
            .WithSummary("Edits the text of the caller's own message within the edit window. Sequence and sender never change.")
            .Accepts<EditMessageRequest>("application/json")
            .Produces<MessageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/conversations/{id:guid}/messages/{messageId:guid}", DeleteAsync)
            .WithName("SogaDeleteMessage")
            .WithSummary("Deletes a message. The sender may delete their own; in a group an owner or admin may delete others'. Repeating it succeeds.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Handles <c>GET /conversations/{id}/messages</c>, reading the <c>limit</c> and <c>cursor</c> query values.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The conversation ID from the route.</param>
    /// <param name="workflow">The message workflow.</param>
    /// <returns>A task whose result is one page of messages, newest first.</returns>
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

    /// <summary>
    /// Handles <c>POST /conversations/{id}/messages</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The conversation ID from the route.</param>
    /// <param name="workflow">The message workflow.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is 201 for a new message, or 200 with the
    /// stored message for an idempotent retry.
    /// </returns>
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

    /// <summary>
    /// Handles <c>PATCH /conversations/{id}/messages/{messageId}</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The conversation ID from the route.</param>
    /// <param name="messageId">The ID of the message to edit.</param>
    /// <param name="workflow">The message workflow.</param>
    /// <returns>A task whose result is the edited message.</returns>
    private static async Task<IResult> EditAsync(HttpContext http, Guid id, Guid messageId, MessageWorkflow workflow)
    {
        var body = await SogaHttp.ReadBodyAsync<EditMessageRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.EditAsync(caller, id, messageId, request, ct),
            message => Results.Ok(message));
    }

    /// <summary>
    /// Handles <c>DELETE /conversations/{id}/messages/{messageId}</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The conversation ID from the route.</param>
    /// <param name="messageId">The ID of the message to delete.</param>
    /// <param name="workflow">The message workflow.</param>
    /// <returns>A task whose result is 204 when the message is deleted or was already deleted.</returns>
    private static Task<IResult> DeleteAsync(HttpContext http, Guid id, Guid messageId, MessageWorkflow workflow) =>
        SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.DeleteAsync(caller, id, messageId, ct),
            _ => Results.NoContent());
}
