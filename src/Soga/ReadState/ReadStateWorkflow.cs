using Soga.Abstractions;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;

namespace Soga.ReadState;

/// <summary>Owns read-position updates and unread counts.</summary>
internal sealed class ReadStateWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal)
{
    public async Task<SogaResult<ReadStateResponse>> AdvanceAsync(
        SogaCaller caller,
        Guid conversationId,
        MarkReadRequest? request,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.read.advance");
        return SogaDiagnostics.Track("read.advance", await ExecuteAsync());

        async Task<SogaResult<ReadStateResponse>> ExecuteAsync()
        {
            if (request is null || request.MessageId == Guid.Empty)
            {
                return SogaResult<ReadStateResponse>.Fail(SogaErrors.InvalidRequest("messageId is required."));
            }

            var outcome = await store.AdvanceReadPositionAsync(
                new AdvanceReadCommand(
                    caller.TenantId,
                    caller.UserId,
                    conversationId,
                    request.MessageId,
                    ids.NewId(),
                    clock.GetUtcNow()),
                cancellationToken);

            if (outcome.Status == AdvanceStatus.Advanced)
            {
                signal.Notify();
            }

            return outcome.Status switch
            {
                AdvanceStatus.Advanced or AdvanceStatus.Unchanged => SogaResult<ReadStateResponse>.Ok(
                    new ReadStateResponse(conversationId, outcome.LastReadSequence, outcome.UnreadCount)),
                AdvanceStatus.MessageNotFound => SogaResult<ReadStateResponse>.Fail(SogaErrors.MessageNotFound),
                _ => SogaResult<ReadStateResponse>.Fail(SogaErrors.ConversationNotFound),
            };
        }
    }

    public async Task<SogaResult<UnreadResponse>> GetTotalUnreadAsync(SogaCaller caller, CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.read.unread");
        var total = await store.GetTotalUnreadAsync(caller.TenantId, caller.UserId, cancellationToken);
        return SogaDiagnostics.Track("read.unread", SogaResult<UnreadResponse>.Ok(new UnreadResponse(total)));
    }
}
