using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;
using Soga.Synchronization;

namespace Soga.Messages;

/// <summary>Owns authorization, validation, idempotency mapping, and history for messages.</summary>
internal sealed class MessageWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaCursorProtector cursors,
    SogaOutboxSignal signal,
    IOptions<SogaOptions> options)
{
    public async Task<SogaResult<CreatedResult<MessageResponse>>> SendAsync(
        SogaCaller caller,
        Guid conversationId,
        SendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.message.send");
        return SogaDiagnostics.Track("message.send", await ExecuteAsync());

        async Task<SogaResult<CreatedResult<MessageResponse>>> ExecuteAsync()
        {
            var clientMessageId = request?.ClientMessageId?.Trim();
            if (!IsValidClientMessageId(clientMessageId))
            {
                return Fail(SogaErrors.InvalidRequest(
                    $"clientMessageId is required, at most {SogaOptions.MaximumClientMessageIdLength} characters, and may only contain letters, digits, '.', '_', '-' and ':'."));
            }

            var text = request!.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return Fail(SogaErrors.InvalidRequest("text is required."));
            }

            if (text.Length > options.Value.Messages.MaximumTextLength)
            {
                return Fail(SogaErrors.MessageTooLong);
            }

            var outcome = await store.AppendMessageAsync(
                new AppendMessageCommand(
                    caller.TenantId,
                    caller.UserId,
                    conversationId,
                    clientMessageId!,
                    text,
                    ids.NewId(),
                    ids.NewId(),
                    clock.GetUtcNow()),
                cancellationToken);

            if (outcome.Status == AppendStatus.Created)
            {
                signal.Notify();
            }

            return outcome.Status switch
            {
                AppendStatus.Created => SogaResult<CreatedResult<MessageResponse>>.Ok(
                    new CreatedResult<MessageResponse>(outcome.Message!.ToResponse(), true)),
                AppendStatus.Duplicate => SogaResult<CreatedResult<MessageResponse>>.Ok(
                    new CreatedResult<MessageResponse>(outcome.Message!.ToResponse(), false)),
                AppendStatus.IdempotencyConflict => Fail(SogaErrors.IdempotencyConflict),
                _ => Fail(SogaErrors.ConversationNotFound),
            };
        }

        static SogaResult<CreatedResult<MessageResponse>> Fail(SogaError error) =>
            SogaResult<CreatedResult<MessageResponse>>.Fail(error);
    }

    public async Task<SogaResult<MessagePageResponse>> GetHistoryAsync(
        SogaCaller caller,
        Guid conversationId,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.message.history");
        return SogaDiagnostics.Track("message.history", await ExecuteAsync());

        async Task<SogaResult<MessagePageResponse>> ExecuteAsync()
        {
            var paging = options.Value.Messages;
            var limit = SogaPaging.ResolveLimit(requestedLimit, paging.DefaultPageSize, paging.MaximumPageSize);
            if (limit is null)
            {
                return SogaResult<MessagePageResponse>.Fail(SogaErrors.InvalidRequest("limit must be at least 1."));
            }

            long? before = null;
            var scope = conversationId.ToString("N");
            if (cursor is not null)
            {
                if (!cursors.TryDecode(cursor, SogaCursorKind.History, caller.TenantId, scope, out var position))
                {
                    return SogaResult<MessagePageResponse>.Fail(SogaErrors.CursorInvalid);
                }

                before = position;
            }

            // Fetch one extra row to learn whether older history exists without a second query.
            var messages = await store.GetMessagesAsync(
                caller.TenantId,
                caller.UserId,
                conversationId,
                before,
                limit.Value + 1,
                cancellationToken);

            if (messages is null)
            {
                return SogaResult<MessagePageResponse>.Fail(SogaErrors.ConversationNotFound);
            }

            var hasMore = messages.Count > limit.Value;
            var items = messages.Take(limit.Value).Select(m => m.ToResponse()).ToList();
            var nextCursor = hasMore
                ? cursors.Encode(SogaCursorKind.History, caller.TenantId, scope, items[^1].Sequence)
                : null;

            return SogaResult<MessagePageResponse>.Ok(new MessagePageResponse(items, nextCursor));
        }
    }

    internal static bool IsValidClientMessageId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > SogaOptions.MaximumClientMessageIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
