using Microsoft.Extensions.Logging;

namespace Soga.Diagnostics;

internal static partial class SogaLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled Soga endpoint failure.")]
    public static partial void EndpointFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Authorization policy {PolicyType} failed with {FailureType}; the operation was denied.")]
    public static partial void PolicyFailure(ILogger logger, string policyType, string failureType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Soga audit: {Action} tenant={TenantId} actor={ActorUserId} conversation={ConversationId} target={TargetUserId} detail={Detail}")]
    public static partial void Audit(
        ILogger logger,
        string action,
        string tenantId,
        string actorUserId,
        Guid? conversationId,
        string? targetUserId,
        string? detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Soga audit sink failed for {Action} with {FailureType}.")]
    public static partial void AuditFailure(ILogger logger, string action, string failureType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Soga event handler {HandlerType} failed with {FailureType}.")]
    public static partial void EventSubscriberFailure(ILogger logger, string handlerType, string failureType);
}
