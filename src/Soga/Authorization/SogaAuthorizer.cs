using Microsoft.Extensions.Logging;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Groups;
using Soga.Identity;
using Soga.Persistence;

namespace Soga.Authorization;

/// <summary>
/// Applies host policies after Soga's own checks. For operations on an existing conversation it confirms membership
/// first, so a non-participant always sees "not found" and can never learn from a policy denial that a conversation exists.
/// </summary>
internal sealed class SogaAuthorizer(
    ISogaStore store,
    IEnumerable<ISogaAuthorizationPolicy> policies,
    ILogger<SogaAuthorizer> logger)
{
    private readonly ISogaAuthorizationPolicy[] _policies = [.. policies];

    /// <summary>Whether any host policy is registered. Callers can skip work that only serves policies.</summary>
    public bool HasPolicies => _policies.Length > 0;

    /// <summary>
    /// Always confirms the caller is an active participant, then applies host policies. Used by realtime operations
    /// (subscribe, typing, presence), which need membership even when no policy is registered.
    /// </summary>
    public async ValueTask<(SogaError? Error, ConversationAccess? Access)> RequireMemberAsync(
        SogaAction action,
        SogaCaller caller,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var access = await store.GetAccessAsync(caller.TenantId, caller.UserId, conversationId, cancellationToken);
        if (access is null)
        {
            return (SogaErrors.ConversationNotFound, null);
        }

        if (_policies.Length == 0)
        {
            return (null, access);
        }

        var context = new SogaAuthorizationContext(
            action,
            caller.TenantId,
            caller.UserId,
            conversationId,
            ExternalReference: access.ExternalReference,
            Role: SogaRoles.ToWire(access.Role));
        return await EvaluateAsync(context, cancellationToken) ? (null, access) : (SogaErrors.AuthorizationDenied, null);
    }

    /// <summary>Checks an operation that has no conversation yet. Returns <see langword="null"/> when allowed.</summary>
    public async ValueTask<SogaError?> CheckAsync(
        SogaAction action,
        SogaCaller caller,
        string? targetUserId,
        string? externalReference,
        CancellationToken cancellationToken)
    {
        if (_policies.Length == 0)
        {
            return null;
        }

        var context = new SogaAuthorizationContext(
            action,
            caller.TenantId,
            caller.UserId,
            TargetUserId: targetUserId,
            ExternalReference: externalReference);
        return await EvaluateAsync(context, cancellationToken) ? null : SogaErrors.AuthorizationDenied;
    }

    /// <summary>Checks an operation on a conversation the caller must be a participant of. Returns <see langword="null"/> when allowed.</summary>
    public async ValueTask<SogaError?> CheckAsync(
        SogaAction action,
        SogaCaller caller,
        Guid conversationId,
        string? targetUserId,
        Guid? messageId,
        CancellationToken cancellationToken)
    {
        if (_policies.Length == 0)
        {
            return null;
        }

        var access = await store.GetAccessAsync(caller.TenantId, caller.UserId, conversationId, cancellationToken);
        if (access is null)
        {
            return SogaErrors.ConversationNotFound;
        }

        var context = new SogaAuthorizationContext(
            action,
            caller.TenantId,
            caller.UserId,
            conversationId,
            targetUserId,
            access.ExternalReference,
            SogaRoles.ToWire(access.Role),
            messageId);
        return await EvaluateAsync(context, cancellationToken) ? null : SogaErrors.AuthorizationDenied;
    }

    private async ValueTask<bool> EvaluateAsync(SogaAuthorizationContext context, CancellationToken cancellationToken)
    {
        foreach (var policy in _policies)
        {
            try
            {
                if (!await policy.IsAllowedAsync(context, cancellationToken))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Fail closed: a broken policy must never silently open access. Only the type is logged.
                SogaLog.PolicyFailure(logger, policy.GetType().Name, exception.GetType().Name);
                return false;
            }
        }

        return true;
    }
}
