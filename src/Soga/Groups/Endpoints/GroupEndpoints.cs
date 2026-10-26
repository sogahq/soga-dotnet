using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensions;

namespace Soga.Groups.Endpoints;

internal static class GroupEndpoints
{
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

    /// <summary>A group change returns the conversation, or 204 when the caller just lost access to it.</summary>
    private static Task<IResult> Run(
        HttpContext http,
        Func<Identity.SogaCaller, CancellationToken, Task<SogaResult<ConversationResponse?>>> action) =>
        SogaHttp.RunAsync(
            http,
            action,
            conversation => conversation is null ? Results.NoContent() : Results.Ok(conversation));
}
