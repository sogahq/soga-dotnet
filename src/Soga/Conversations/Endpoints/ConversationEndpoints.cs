using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Conversations.Endpoints;

internal static class ConversationEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/conversations/direct", CreateDirectAsync)
            .WithName("SogaCreateDirectConversation")
            .WithSummary("Creates a direct conversation, or returns the existing one for the same user pair and external reference.")
            .Accepts<CreateDirectConversationRequest>("application/json")
            .Produces<ConversationResponse>(StatusCodes.Status200OK)
            .Produces<ConversationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/conversations", ListAsync)
            .WithName("SogaListConversations")
            .WithSummary("Lists the caller's conversations with unread counts, most recent activity first.")
            .Produces<IReadOnlyList<ConversationResponse>>();

        group.MapGet("/conversations/{id:guid}", GetAsync)
            .WithName("SogaGetConversation")
            .WithSummary("Gets one conversation the caller participates in.")
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> CreateDirectAsync(
        HttpContext http,
        ConversationWorkflow workflow,
        IOptions<SogaOptions> options)
    {
        var body = await SogaHttp.ReadBodyAsync<CreateDirectConversationRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.CreateDirectAsync(caller, request, ct),
            result => result.Created
                ? Results.Created(
                    $"{http.Request.PathBase}{options.Value.RoutePrefix}/conversations/{result.Value.Id}",
                    result.Value)
                : Results.Ok(result.Value));
    }

    private static Task<IResult> ListAsync(HttpContext http, ConversationWorkflow workflow)
    {
        if (!SogaHttp.TryGetLimit(http, out var limit))
        {
            return Task.FromResult(SogaHttp.Problem(http, SogaErrors.InvalidRequest("limit must be an integer.")));
        }

        return SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.ListAsync(caller, limit, ct),
            conversations => Results.Ok(conversations));
    }

    private static Task<IResult> GetAsync(HttpContext http, Guid id, ConversationWorkflow workflow) =>
        SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.GetAsync(caller, id, ct),
            conversation => Results.Ok(conversation));
}
