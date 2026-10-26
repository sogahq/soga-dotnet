using Soga.Abstractions;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;

namespace Soga.ReadState;

/// <summary>
/// Implements read positions and unread counts.
/// </summary>
/// <param name="store">The persistence store.</param>
/// <param name="ids">The generator for event IDs.</param>
/// <param name="clock">The clock that timestamps read events.</param>
/// <param name="signal">Wakes the outbox dispatcher when a read event is stored.</param>
/// <remarks>
/// Each participant has one read position per conversation, stored as the sequence of the last message they read. The
/// position only ever moves forward, so a late or repeated request can never mark messages as unread again.
/// </remarks>
internal sealed class ReadStateWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal)
{
    /// <summary>
    /// Advances the caller's read position through the specified message.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="request">The request that names the last message read.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the read position and unread count
    /// after the update. It is also successful, and unchanged, when the message is at or before the current position.
    /// </returns>
    /// <remarks>
    /// A <c>ReadPositionChanged</c> event is published only when the position actually moves.
    /// </remarks>
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

    /// <summary>
    /// Gets the caller's total number of unread messages across all their conversations.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the total unread count.</returns>
    public async Task<SogaResult<UnreadResponse>> GetTotalUnreadAsync(SogaCaller caller, CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.read.unread");
        var total = await store.GetTotalUnreadAsync(caller.TenantId, caller.UserId, cancellationToken);
        return SogaDiagnostics.Track("read.unread", SogaResult<UnreadResponse>.Ok(new UnreadResponse(total)));
    }
}
