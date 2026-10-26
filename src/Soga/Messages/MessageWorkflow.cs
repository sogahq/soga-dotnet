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
using Soga.Synchronization;

namespace Soga.Messages;

/// <summary>
/// Implements sending, editing, deleting, and reading messages.
/// </summary>
/// <param name="store">The persistence store.</param>
/// <param name="ids">The generator for message and event IDs.</param>
/// <param name="clock">The clock that timestamps messages and events.</param>
/// <param name="cursors">Protects and validates history cursors.</param>
/// <param name="signal">Wakes the outbox dispatcher when an event is stored.</param>
/// <param name="authorizer">Applies host authorization policies.</param>
/// <param name="auditor">Records security-relevant actions, such as an administrator deleting a message.</param>
/// <param name="validators">The host message validators, run in registration order.</param>
/// <param name="options">The Soga options that hold message limits and rules.</param>
/// <remarks>
/// <para>
/// Each operation validates input, applies host policy, then makes one atomic store call. Membership is checked by the
/// store inside the same transaction as the write, so access cannot change between the check and the write.
/// </para>
/// <para>
/// The sender is always the authenticated caller; a request can never choose who a message is from.
/// </para>
/// </remarks>
internal sealed class MessageWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaCursorProtector cursors,
    SogaOutboxSignal signal,
    SogaAuthorizer authorizer,
    SogaAuditor auditor,
    IEnumerable<ISogaMessageValidator> validators,
    IOptions<SogaOptions> options)
{
    /// <summary>
    /// The host message validators, materialized once so they are not re-enumerated on every send.
    /// </summary>
    private readonly ISogaMessageValidator[] _validators = [.. validators];

    /// <summary>
    /// Sends a text message, or returns the stored message when the same request is retried.
    /// </summary>
    /// <param name="caller">The trusted caller, who becomes the sender.</param>
    /// <param name="conversationId">The ID of the conversation to send to.</param>
    /// <param name="request">The message to send, including its client message ID.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the stored message, with
    /// <see cref="CreatedResult{T}.Created"/> set to <see langword="false"/> for an idempotent retry. It fails with
    /// <c>soga.message.idempotency_conflict</c> when the client message ID was used for different text.
    /// </returns>
    /// <remarks>
    /// The client message ID is the idempotency key: retrying a timed-out request with the same ID can never create a
    /// duplicate. The message, its change-log entry, and its outbox record are written in one transaction before any
    /// realtime event is published.
    /// </remarks>
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

            var textError = ValidateText(request!.Text);
            if (textError is not null)
            {
                return Fail(textError);
            }

            var denied = await authorizer.CheckAsync(
                SogaAction.SendMessage,
                caller,
                conversationId,
                targetUserId: null,
                messageId: null,
                cancellationToken);
            if (denied is not null)
            {
                return Fail(denied);
            }

            var rejected = await RunValidatorsAsync(caller, conversationId, request.Text, request.ReplyToMessageId, cancellationToken);
            if (rejected is not null)
            {
                return Fail(rejected);
            }

            var outcome = await store.AppendMessageAsync(
                new AppendMessageCommand(
                    caller.TenantId,
                    caller.UserId,
                    conversationId,
                    clientMessageId!,
                    request.Text,
                    ids.NewId(),
                    ids.NewId(),
                    clock.GetUtcNow(),
                    request.ReplyToMessageId),
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
                AppendStatus.Archived => Fail(SogaErrors.ConversationArchived),
                AppendStatus.ReplyInvalid => Fail(SogaErrors.ReplyInvalid),
                _ => Fail(SogaErrors.ConversationNotFound),
            };
        }

        static SogaResult<CreatedResult<MessageResponse>> Fail(SogaError error) =>
            SogaResult<CreatedResult<MessageResponse>>.Fail(error);
    }

    /// <summary>
    /// Replaces the text of one of the caller's own messages.
    /// </summary>
    /// <param name="caller">The trusted caller, who must be the original sender.</param>
    /// <param name="conversationId">The ID of the conversation that contains the message.</param>
    /// <param name="messageId">The ID of the message to edit.</param>
    /// <param name="request">The new text.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the edited message. Editing to the
    /// same text succeeds without publishing an event.
    /// </returns>
    /// <remarks>
    /// The message keeps its ID, sequence, sender, and creation time. When
    /// <see cref="SogaMessageOptions.RetainEditHistory"/> is enabled, the previous text is stored.
    /// </remarks>
    public async Task<SogaResult<MessageResponse>> EditAsync(
        SogaCaller caller,
        Guid conversationId,
        Guid messageId,
        EditMessageRequest? request,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.message.edit");
        return SogaDiagnostics.Track("message.edit", await ExecuteAsync());

        async Task<SogaResult<MessageResponse>> ExecuteAsync()
        {
            var settings = options.Value.Messages;
            var textError = ValidateText(request?.Text);
            if (textError is not null)
            {
                return SogaResult<MessageResponse>.Fail(textError);
            }

            var denied = await authorizer.CheckAsync(SogaAction.EditMessage, caller, conversationId, null, messageId, cancellationToken);
            if (denied is not null)
            {
                return SogaResult<MessageResponse>.Fail(denied);
            }

            if (!settings.AllowEditing)
            {
                // Editing disabled by the host; still say "not found" first to non-participants.
                var access = await store.GetAccessAsync(caller.TenantId, caller.UserId, conversationId, cancellationToken);
                return SogaResult<MessageResponse>.Fail(access is null ? SogaErrors.ConversationNotFound : SogaErrors.MessageNotEditable);
            }

            var rejected = await RunValidatorsAsync(caller, conversationId, request!.Text, null, cancellationToken);
            if (rejected is not null)
            {
                return SogaResult<MessageResponse>.Fail(rejected);
            }

            var outcome = await store.EditMessageAsync(
                new EditMessageCommand(
                    caller.TenantId,
                    caller.UserId,
                    conversationId,
                    messageId,
                    request.Text,
                    ids.NewId(),
                    ids,
                    clock.GetUtcNow(),
                    settings.EditWindow,
                    settings.RetainEditHistory),
                cancellationToken);

            if (outcome.Status == EditStatus.Updated)
            {
                signal.Notify();
            }

            return outcome.Status switch
            {
                EditStatus.Updated or EditStatus.Unchanged => SogaResult<MessageResponse>.Ok(outcome.Message!.ToResponse()),
                EditStatus.MessageNotFound => SogaResult<MessageResponse>.Fail(SogaErrors.MessageNotFound),
                EditStatus.Forbidden => SogaResult<MessageResponse>.Fail(SogaErrors.AuthorizationDenied),
                EditStatus.NotEditable => SogaResult<MessageResponse>.Fail(SogaErrors.MessageNotEditable),
                EditStatus.WindowExpired => SogaResult<MessageResponse>.Fail(SogaErrors.MessageEditWindowExpired),
                _ => SogaResult<MessageResponse>.Fail(SogaErrors.ConversationNotFound),
            };
        }
    }

    /// <summary>
    /// Deletes a message, leaving an empty tombstone in its place.
    /// </summary>
    /// <param name="caller">The trusted caller: the sender, or a group owner or administrator.</param>
    /// <param name="conversationId">The ID of the conversation that contains the message.</param>
    /// <param name="messageId">The ID of the message to delete.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is successful when the message is deleted or
    /// was already deleted, so repeating the request is safe.
    /// </returns>
    /// <remarks>
    /// The text is erased, but the tombstone keeps its sequence so message ordering stays contiguous. A deletion by an
    /// administrator rather than the sender is recorded in the audit trail.
    /// </remarks>
    public async Task<SogaResult<bool>> DeleteAsync(
        SogaCaller caller,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.message.delete");
        return SogaDiagnostics.Track("message.delete", await ExecuteAsync());

        async Task<SogaResult<bool>> ExecuteAsync()
        {
            var denied = await authorizer.CheckAsync(SogaAction.DeleteMessage, caller, conversationId, null, messageId, cancellationToken);
            if (denied is not null)
            {
                return SogaResult<bool>.Fail(denied);
            }

            var settings = options.Value.Messages;
            var outcome = await store.DeleteMessageAsync(
                new DeleteMessageCommand(
                    caller.TenantId,
                    caller.UserId,
                    conversationId,
                    messageId,
                    settings.AllowDeletion,
                    settings.AllowAdminDeletion,
                    ids.NewId(),
                    clock.GetUtcNow()),
                cancellationToken);

            if (outcome.Status == DeleteStatus.Deleted)
            {
                signal.Notify();
                if (outcome.ByAdministrator)
                {
                    await auditor.RecordAsync(
                        "message.deleted_by_admin",
                        caller.TenantId,
                        caller.UserId,
                        conversationId,
                        outcome.SenderId,
                        $"message={messageId:N}",
                        cancellationToken);
                }
            }

            return outcome.Status switch
            {
                DeleteStatus.Deleted or DeleteStatus.AlreadyDeleted => SogaResult<bool>.Ok(true),
                DeleteStatus.MessageNotFound => SogaResult<bool>.Fail(SogaErrors.MessageNotFound),
                DeleteStatus.Forbidden => SogaResult<bool>.Fail(SogaErrors.AuthorizationDenied),
                DeleteStatus.NotDeletable => SogaResult<bool>.Fail(SogaErrors.MessageNotDeletable),
                _ => SogaResult<bool>.Fail(SogaErrors.ConversationNotFound),
            };
        }
    }

    /// <summary>
    /// Gets one page of a conversation's message history, newest first.
    /// </summary>
    /// <param name="caller">The trusted caller, who must be a participant.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="cursor">The cursor from the previous page, or <see langword="null"/> for the newest messages.</param>
    /// <param name="requestedLimit">The page size, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the messages and a cursor for older
    /// messages, which is <see langword="null"/> on the last page.
    /// </returns>
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

    /// <summary>
    /// Determines whether a client message ID has a valid length and uses only allowed characters.
    /// </summary>
    /// <param name="value">The trimmed client message ID.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="value"/> is 1 to <see cref="SogaOptions.MaximumClientMessageIdLength"/>
    /// ASCII letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, or <c>:</c>; otherwise, <see langword="false"/>.
    /// </returns>
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

    /// <summary>
    /// Validates message text against the presence and length rules.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <returns>
    /// <see langword="null"/> if the text is valid; otherwise, the error for missing text or for text longer than
    /// <see cref="SogaMessageOptions.MaximumTextLength"/>.
    /// </returns>
    private SogaError? ValidateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SogaErrors.InvalidRequest("text is required.");
        }

        return text.Length > options.Value.Messages.MaximumTextLength ? SogaErrors.MessageTooLong : null;
    }

    /// <summary>
    /// Runs every host message validator and stops at the first rejection.
    /// </summary>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="text">The message text to validate.</param>
    /// <param name="replyToMessageId">The ID of the message being replied to, if any.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="null"/> when every
    /// validator accepts the message; otherwise, a <c>soga.message.rejected</c> error carrying the validator's reason
    /// code, trimmed to 100 characters.
    /// </returns>
    private async ValueTask<SogaError?> RunValidatorsAsync(
        SogaCaller caller,
        Guid conversationId,
        string text,
        Guid? replyToMessageId,
        CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
        {
            return null;
        }

        var context = new SogaMessageValidationContext(caller.TenantId, caller.UserId, conversationId, text, replyToMessageId);
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            if (!result.IsValid)
            {
                var reason = string.IsNullOrWhiteSpace(result.ReasonCode) ? "rejected" : result.ReasonCode.Trim();
                return SogaErrors.MessageRejected(reason.Length > 100 ? reason[..100] : reason);
            }
        }

        return null;
    }
}

/// <summary>
/// The default <see cref="ISogaSystemMessages"/>, which lets trusted host code post system messages.
/// </summary>
/// <param name="store">The persistence store.</param>
/// <param name="ids">The generator for message and event IDs.</param>
/// <param name="clock">The clock that timestamps messages.</param>
/// <param name="signal">Wakes the outbox dispatcher when a message is stored.</param>
/// <param name="options">The Soga options that hold the text-length limit.</param>
/// <remarks>
/// System messages are sent as <see cref="SogaSystemSender.UserId"/>. No HTTP endpoint or hub method reaches this
/// service, so participants can never create, edit, or delete a system message.
/// </remarks>
internal sealed class SogaSystemMessages(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    IOptions<SogaOptions> options) : ISogaSystemMessages
{
    /// <inheritdoc />
    public async Task<MessageResponse?> SendAsync(
        string tenantId,
        Guid conversationId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > options.Value.Messages.MaximumTextLength)
        {
            throw new ArgumentException($"The text cannot exceed {options.Value.Messages.MaximumTextLength} characters.", nameof(text));
        }

        var messageId = ids.NewId();
        var outcome = await store.AppendMessageAsync(
            new AppendMessageCommand(
                tenantId.Trim(),
                SogaSystemSender.UserId,
                conversationId,
                $"sys:{messageId:N}",
                text,
                messageId,
                ids.NewId(),
                clock.GetUtcNow(),
                IsSystem: true),
            cancellationToken);

        if (outcome.Status != AppendStatus.Created)
        {
            return null;
        }

        signal.Notify();
        return outcome.Message!.ToResponse();
    }
}
