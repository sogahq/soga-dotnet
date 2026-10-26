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
/// Implements creating and managing group conversations: details, archiving, participants, and roles.
/// </summary>
/// <param name="store">The persistence store.</param>
/// <param name="ids">The generator for conversation and event IDs.</param>
/// <param name="clock">The clock that timestamps changes.</param>
/// <param name="signal">Wakes the outbox dispatcher when an event is stored.</param>
/// <param name="authorizer">Applies host authorization policies.</param>
/// <param name="auditor">Records every successful group change in the audit trail.</param>
/// <param name="options">The Soga options that hold group limits.</param>
/// <remarks>
/// Every change is decided by <see cref="GroupRules"/> inside a store transaction that locks the group, so concurrent
/// membership and ownership changes always have a deterministic outcome and a group can never lose its last owner.
/// </remarks>
internal sealed class GroupWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    SogaAuthorizer authorizer,
    SogaAuditor auditor,
    IOptions<SogaOptions> options)
{
    /// <summary>
    /// Creates a group with the caller as its owner.
    /// </summary>
    /// <param name="caller">The trusted caller, who becomes the owner.</param>
    /// <param name="request">The title, initial participants, and optional external reference and metadata.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the new group. It fails when groups
    /// are disabled, the input is invalid, the group would be too large, or a host policy denies it.
    /// </returns>
    /// <remarks>
    /// Duplicate participant IDs, and the caller's own ID, are removed from the participant list. Every other
    /// participant joins as a member.
    /// </remarks>
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

    /// <summary>
    /// Changes a group's title, metadata, or both. Requires the owner or admin role.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="request">The new title and/or metadata. Omitted fields are left unchanged.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the updated group.</returns>
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

    /// <summary>
    /// Archives or restores a group. Requires the owner or admin role.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="archived"><see langword="true"/> to archive the group; <see langword="false"/> to restore it.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the updated group.</returns>
    /// <remarks>An archived group stays readable but cannot receive new messages.</remarks>
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

    /// <summary>
    /// Adds a participant to a group. Requires the owner or admin role; only an owner can grant admin or owner.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="request">The user to add and their role, which defaults to member.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the updated group.</returns>
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

    /// <summary>
    /// Removes a participant from a group, or leaves the group when <paramref name="userId"/> is the caller.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="userId">The ID of the participant to remove.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the updated group, or
    /// <see langword="null"/> when the caller left and can no longer see it.
    /// </returns>
    /// <remarks>
    /// A removed user immediately loses HTTP access, sync access, and every live connection to the group.
    /// </remarks>
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

    /// <summary>
    /// Changes a participant's role. Requires the owner role.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="userId">The ID of the participant whose role changes.</param>
    /// <param name="request">The new role.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the updated group. It fails with
    /// <c>soga.group.last_owner</c> when the change would leave the group without an owner.
    /// </returns>
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

    /// <summary>
    /// Runs one group change: checks host policy, applies the change atomically in the store, then publishes and
    /// audits it.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the group.</param>
    /// <param name="mutation">The change to apply.</param>
    /// <param name="action">The action passed to host authorization policies.</param>
    /// <param name="targetUserId">The participant affected by the change, if any.</param>
    /// <param name="auditAction">The audit action recorded when the change succeeds.</param>
    /// <param name="detail">An optional non-sensitive audit detail, such as the new role.</param>
    /// <param name="commandName">The name used for the tracing span and the command metric.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the group after the change, or
    /// <see langword="null"/> when the caller no longer has access to it.
    /// </returns>
    /// <remarks>
    /// Nothing is published or audited when the change is a no-op, such as adding a participant who is already there.
    /// </remarks>
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

    /// <summary>
    /// Trims a group title and checks it against <see cref="SogaGroupOptions.MaximumTitleLength"/>.
    /// </summary>
    /// <param name="raw">The title supplied by the client.</param>
    /// <returns>The trimmed title, or <see langword="null"/> when it is empty or too long.</returns>
    private string? NormalizeTitle(string? raw)
    {
        var title = raw?.Trim();
        return string.IsNullOrEmpty(title) || title.Length > options.Value.Groups.MaximumTitleLength ? null : title;
    }

    /// <summary>
    /// Creates a failed result for <see cref="CreateAsync"/>.
    /// </summary>
    /// <param name="error">The error that describes the failure.</param>
    /// <returns>A failed <see cref="SogaResult{T}"/>.</returns>
    private static SogaResult<ConversationResponse> Fail(SogaError error) => SogaResult<ConversationResponse>.Fail(error);
}
