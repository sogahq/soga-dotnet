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
/// Serves durable changes to reconnecting clients. Realtime events are notifications; this workflow is how a
/// client recovers everything it missed. Authorization is re-evaluated on every request.
/// </summary>
internal sealed class SyncWorkflow(
    ISogaStore store,
    SogaCursorProtector cursors,
    IOptions<SogaOptions> options)
{
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

    private string Encode(SogaCaller caller, long position) =>
        cursors.Encode(SogaCursorKind.Sync, caller.TenantId, caller.UserId, position);
}
