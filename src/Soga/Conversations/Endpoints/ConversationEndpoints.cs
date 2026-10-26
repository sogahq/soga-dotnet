using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Conversations.Endpoints;

/// <summary>
/// Maps the HTTP endpoints for creating, listing, and reading conversations.
/// </summary>
/// <remarks>
/// Endpoints only translate HTTP to workflow calls. Validation and authorization live in
/// <see cref="ConversationWorkflow"/>.
/// </remarks>
internal static class ConversationEndpoints
{
    /// <summary>
    /// Adds the conversation endpoints to the Soga route group.
    /// </summary>
    /// <param name="group">The Soga route group created by <see cref="SogaEndpointRouteBuilderExtensions.MapSoga"/>.</param>
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
            .WithSummary("Lists the caller's conversations, most recent activity first, with unread counts and a last-message preview. Filters: kind=direct|group, state=active|archived|all, externalReference. Page with cursor.")
            .Produces<ConversationPageResponse>();

        group.MapGet("/conversations/{id:guid}", GetAsync)
            .WithName("SogaGetConversation")
            .WithSummary("Gets one conversation the caller participates in, including its participants and roles.")
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Handles <c>POST /conversations/direct</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="workflow">The conversation workflow.</param>
    /// <param name="options">The Soga options, used to build the <c>Location</c> header.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is 201 with a <c>Location</c> header when the
    /// conversation was created, or 200 when it already existed.
    /// </returns>
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

    /// <summary>
    /// Handles <c>GET /conversations</c>, reading the <c>limit</c>, <c>cursor</c>, <c>kind</c>, <c>state</c>, and
    /// <c>externalReference</c> query values.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="workflow">The conversation workflow.</param>
    /// <returns>A task whose result is one page of the caller's conversations.</returns>
    private static Task<IResult> ListAsync(HttpContext http, ConversationWorkflow workflow)
    {
        if (!SogaHttp.TryGetLimit(http, out var limit))
        {
            return Task.FromResult(SogaHttp.Problem(http, SogaErrors.InvalidRequest("limit must be an integer.")));
        }

        var request = new ConversationListRequest(
            limit,
            SogaHttp.GetCursor(http),
            Query(http, "kind"),
            Query(http, "state"),
            Query(http, "externalReference"));

        return SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.ListAsync(caller, request, ct),
            page => Results.Ok(page));
    }

    /// <summary>
    /// Handles <c>GET /conversations/{id}</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The conversation ID from the route.</param>
    /// <param name="workflow">The conversation workflow.</param>
    /// <returns>A task whose result is the conversation, or 404 when the caller is not a participant.</returns>
    private static Task<IResult> GetAsync(HttpContext http, Guid id, ConversationWorkflow workflow) =>
        SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.GetAsync(caller, id, ct),
            conversation => Results.Ok(conversation));

    /// <summary>
    /// Gets an optional query string value.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="name">The query string key.</param>
    /// <returns>The value, or <see langword="null"/> when it is absent or empty.</returns>
    private static string? Query(HttpContext http, string name)
    {
        var value = http.Request.Query[name].ToString();
        return value.Length == 0 ? null : value;
    }
}
