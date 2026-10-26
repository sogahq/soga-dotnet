using Microsoft.Extensions.Logging;
using Soga.Contracts;
using Soga.Diagnostics;

namespace Soga.Extensibility;

/// <summary>The outcome of a host message validator.</summary>
/// <param name="IsValid">Whether the message may continue.</param>
/// <param name="ReasonCode">A short machine-readable code, returned to the client as the error detail. Never message text.</param>
public readonly record struct SogaValidationResult(bool IsValid, string? ReasonCode = null)
{
    /// <summary>The message is acceptable.</summary>
    public static SogaValidationResult Accepted => new(true);

    /// <summary>Rejects the message with a machine-readable reason such as <c>contains_link</c>.</summary>
    public static SogaValidationResult Reject(string reasonCode) => new(false, reasonCode);
}

/// <summary>What a message validator inspects.</summary>
/// <param name="TenantId">The trusted tenant.</param>
/// <param name="UserId">The sender.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Text">The proposed text. Untrusted.</param>
/// <param name="ReplyToMessageId">The message being replied to, if any.</param>
public sealed record SogaMessageValidationContext(
    string TenantId,
    string UserId,
    Guid ConversationId,
    string Text,
    Guid? ReplyToMessageId);

/// <summary>
/// Inspects a message before it is stored, for example to reject links or banned words. It can only accept or reject;
/// it cannot rewrite the message. It runs for sends and edits, after Soga's own validation and authorization.
/// </summary>
public interface ISogaMessageValidator
{
    /// <summary>Validates the proposed message.</summary>
    ValueTask<SogaValidationResult> ValidateAsync(SogaMessageValidationContext context, CancellationToken cancellationToken);
}

/// <summary>A security-relevant operation that succeeded.</summary>
/// <param name="Action">A stable name such as <c>group.created</c>, <c>participant.added</c>, <c>participant.removed</c>, <c>participant.role_changed</c>, <c>group.updated</c>, <c>group.archived</c>, or <c>message.deleted_by_admin</c>.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="ActorUserId">Who did it.</param>
/// <param name="ConversationId">The conversation, if any.</param>
/// <param name="TargetUserId">Who it was done to, if any.</param>
/// <param name="At">When (UTC).</param>
/// <param name="Detail">A short machine-readable detail such as the new role. Never message text.</param>
public sealed record SogaAuditEntry(
    string Action,
    string TenantId,
    string ActorUserId,
    Guid? ConversationId,
    string? TargetUserId,
    DateTimeOffset At,
    string? Detail);

/// <summary>Receives audit entries. Replace the default (which writes structured logs) to send them to your audit store.</summary>
public interface ISogaAuditSink
{
    /// <summary>Records an audit entry. A failure is logged and never undoes the completed operation.</summary>
    ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>A committed durable event, delivered after realtime publication.</summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="Event">The event, exactly as clients receive it.</param>
public sealed record SogaEventNotification(string TenantId, SogaEvent Event);

/// <summary>
/// Reacts to committed events, for example to send a push notification for a new message. Delivery is at least once:
/// a failure retries the event, so handlers must be idempotent. Deduplicate by <c>Event.EventId</c>.
/// </summary>
public interface ISogaEventSubscriber
{
    /// <summary>Handles a committed durable event.</summary>
    Task HandleAsync(SogaEventNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Posts system messages from trusted host code, such as "order shipped". System messages get a sequence, sync, and
/// outbox like any other message, but participants cannot create, edit, or delete them.
/// </summary>
public interface ISogaSystemMessages
{
    /// <summary>Posts a system message. Returns <see langword="null"/> if the conversation does not exist in the tenant.</summary>
    Task<MessageResponse?> SendAsync(
        string tenantId,
        Guid conversationId,
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>A user's presence as known to the provider.</summary>
/// <param name="UserId">The user.</param>
/// <param name="Online">Whether the user is online.</param>
/// <param name="LastSeenAt">When they were last online, if known.</param>
public sealed record SogaPresence(string UserId, bool Online, DateTimeOffset? LastSeenAt);

/// <summary>
/// Tracks who is online. The built-in provider counts connections inside one server process. A provider for several
/// instances implements this interface (planned for a later release). Presence is advisory and never grants access.
/// </summary>
public interface ISogaPresenceProvider
{
    /// <summary>Records a new connection. Returns <see langword="true"/> when the user just came online.</summary>
    ValueTask<bool> ConnectionOpenedAsync(string tenantId, string userId, string connectionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records a closed connection. The user stays online until the grace period passes.</summary>
    ValueTask ConnectionClosedAsync(string tenantId, string userId, string connectionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns presence for the given users.</summary>
    ValueTask<IReadOnlyList<SogaPresence>> GetPresenceAsync(string tenantId, IReadOnlyCollection<string> userIds, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns the users whose grace period has just ended, and marks them offline. Called periodically.</summary>
    ValueTask<IReadOnlyList<(string TenantId, SogaPresence Presence)>> CollectExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The default audit sink. Writes one structured log line per entry, without message content.</summary>
internal sealed class LoggingAuditSink(ILogger<LoggingAuditSink> logger) : ISogaAuditSink
{
    public ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken)
    {
        SogaLog.Audit(logger, entry.Action, entry.TenantId, entry.ActorUserId, entry.ConversationId, entry.TargetUserId, entry.Detail);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Wraps the audit sink so a sink failure is logged and never fails the operation that already committed.</summary>
internal sealed class SogaAuditor(ISogaAuditSink sink, TimeProvider clock, ILogger<SogaAuditor> logger)
{
    public async ValueTask RecordAsync(
        string action,
        string tenantId,
        string actorUserId,
        Guid? conversationId,
        string? targetUserId,
        string? detail,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.WriteAsync(
                new SogaAuditEntry(action, tenantId, actorUserId, conversationId, targetUserId, clock.GetUtcNow(), detail),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SogaLog.AuditFailure(logger, action, exception.GetType().Name);
        }
    }
}
