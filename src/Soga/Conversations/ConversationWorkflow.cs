using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;

namespace Soga.Conversations;

/// <summary>Owns authorization, validation, and result mapping for conversation operations.</summary>
internal sealed class ConversationWorkflow(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
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
                return Fail($"otherUserId is required and cannot exceed {SogaOptions.MaximumUserIdLength} characters.");
            }

            if (string.Equals(otherUserId, caller.UserId, StringComparison.Ordinal))
            {
                return Fail("A direct conversation requires two different users.");
            }

            var externalReference = request!.ExternalReference?.Trim();
            if (string.IsNullOrEmpty(externalReference))
            {
                externalReference = null;
            }
            else if (externalReference.Length > SogaOptions.MaximumExternalReferenceLength)
            {
                return Fail($"externalReference cannot exceed {SogaOptions.MaximumExternalReferenceLength} characters.");
            }

            var outcome = await store.GetOrCreateDirectConversationAsync(
                new CreateDirectConversationCommand(
                    caller.TenantId,
                    caller.UserId,
                    otherUserId,
                    externalReference,
                    ids.NewId(),
                    ids.NewId(),
                    clock.GetUtcNow()),
                cancellationToken);

            if (outcome.Created)
            {
                signal.Notify();
            }

            return SogaResult<CreatedResult<ConversationResponse>>.Ok(
                new CreatedResult<ConversationResponse>(outcome.Conversation.ToResponse(), outcome.Created));
        }

        static SogaResult<CreatedResult<ConversationResponse>> Fail(string detail) =>
            SogaResult<CreatedResult<ConversationResponse>>.Fail(SogaErrors.InvalidRequest(detail));
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

    public async Task<SogaResult<IReadOnlyList<ConversationResponse>>> ListAsync(
        SogaCaller caller,
        int? requestedLimit,
        CancellationToken cancellationToken)
    {
        using var activity = SogaDiagnostics.StartActivity("soga.conversation.list");
        var paging = options.Value.Conversations;
        var limit = SogaPaging.ResolveLimit(requestedLimit, paging.DefaultPageSize, paging.MaximumPageSize);
        if (limit is null)
        {
            return SogaDiagnostics.Track(
                "conversation.list",
                SogaResult<IReadOnlyList<ConversationResponse>>.Fail(SogaErrors.InvalidRequest("limit must be at least 1.")));
        }

        var conversations = await store.ListConversationsAsync(
            caller.TenantId,
            caller.UserId,
            limit.Value,
            cancellationToken);

        return SogaDiagnostics.Track(
            "conversation.list",
            SogaResult<IReadOnlyList<ConversationResponse>>.Ok(conversations.Select(c => c.ToResponse()).ToList()));
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
