using System.Globalization;
using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Authorization;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;
using Soga.Synchronization;

namespace Soga.Conversations;

/// <summary>The optional filters and paging of a conversation list request.</summary>
internal sealed record ConversationListRequest(
    int? Limit = null,
    string? Cursor = null,
    string? Kind = null,
    string? State = null,
    string? ExternalReference = null);

/// <summary>Owns authorization, validation, and result mapping for direct conversations and conversation listing.</summary>
internal sealed class ConversationWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    SogaCursorProtector cursors,
    SogaAuthorizer authorizer,
    IOptions<SogaOptions> options)
{
    public async Task<SogaResult<CreatedResult<ConversationResponse>>> CreateDirectAsync(
        SogaCaller caller,
        CreateDirectConversationRequest? request,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.conversation.create");
        return SogaDiagnostics.Track("conversation.create", await ExecuteAsync());

        async Task<SogaResult<CreatedResult<ConversationResponse>>> ExecuteAsync()
        {
            var otherUserId = SogaIdentity.NormalizeUserId(request?.OtherUserId);
            if (otherUserId is null)
            {
                return Fail(SogaErrors.InvalidRequest($"otherUserId is required and cannot exceed {SogaOptions.MaximumUserIdLength} characters."));
            }

            if (string.Equals(otherUserId, caller.UserId, StringComparison.Ordinal))
            {
                return Fail(SogaErrors.InvalidRequest("A direct conversation requires two different users."));
            }

            var externalReference = request!.ExternalReference?.Trim();
            if (string.IsNullOrEmpty(externalReference))
            {
                externalReference = null;
            }
            else if (externalReference.Length > SogaOptions.MaximumExternalReferenceLength)
            {
                return Fail(SogaErrors.InvalidRequest($"externalReference cannot exceed {SogaOptions.MaximumExternalReferenceLength} characters."));
            }

            var (metadataJson, metadataError) = SogaMetadata.Validate(request.Metadata, options.Value.Conversations);
            if (metadataError is not null)
            {
                return Fail(SogaErrors.MetadataInvalid(metadataError));
            }

            var denied = await authorizer.CheckAsync(
                SogaAction.CreateDirectConversation,
                caller,
                otherUserId,
                externalReference,
                cancellationToken);
            if (denied is not null)
            {
                return Fail(denied);
            }

            var outcome = await store.GetOrCreateDirectConversationAsync(
                new CreateDirectConversationCommand(
                    caller.TenantId,
                    caller.UserId,
                    otherUserId,
                    externalReference,
                    ids.NewId(),
                    ids.NewId(),
                    clock.GetUtcNow(),
                    metadataJson),
                cancellationToken);

            if (outcome.Created)
            {
                signal.Notify();
            }

            return SogaResult<CreatedResult<ConversationResponse>>.Ok(
                new CreatedResult<ConversationResponse>(outcome.Conversation.ToResponse(), outcome.Created));
        }

        static SogaResult<CreatedResult<ConversationResponse>> Fail(SogaError error) =>
            SogaResult<CreatedResult<ConversationResponse>>.Fail(error);
    }

    public async Task<SogaResult<ConversationResponse>> GetAsync(
        SogaCaller caller,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.conversation.get");
        var conversation = await store.GetConversationAsync(
            caller.TenantId,
            caller.UserId,
            conversationId,
            cancellationToken);

        return SogaDiagnostics.Track(
            "conversation.get",
            conversation is null
                ? SogaResult<ConversationResponse>.Fail(SogaErrors.ConversationNotFound)
                : SogaResult<ConversationResponse>.Ok(conversation.ToResponse()));
    }

    public async Task<SogaResult<ConversationPageResponse>> ListAsync(
        SogaCaller caller,
        ConversationListRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.conversation.list");
        return SogaDiagnostics.Track("conversation.list", await ExecuteAsync());

        async Task<SogaResult<ConversationPageResponse>> ExecuteAsync()
        {
            var paging = options.Value.Conversations;
            var limit = SogaPaging.ResolveLimit(request.Limit, paging.DefaultPageSize, paging.MaximumPageSize);
            if (limit is null)
            {
                return SogaResult<ConversationPageResponse>.Fail(SogaErrors.InvalidRequest("limit must be at least 1."));
            }

            string? kind = null;
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                kind = request.Kind.Trim().ToLowerInvariant() switch
                {
                    "direct" => SogaConversationKinds.Direct,
                    "group" => SogaConversationKinds.Group,
                    _ => null,
                };

                if (kind is null)
                {
                    return SogaResult<ConversationPageResponse>.Fail(SogaErrors.InvalidRequest("kind must be 'direct' or 'group'."));
                }
            }

            var state = (request.State?.Trim().ToLowerInvariant()) switch
            {
                null or "" or "active" => ConversationStateFilter.Active,
                "archived" => ConversationStateFilter.Archived,
                "all" => ConversationStateFilter.All,
                _ => (ConversationStateFilter?)null,
            };

            if (state is null)
            {
                return SogaResult<ConversationPageResponse>.Fail(SogaErrors.InvalidRequest("state must be 'active', 'archived', or 'all'."));
            }

            var reference = request.ExternalReference?.Trim();
            if (string.IsNullOrEmpty(reference))
            {
                reference = null;
            }
            else if (reference.Length > SogaOptions.MaximumExternalReferenceLength)
            {
                return SogaResult<ConversationPageResponse>.Fail(SogaErrors.InvalidRequest("externalReference is too long."));
            }

            // The filters are part of the cursor scope, so a cursor cannot be replayed under different filters or by another user.
            var scope = $"{caller.UserId}|{kind}|{state}|{reference}";
            DateTimeOffset? afterActivity = null;
            var skipTies = 0;
            if (request.Cursor is not null)
            {
                if (!TryDecodePosition(request.Cursor, caller, scope, out var activityAt, out skipTies))
                {
                    return SogaResult<ConversationPageResponse>.Fail(SogaErrors.CursorInvalid);
                }

                afterActivity = activityAt;
            }

            var page = await store.ListConversationsAsync(
                new ListConversationsQuery(
                    caller.TenantId,
                    caller.UserId,
                    limit.Value,
                    kind,
                    state.Value,
                    reference,
                    afterActivity,
                    skipTies),
                cancellationToken);

            var nextCursor = page.HasMore && page.Items.Count > 0
                ? EncodePosition(caller, scope, page.Items, afterActivity, skipTies)
                : null;

            return SogaResult<ConversationPageResponse>.Ok(
                new ConversationPageResponse([.. page.Items.Select(c => c.ToResponse())], nextCursor));
        }
    }

    /// <summary>
    /// The cursor is the last item's activity time plus how many items with exactly that time were already returned.
    /// Skipping that many ties is stable and needs no ID ordering, so it behaves the same on every database.
    /// </summary>
    private string EncodePosition(
        SogaCaller caller,
        string scope,
        IReadOnlyList<ConversationRecord> items,
        DateTimeOffset? previousActivity,
        int previousSkip)
    {
        var lastActivity = items[^1].LastActivityAt;
        var tied = items.Count(c => c.LastActivityAt == lastActivity);
        var skip = previousActivity == lastActivity && tied == items.Count ? previousSkip + tied : tied;

        return cursors.EncodeValue(
            SogaCursorKind.ConversationList,
            caller.TenantId,
            scope,
            string.Create(CultureInfo.InvariantCulture, $"{lastActivity.UtcTicks}.{skip}"));
    }

    private bool TryDecodePosition(string cursor, SogaCaller caller, string scope, out DateTimeOffset activityAt, out int skip)
    {
        activityAt = default;
        skip = 0;
        if (!cursors.TryDecodeValue(cursor, SogaCursorKind.ConversationList, caller.TenantId, scope, out var value))
        {
            return false;
        }

        var parts = value.Split('.', 2);
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out skip) ||
            ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        activityAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}

internal static class SogaPaging
{
    /// <summary>Returns the effective page size, or <see langword="null"/> when the request is invalid. Oversized requests are clamped.</summary>
    public static int? ResolveLimit(int? requested, int defaultSize, int maximumSize)
    {
        if (requested is null)
        {
            return defaultSize;
        }

        return requested < 1 ? null : Math.Min(requested.Value, maximumSize);
    }
}
