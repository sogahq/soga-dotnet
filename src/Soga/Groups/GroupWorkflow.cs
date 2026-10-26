using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Authorization;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;

namespace Soga.Groups;

/// <summary>
/// Owns group creation and management. Every change runs through <see cref="GroupRules"/> inside a store transaction,
/// so concurrent membership and ownership changes have a deterministic outcome.
/// </summary>
internal sealed class GroupWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    SogaAuthorizer authorizer,
    SogaAuditor auditor,
    IOptions<SogaOptions> options)
{
    public async Task<SogaResult<ConversationResponse>> CreateAsync(
        SogaCaller caller,
        CreateGroupRequest? request,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.group.create");
        return SogaDiagnostics.Track("group.create", await ExecuteAsync());

        async Task<SogaResult<ConversationResponse>> ExecuteAsync()
        {
            var settings = options.Value;
            if (!settings.Groups.Enabled)
            {
                return Fail(SogaErrors.GroupsDisabled);
            }

            var title = NormalizeTitle(request?.Title);
            if (title is null)
            {
                return Fail(SogaErrors.InvalidRequest($"title is required and cannot exceed {settings.Groups.MaximumTitleLength} characters."));
            }

            var others = new List<string>();
            foreach (var raw in request!.ParticipantUserIds ?? [])
            {
                var userId = SogaIdentity.NormalizeUserId(raw);
                if (userId is null)
                {
                    return Fail(SogaErrors.InvalidRequest($"Every participant ID is required and cannot exceed {SogaOptions.MaximumUserIdLength} characters."));
                }

                if (!string.Equals(userId, caller.UserId, StringComparison.Ordinal) && !others.Contains(userId, StringComparer.Ordinal))
                {
                    others.Add(userId);
                }
            }

            if (others.Count + 1 > settings.Groups.MaximumParticipants)
            {
                return Fail(SogaErrors.GroupTooLarge);
            }

            var externalReference = request.ExternalReference?.Trim();
            if (string.IsNullOrEmpty(externalReference))
            {
                externalReference = null;
            }
            else if (externalReference.Length > SogaOptions.MaximumExternalReferenceLength)
            {
                return Fail(SogaErrors.InvalidRequest($"externalReference cannot exceed {SogaOptions.MaximumExternalReferenceLength} characters."));
            }

            var (metadataJson, metadataError) = SogaMetadata.Validate(request.Metadata, settings.Conversations);
            if (metadataError is not null)
            {
                return Fail(SogaErrors.MetadataInvalid(metadataError));
            }

            var denied = await authorizer.CheckAsync(SogaAction.CreateGroup, caller, null, externalReference, cancellationToken);
            if (denied is not null)
            {
                return Fail(denied);
            }

            var conversation = await store.CreateGroupAsync(
                new CreateGroupCommand(
                    caller.TenantId,
                    caller.UserId,
                    title,
                    others,
                    externalReference,
                    metadataJson,
                    ids.NewId(),
                    ids.NewId(),
                    clock.GetUtcNow()),
                cancellationToken);

            signal.Notify();
            await auditor.RecordAsync("group.created", caller.TenantId, caller.UserId, conversation.Id, null, $"participants={others.Count + 1}", cancellationToken);
            return SogaResult<ConversationResponse>.Ok(conversation.ToResponse());
        }
    }

    public Task<SogaResult<ConversationResponse?>> UpdateAsync(
        SogaCaller caller,
        Guid conversationId,
        UpdateConversationRequest? request,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var titleGiven = request?.Title is not null;
        var metadataGiven = request?.Metadata is not null;
        if (request is null || (!titleGiven && !metadataGiven))
        {
            return Task.FromResult(SogaResult<ConversationResponse?>.Fail(SogaErrors.InvalidRequest("Provide a title, metadata, or both.")));
        }

        string? title = null;
        if (titleGiven)
        {
            title = NormalizeTitle(request.Title);
            if (title is null)
            {
                return Task.FromResult(SogaResult<ConversationResponse?>.Fail(
                    SogaErrors.InvalidRequest($"title cannot be empty or exceed {settings.Groups.MaximumTitleLength} characters.")));
            }
        }

        string? metadataJson = null;
        if (metadataGiven)
        {
            var (json, error) = SogaMetadata.Validate(request.Metadata, settings.Conversations);
            if (error is not null)
            {
                return Task.FromResult(SogaResult<ConversationResponse?>.Fail(SogaErrors.MetadataInvalid(error)));
            }

            metadataJson = json;
        }

        return MutateAsync(
            caller,
            conversationId,
            new UpdateConversationMutation(titleGiven, title, metadataGiven, metadataJson),
            SogaAction.UpdateConversation,
            targetUserId: null,
            "group.updated",
            detail: null,
            "group.update",
            cancellationToken);
    }

    public Task<SogaResult<ConversationResponse?>> ArchiveAsync(
        SogaCaller caller,
        Guid conversationId,
        bool archived,
        CancellationToken cancellationToken) =>
        MutateAsync(
            caller,
            conversationId,
            new ArchiveMutation(archived),
            SogaAction.ArchiveConversation,
            targetUserId: null,
            archived ? "group.archived" : "group.unarchived",
            detail: null,
            archived ? "group.archive" : "group.unarchive",
            cancellationToken);

    public Task<SogaResult<ConversationResponse?>> AddParticipantAsync(
        SogaCaller caller,
        Guid conversationId,
        AddParticipantRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = SogaIdentity.NormalizeUserId(request?.UserId);
        if (userId is null)
        {
            return Task.FromResult(SogaResult<ConversationResponse?>.Fail(
                SogaErrors.InvalidRequest($"userId is required and cannot exceed {SogaOptions.MaximumUserIdLength} characters.")));
        }

        if (!SogaRoles.TryParse(request!.Role, out var role))
        {
            return Task.FromResult(SogaResult<ConversationResponse?>.Fail(
                SogaErrors.InvalidRequest("role must be 'member', 'admin', or 'owner'.")));
        }

        return MutateAsync(
            caller,
            conversationId,
            new AddParticipantMutation(userId, role),
            SogaAction.AddParticipant,
            userId,
            "participant.added",
            $"role={SogaRoles.ToWire(role)}",
            "group.participant.add",
            cancellationToken);
    }

    /// <summary>Removes a participant, or, when <paramref name="userId"/> is the caller, leaves the group.</summary>
    public Task<SogaResult<ConversationResponse?>> RemoveParticipantAsync(
        SogaCaller caller,
        Guid conversationId,
        string? userId,
        CancellationToken cancellationToken)
    {
        var target = SogaIdentity.NormalizeUserId(userId);
        if (target is null)
        {
            return Task.FromResult(SogaResult<ConversationResponse?>.Fail(SogaErrors.InvalidRequest("A valid user ID is required.")));
        }

        var isSelf = string.Equals(target, caller.UserId, StringComparison.Ordinal);
        return MutateAsync(
            caller,
            conversationId,
            new RemoveParticipantMutation(target),
            SogaAction.RemoveParticipant,
            target,
            isSelf ? "participant.left" : "participant.removed",
            detail: null,
            "group.participant.remove",
            cancellationToken);
    }

    public Task<SogaResult<ConversationResponse?>> ChangeRoleAsync(
        SogaCaller caller,
        Guid conversationId,
        string? userId,
        ChangeRoleRequest? request,
        CancellationToken cancellationToken)
    {
        var target = SogaIdentity.NormalizeUserId(userId);
        if (target is null)
        {
            return Task.FromResult(SogaResult<ConversationResponse?>.Fail(SogaErrors.InvalidRequest("A valid user ID is required.")));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Role) || !SogaRoles.TryParse(request.Role, out var role))
        {
            return Task.FromResult(SogaResult<ConversationResponse?>.Fail(
                SogaErrors.InvalidRequest("role must be 'member', 'admin', or 'owner'.")));
        }

        return MutateAsync(
            caller,
            conversationId,
            new ChangeRoleMutation(target, role),
            SogaAction.ChangeRole,
            target,
            "participant.role_changed",
            $"role={SogaRoles.ToWire(role)}",
            "group.participant.role",
            cancellationToken);
    }

    private async Task<SogaResult<ConversationResponse?>> MutateAsync(
        SogaCaller caller,
        Guid conversationId,
        GroupMutation mutation,
        SogaAction action,
        string? targetUserId,
        string auditAction,
        string? detail,
        string commandName,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity($"soga.{commandName}");
        return SogaDiagnostics.Track(commandName, await ExecuteAsync());

        async Task<SogaResult<ConversationResponse?>> ExecuteAsync()
        {
            var settings = options.Value;
            if (!settings.Groups.Enabled)
            {
                return SogaResult<ConversationResponse?>.Fail(SogaErrors.GroupsDisabled);
            }

            // Membership is confirmed before any host policy runs, so outsiders only ever see "not found".
            var denied = await authorizer.CheckAsync(action, caller, conversationId, targetUserId, messageId: null, cancellationToken);
            if (denied is not null)
            {
                return SogaResult<ConversationResponse?>.Fail(denied);
            }

            var outcome = await store.MutateGroupAsync(
                new GroupMutationCommand(
                    caller.TenantId,
                    caller.UserId,
                    conversationId,
                    mutation,
                    settings.Groups.MaximumParticipants,
                    settings.Groups.PostSystemMessages,
                    ids.NewId(),
                    ids,
                    clock.GetUtcNow()),
                cancellationToken);

            var error = outcome.Error switch
            {
                GroupError.None => null,
                GroupError.NotFound => SogaErrors.ConversationNotFound,
                GroupError.NotGroup => SogaErrors.ConversationNotGroup,
                GroupError.Forbidden => SogaErrors.AuthorizationDenied,
                GroupError.LastOwner => SogaErrors.GroupLastOwner,
                GroupError.TooLarge => SogaErrors.GroupTooLarge,
                GroupError.ParticipantNotFound => SogaErrors.ParticipantNotFound,
                _ => SogaErrors.InternalError,
            };

            if (error is not null)
            {
                return SogaResult<ConversationResponse?>.Fail(error);
            }

            if (outcome.Changed)
            {
                signal.Notify();
                await auditor.RecordAsync(auditAction, caller.TenantId, caller.UserId, conversationId, targetUserId, detail, cancellationToken);
            }

            return SogaResult<ConversationResponse?>.Ok(outcome.Conversation?.ToResponse());
        }
    }

    private string? NormalizeTitle(string? raw)
    {
        var title = raw?.Trim();
        return string.IsNullOrEmpty(title) || title.Length > options.Value.Groups.MaximumTitleLength ? null : title;
    }

    private static SogaResult<ConversationResponse> Fail(SogaError error) => SogaResult<ConversationResponse>.Fail(error);
}
