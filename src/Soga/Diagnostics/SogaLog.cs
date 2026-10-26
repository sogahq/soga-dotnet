using Microsoft.Extensions.Logging;

namespace Soga.Diagnostics;

/// <summary>
/// Defines the source-generated log messages that Soga writes.
/// </summary>
/// <remarks>
/// Log messages never contain message text or access tokens. Failures log the exception type name rather than the
/// exception message when the exception comes from host code, because host exceptions may contain sensitive data.
/// </remarks>
internal static partial class SogaLog
{
    /// <summary>
    /// Logs an unexpected exception thrown while handling a Soga HTTP endpoint.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="exception">The unhandled exception.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled Soga endpoint failure.")]
    public static partial void EndpointFailure(ILogger logger, Exception exception);

    /// <summary>
    /// Logs that a host <see cref="Authorization.ISogaAuthorizationPolicy"/> threw, which causes the operation to be
    /// denied.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="policyType">The full type name of the policy that failed.</param>
    /// <param name="failureType">The type name of the exception the policy threw.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Authorization policy {PolicyType} failed with {FailureType}; the operation was denied.")]
    public static partial void PolicyFailure(ILogger logger, string policyType, string failureType);

    /// <summary>
    /// Writes an audit record for a security-relevant action, such as removing a participant.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="action">The audit action name, such as <c>participant.removed</c>.</param>
    /// <param name="tenantId">The tenant the action happened in.</param>
    /// <param name="actorUserId">The user who performed the action.</param>
    /// <param name="conversationId">The conversation affected, if any.</param>
    /// <param name="targetUserId">The user affected, if any.</param>
    /// <param name="detail">A short, non-sensitive detail string, if any.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Soga audit: {Action} tenant={TenantId} actor={ActorUserId} conversation={ConversationId} target={TargetUserId} detail={Detail}")]
    public static partial void Audit(
        ILogger logger,
        string action,
        string tenantId,
        string actorUserId,
        Guid? conversationId,
        string? targetUserId,
        string? detail);

    /// <summary>
    /// Logs that a host <see cref="Extensibility.ISogaAuditSink"/> threw while recording an action.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="action">The audit action that could not be recorded.</param>
    /// <param name="failureType">The type name of the exception the sink threw.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Soga audit sink failed for {Action} with {FailureType}.")]
    public static partial void AuditFailure(ILogger logger, string action, string failureType);

    /// <summary>
    /// Logs that a host <see cref="Extensibility.ISogaEventSubscriber"/> threw while handling an event.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="handlerType">The full type name of the subscriber that failed.</param>
    /// <param name="failureType">The type name of the exception the subscriber threw.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Soga event handler {HandlerType} failed with {FailureType}.")]
    public static partial void EventSubscriberFailure(ILogger logger, string handlerType, string failureType);
}
