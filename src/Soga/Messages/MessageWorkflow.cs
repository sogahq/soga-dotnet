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

/// <summary>Owns authorization, validation, idempotency mapping, edit, delete, and history for messages.</summary>
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
    private readonly ISogaMessageValidator[] _validators = [.. validators];

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

    private SogaError? ValidateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SogaErrors.InvalidRequest("text is required.");
        }

        return text.Length > options.Value.Messages.MaximumTextLength ? SogaErrors.MessageTooLong : null;
    }

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

/// <summary>Posts system messages for trusted host code. Participants have no way to reach this.</summary>
internal sealed class SogaSystemMessages(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    IOptions<SogaOptions> options) : ISogaSystemMessages
{
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
