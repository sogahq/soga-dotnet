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

/// <summary>
/// Represents the filters and paging of a conversation list request, as read from the query string.
/// </summary>
/// <param name="Limit">The page size, or <see langword="null"/> for the default.</param>
/// <param name="Cursor">The cursor from the previous page, or <see langword="null"/> for the first page.</param>
/// <param name="Kind">An optional kind filter: <c>direct</c> or <c>group</c>.</param>
/// <param name="State">An optional state filter: <c>active</c> (the default), <c>archived</c>, or <c>all</c>.</param>
/// <param name="ExternalReference">An optional filter for conversations about one host reference.</param>
internal sealed record ConversationListRequest(
    int? Limit = null,
    string? Cursor = null,
    string? Kind = null,
    string? State = null,
    string? ExternalReference = null);

/// <summary>
/// Implements creating direct conversations, reading a conversation, and listing the caller's conversations.
/// </summary>
/// <param name="store">The persistence store.</param>
/// <param name="ids">The generator for conversation and event IDs.</param>
/// <param name="clock">The clock that timestamps new conversations.</param>
/// <param name="signal">Wakes the outbox dispatcher when an event is stored.</param>
/// <param name="cursors">Protects and validates conversation-list cursors.</param>
/// <param name="authorizer">Applies host authorization policies.</param>
/// <param name="options">The Soga options that hold paging and metadata limits.</param>
internal sealed class ConversationWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    SogaCursorProtector cursors,
    SogaAuthorizer authorizer,
    IOptions<SogaOptions> options)
{
    /// <summary>
    /// Creates a direct conversation between the caller and another user, or returns the existing one.
    /// </summary>
    /// <param name="caller">The trusted caller, who becomes one of the two participants.</param>
    /// <param name="request">The other user, and an optional external reference and metadata.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the conversation, with
    /// <see cref="CreatedResult{T}.Created"/> set to <see langword="false"/> when it already existed.
    /// </returns>
    /// <remarks>
    /// There is at most one direct conversation per pair of users and external reference (ADR 0007). The database
    /// enforces this, so two simultaneous requests still produce a single conversation.
    /// </remarks>
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

    /// <summary>
    /// Gets a conversation the caller participates in.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the conversation, or fails with
    /// <c>soga.conversation.not_found</c> when it does not exist or the caller is not a participant.
    /// </returns>
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

    /// <summary>
    /// Lists the caller's conversations, most recent activity first.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="request">The filters and paging for the list.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains one page of conversations, each
    /// with its unread count and last-message preview, and a cursor for the next page.
    /// </returns>
    /// <remarks>
    /// The filters and the caller are bound into the cursor, so a cursor cannot be replayed under different filters or
    /// by another user.
    /// </remarks>
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
    /// Creates the cursor that continues a conversation list after the current page.
    /// </summary>
    /// <param name="caller">The caller the cursor belongs to.</param>
    /// <param name="scope">The cursor scope, which includes the caller and the filters.</param>
    /// <param name="items">The conversations on the current page.</param>
    /// <param name="previousActivity">The activity time from the cursor that produced this page, if any.</param>
    /// <param name="previousSkip">The tie count from the cursor that produced this page.</param>
    /// <returns>An opaque cursor string.</returns>
    /// <remarks>
    /// The position is the last item's activity time plus how many items with exactly that time have already been
    /// returned. Skipping that many ties is stable and needs no secondary ID ordering, so it behaves the same on every
    /// database. When a whole page shares one activity time, the tie count carries over from the previous cursor.
    /// </remarks>
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

    /// <summary>
    /// Validates a conversation-list cursor and reads its position.
    /// </summary>
    /// <param name="cursor">The cursor supplied by the client.</param>
    /// <param name="caller">The caller the cursor must belong to.</param>
    /// <param name="scope">The scope the cursor must have been issued for.</param>
    /// <param name="activityAt">When this method returns <see langword="true"/>, contains the activity time to continue after.</param>
    /// <param name="skip">When this method returns <see langword="true"/>, contains the number of tied items to skip.</param>
    /// <returns>
    /// <see langword="true"/> if the cursor is authentic, matches the scope, and holds a valid position; otherwise,
    /// <see langword="false"/>.
    /// </returns>
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

/// <summary>
/// Provides the page-size rule shared by every paged endpoint.
/// </summary>
internal static class SogaPaging
{
    /// <summary>
    /// Resolves the effective page size for a request.
    /// </summary>
    /// <param name="requested">The page size the client asked for, or <see langword="null"/> for the default.</param>
    /// <param name="defaultSize">The page size used when none is requested.</param>
    /// <param name="maximumSize">The largest page size allowed.</param>
    /// <returns>
    /// <paramref name="defaultSize"/> when nothing is requested, the requested size clamped to
    /// <paramref name="maximumSize"/>, or <see langword="null"/> when the requested size is less than 1.
    /// </returns>
    public static int? ResolveLimit(int? requested, int defaultSize, int maximumSize)
    {
        if (requested is null)
        {
            return defaultSize;
        }

        return requested < 1 ? null : Math.Min(requested.Value, maximumSize);
    }
}
