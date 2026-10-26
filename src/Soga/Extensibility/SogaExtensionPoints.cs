using Microsoft.Extensions.Logging;
using Soga.Contracts;
using Soga.Diagnostics;

namespace Soga.Extensibility;

/// <summary>
/// Represents the outcome of an <see cref="ISogaMessageValidator"/>.
/// </summary>
/// <param name="IsValid"><see langword="true"/> if the message may continue; otherwise, <see langword="false"/>.</param>
/// <param name="ReasonCode">A short machine-readable code, returned to the client as the error detail. Never message text.</param>
public readonly record struct SogaValidationResult(bool IsValid, string? ReasonCode = null)
{
    /// <summary>Gets a result that accepts the message.</summary>
    public static SogaValidationResult Accepted => new(true);

    /// <summary>
    /// Creates a result that rejects the message.
    /// </summary>
    /// <param name="reasonCode">A machine-readable reason, such as <c>contains_link</c>.</param>
    /// <returns>A rejecting <see cref="SogaValidationResult"/>.</returns>
    public static SogaValidationResult Reject(string reasonCode) => new(false, reasonCode);
}

/// <summary>
/// Describes the message an <see cref="ISogaMessageValidator"/> inspects.
/// </summary>
/// <param name="TenantId">The trusted tenant.</param>
/// <param name="UserId">The sender.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Text">The proposed text. Treat it as untrusted input.</param>
/// <param name="ReplyToMessageId">The message being replied to, if any.</param>
public sealed record SogaMessageValidationContext(
    string TenantId,
    string UserId,
    Guid ConversationId,
    string Text,
    Guid? ReplyToMessageId);

/// <summary>
/// Defines a host check that inspects a message before it is stored, for example to reject links or banned words.
/// </summary>
/// <remarks>
/// A validator can only accept or reject; it cannot rewrite the message. Validators run for both sends and edits,
/// after Soga's own validation and authorization. A rejection returns <c>soga.message.rejected</c>.
/// </remarks>
public interface ISogaMessageValidator
{
    /// <summary>
    /// Validates a proposed message.
    /// </summary>
    /// <param name="context">The message and who is sending it.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the validation outcome.</returns>
    ValueTask<SogaValidationResult> ValidateAsync(SogaMessageValidationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Represents a security-relevant operation that succeeded.
/// </summary>
/// <param name="Action">A stable name such as <c>group.created</c>, <c>participant.added</c>, <c>participant.removed</c>, <c>participant.role_changed</c>, <c>group.updated</c>, <c>group.archived</c>, or <c>message.deleted_by_admin</c>.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="ActorUserId">Who did it.</param>
/// <param name="ConversationId">The conversation, if any.</param>
/// <param name="TargetUserId">Who it was done to, if any.</param>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Detail">A short machine-readable detail such as the new role. Never message text.</param>
public sealed record SogaAuditEntry(
    string Action,
    string TenantId,
    string ActorUserId,
    Guid? ConversationId,
    string? TargetUserId,
    DateTimeOffset At,
    string? Detail);

/// <summary>
/// Defines a destination for audit entries.
/// </summary>
/// <remarks>
/// The default sink writes structured log lines. Replace it to send entries to your own audit store.
/// </remarks>
public interface ISogaAuditSink
{
    /// <summary>
    /// Records an audit entry.
    /// </summary>
    /// <param name="entry">The entry to record.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>A failure is logged and never undoes the operation, which has already been committed.</remarks>
    ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Represents a committed durable event, delivered to <see cref="ISogaEventSubscriber"/> instances after realtime
/// publication.
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="Event">The event, exactly as clients receive it.</param>
public sealed record SogaEventNotification(string TenantId, SogaEvent Event);

/// <summary>
/// Defines a host handler that reacts to committed events, for example to send a push notification for a new message.
/// </summary>
/// <remarks>
/// Delivery is at least once: a failure causes the event to be retried, so handlers must be idempotent. Deduplicate by
/// <c>Event.EventId</c>.
/// </remarks>
public interface ISogaEventSubscriber
{
    /// <summary>
    /// Handles a committed durable event.
    /// </summary>
    /// <param name="notification">The event and its tenant.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task HandleAsync(SogaEventNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Posts system messages, such as "order shipped", from trusted host code.
/// </summary>
/// <remarks>
/// System messages get a sequence, sync entry, and outbox record like any other message, but participants can never
/// create, edit, or delete them.
/// </remarks>
public interface ISogaSystemMessages
{
    /// <summary>
    /// Posts a system message to a conversation.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the conversation.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="text">The message text.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the stored message, or
    /// <see langword="null"/> if the conversation does not exist in the tenant or is archived.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenantId"/> or <paramref name="text"/> is empty, or <paramref name="text"/> exceeds the
    /// configured maximum length.
    /// </exception>
    Task<MessageResponse?> SendAsync(
        string tenantId,
        Guid conversationId,
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a user's presence as known to the <see cref="ISogaPresenceProvider"/>.
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="Online"><see langword="true"/> if the user is online.</param>
/// <param name="LastSeenAt">When the user was last online, if known.</param>
public sealed record SogaPresence(string UserId, bool Online, DateTimeOffset? LastSeenAt);

/// <summary>
/// Defines how Soga tracks which users are online.
/// </summary>
/// <remarks>
/// The built-in provider counts connections inside one server process. A provider for several server instances
/// implements this interface (planned for a later release). Presence is advisory and never grants access.
/// </remarks>
public interface ISogaPresenceProvider
{
    /// <summary>
    /// Records that a user opened a connection.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="connectionId">The SignalR connection ID.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="true"/> if the user has just
    /// come online; <see langword="false"/> if they were already online.
    /// </returns>
    ValueTask<bool> ConnectionOpenedAsync(string tenantId, string userId, string connectionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a user closed a connection.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="connectionId">The SignalR connection ID.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// The user stays online until the grace period passes, so a quick reconnect does not flicker their status.
    /// </remarks>
    ValueTask ConnectionClosedAsync(string tenantId, string userId, string connectionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the presence of several users.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userIds">The users to look up.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains one entry per user.</returns>
    ValueTask<IReadOnlyList<SogaPresence>> GetPresenceAsync(string tenantId, IReadOnlyCollection<string> userIds, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Marks users offline whose grace period has ended, and returns them.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains each user who has just gone
    /// offline, with their tenant.
    /// </returns>
    /// <remarks>Soga calls this method periodically from a background service.</remarks>
    ValueTask<IReadOnlyList<(string TenantId, SogaPresence Presence)>> CollectExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="ISogaAuditSink"/>, which writes one structured log line per entry, without message content.
/// </summary>
/// <param name="logger">The logger to write audit lines to.</param>
internal sealed class LoggingAuditSink(ILogger<LoggingAuditSink> logger) : ISogaAuditSink
{
    /// <inheritdoc />
    public ValueTask WriteAsync(SogaAuditEntry entry, CancellationToken cancellationToken)
    {
        SogaLog.Audit(logger, entry.Action, entry.TenantId, entry.ActorUserId, entry.ConversationId, entry.TargetUserId, entry.Detail);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Writes audit entries to the configured <see cref="ISogaAuditSink"/>, isolating the caller from sink failures.
/// </summary>
/// <param name="sink">The host's audit sink.</param>
/// <param name="clock">The clock that timestamps entries.</param>
/// <param name="logger">The logger that records sink failures.</param>
internal sealed class SogaAuditor(ISogaAuditSink sink, TimeProvider clock, ILogger<SogaAuditor> logger)
{
    /// <summary>
    /// Records an audit entry. A sink failure is logged and never fails the operation, which has already committed.
    /// </summary>
    /// <param name="action">The audit action name, such as <c>participant.added</c>.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="actorUserId">The user who performed the action.</param>
    /// <param name="conversationId">The conversation affected, if any.</param>
    /// <param name="targetUserId">The user affected, if any.</param>
    /// <param name="detail">A short, non-sensitive detail, if any.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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
