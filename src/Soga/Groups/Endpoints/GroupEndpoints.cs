using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Groups.Endpoints;

/// <summary>
/// Maps the HTTP endpoints for creating and managing group conversations and their participants.
/// </summary>
/// <remarks>
/// Endpoints only translate HTTP to workflow calls. Role checks and validation live in <see cref="GroupWorkflow"/>
/// and <see cref="GroupRules"/>.
/// </remarks>
internal static class GroupEndpoints
{
    /// <summary>
    /// Adds the group endpoints to the Soga route group.
    /// </summary>
    /// <param name="group">The Soga route group created by <see cref="SogaEndpointRouteBuilderExtensions.MapSoga"/>.</param>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/conversations/groups", CreateAsync)
            .WithName("SogaCreateGroup")
            .WithSummary("Creates a group. The caller becomes its owner.")
            .Accepts<CreateGroupRequest>("application/json")
            .Produces<ConversationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/conversations/{id:guid}", UpdateAsync)
            .WithName("SogaUpdateConversation")
            .WithSummary("Changes a group's title and/or metadata. Owner or admin.")
            .Accepts<UpdateConversationRequest>("application/json")
            .Produces<ConversationResponse>();

        group.MapPost("/conversations/{id:guid}/archive", (HttpContext http, Guid id, GroupWorkflow workflow) =>
            Run(http, (caller, ct) => workflow.ArchiveAsync(caller, id, archived: true, ct)))
            .WithName("SogaArchiveConversation")
            .WithSummary("Archives a group: it stays readable but cannot receive new messages. Owner or admin.")
            .Produces<ConversationResponse>();

        group.MapPost("/conversations/{id:guid}/unarchive", (HttpContext http, Guid id, GroupWorkflow workflow) =>
            Run(http, (caller, ct) => workflow.ArchiveAsync(caller, id, archived: false, ct)))
            .WithName("SogaUnarchiveConversation")
            .WithSummary("Restores an archived group. Owner or admin.")
            .Produces<ConversationResponse>();

        group.MapPost("/conversations/{id:guid}/participants", AddParticipantAsync)
            .WithName("SogaAddParticipant")
            .WithSummary("Adds a participant. Owner or admin; only an owner can grant admin or owner.")
            .Accepts<AddParticipantRequest>("application/json")
            .Produces<ConversationResponse>();

        group.MapDelete("/conversations/{id:guid}/participants/{userId}", (HttpContext http, Guid id, string userId, GroupWorkflow workflow) =>
            Run(http, (caller, ct) => workflow.RemoveParticipantAsync(caller, id, userId, ct)))
            .WithName("SogaRemoveParticipant")
            .WithSummary("Removes a participant, or leaves the group when userId is the caller. Returns 204 when the caller no longer has access.")
            .Produces<ConversationResponse>()
            .Produces(StatusCodes.Status204NoContent);

        group.MapPut("/conversations/{id:guid}/participants/{userId}/role", ChangeRoleAsync)
            .WithName("SogaChangeRole")
            .WithSummary("Changes a participant's role. Owner only. A group always keeps at least one owner.")
            .Accepts<ChangeRoleRequest>("application/json")
            .Produces<ConversationResponse>();
    }

    /// <summary>
    /// Handles <c>POST /conversations/groups</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="workflow">The group workflow.</param>
    /// <param name="options">The Soga options, used to build the <c>Location</c> header.</param>
    /// <returns>A task whose result is 201 with the new group and a <c>Location</c> header.</returns>
    private static async Task<IResult> CreateAsync(
        HttpContext http,
        GroupWorkflow workflow,
        IOptions<SogaOptions> options)
    {
        var body = await SogaHttp.ReadBodyAsync<CreateGroupRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await SogaHttp.RunAsync(
            http,
            (caller, ct) => workflow.CreateAsync(caller, request, ct),
            conversation => Results.Created(
                $"{http.Request.PathBase}{options.Value.RoutePrefix}/conversations/{conversation.Id}",
                conversation));
    }

    /// <summary>
    /// Handles <c>PATCH /conversations/{id}</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The group ID from the route.</param>
    /// <param name="workflow">The group workflow.</param>
    /// <returns>A task whose result is the updated group.</returns>
    private static async Task<IResult> UpdateAsync(HttpContext http, Guid id, GroupWorkflow workflow)
    {
        var body = await SogaHttp.ReadBodyAsync<UpdateConversationRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await Run(http, (caller, ct) => workflow.UpdateAsync(caller, id, request, ct));
    }

    /// <summary>
    /// Handles <c>POST /conversations/{id}/participants</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The group ID from the route.</param>
    /// <param name="workflow">The group workflow.</param>
    /// <returns>A task whose result is the group with its updated participants.</returns>
    private static async Task<IResult> AddParticipantAsync(HttpContext http, Guid id, GroupWorkflow workflow)
    {
        var body = await SogaHttp.ReadBodyAsync<AddParticipantRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await Run(http, (caller, ct) => workflow.AddParticipantAsync(caller, id, request, ct));
    }

    /// <summary>
    /// Handles <c>PUT /conversations/{id}/participants/{userId}/role</c>.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="id">The group ID from the route.</param>
    /// <param name="userId">The ID of the participant whose role changes.</param>
    /// <param name="workflow">The group workflow.</param>
    /// <returns>A task whose result is the group with its updated participants.</returns>
    private static async Task<IResult> ChangeRoleAsync(HttpContext http, Guid id, string userId, GroupWorkflow workflow)
    {
        var body = await SogaHttp.ReadBodyAsync<ChangeRoleRequest>(http);
        if (!body.IsSuccess)
        {
            return SogaHttp.Problem(http, body.Error!);
        }

        var request = body.Value;
        return await Run(http, (caller, ct) => workflow.ChangeRoleAsync(caller, id, userId, request, ct));
    }

    /// <summary>
    /// Runs a group change and maps its result to an HTTP response.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="action">The group workflow operation to run.</param>
    /// <returns>
    /// A task whose result is 200 with the updated group, or 204 when the change removed the caller's own access, for
    /// example when they left the group.
    /// </returns>
    private static Task<IResult> Run(
        HttpContext http,
        Func<Identity.SogaCaller, CancellationToken, Task<SogaResult<ConversationResponse?>>> action) =>
        SogaHttp.RunAsync(
            http,
            action,
            conversation => conversation is null ? Results.NoContent() : Results.Ok(conversation));
}
