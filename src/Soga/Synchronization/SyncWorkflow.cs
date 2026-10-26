using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;
using Soga.Persistence;

namespace Soga.Synchronization;

/// <summary>
/// Serves the durable changes a client missed while it was disconnected.
/// </summary>
/// <param name="store">The persistence store that holds the change log.</param>
/// <param name="cursors">Protects and validates sync cursors.</param>
/// <param name="options">The Soga options that define the sync batch sizes.</param>
/// <remarks>
/// <para>
/// Realtime events are only notifications; the change log is the source of truth. A client that reconnects calls
/// sync with its last cursor and receives every change it is still allowed to see, in order, with the same event IDs
/// that realtime used, so it can deduplicate.
/// </para>
/// <para>
/// Authorization is evaluated again on every request, so a user who lost access to a conversation stops receiving its
/// changes, apart from the change that tells them they were removed.
/// </para>
/// </remarks>
internal sealed class SyncWorkflow(
    ISogaStore store,
    SogaCursorProtector cursors,
    IOptions<SogaOptions> options)
{
    /// <summary>
    /// Gets the next batch of changes after a cursor.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="cursor">
    /// The cursor returned by the previous call, or <see langword="null"/> to start fresh and receive only the current
    /// head cursor.
    /// </param>
    /// <param name="requestedLimit">The maximum number of changes to return, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the events, the cursor to pass next
    /// time, and whether more changes are waiting. It fails with <c>soga.cursor.invalid</c> when the cursor was
    /// tampered with or belongs to another user or tenant.
    /// </returns>
    public async Task<SogaResult<SyncResponse>> SyncAsync(
        SogaCaller caller,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.sync.execute");
        return SogaDiagnostics.Track("sync.execute", await ExecuteAsync());

        async Task<SogaResult<SyncResponse>> ExecuteAsync()
        {
            var paging = options.Value.Sync;
            var limit = SogaPaging.ResolveLimit(requestedLimit, paging.DefaultBatchSize, paging.MaximumBatchSize);
            if (limit is null)
            {
                return SogaResult<SyncResponse>.Fail(SogaErrors.InvalidRequest("limit must be at least 1."));
            }

            // Without a cursor the client is starting fresh: hand back the current head so it can load state over
            // HTTP and then catch up from here. Call /sync first, then list conversations, to avoid a gap.
            if (cursor is null)
            {
                var head = await store.GetChangeHeadAsync(caller.TenantId, cancellationToken);
                return SogaResult<SyncResponse>.Ok(new SyncResponse([], Encode(caller, head), false));
            }

            if (!cursors.TryDecode(cursor, SogaCursorKind.Sync, caller.TenantId, caller.UserId, out var after))
            {
                return SogaResult<SyncResponse>.Fail(SogaErrors.CursorInvalid);
            }

            var page = await store.GetChangesAsync(caller.TenantId, caller.UserId, after, limit.Value, cancellationToken);
            var events = page.Changes.Select(c => SogaEventSerializer.Deserialize(c.Payload)).ToList();
            var boundary = page.Changes.Count == 0 ? after : page.Changes[^1].Sequence;

            return SogaResult<SyncResponse>.Ok(new SyncResponse(events, Encode(caller, boundary), page.HasMore));
        }
    }

    /// <summary>
    /// Creates a sync cursor bound to the caller's tenant and user.
    /// </summary>
    /// <param name="caller">The caller the cursor belongs to.</param>
    /// <param name="position">The change-log sequence the cursor points after.</param>
    /// <returns>An opaque, tamper-proof cursor string.</returns>
    private string Encode(SogaCaller caller, long position) =>
        cursors.Encode(SogaCursorKind.Sync, caller.TenantId, caller.UserId, position);
}
