using Microsoft.Extensions.Logging;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Groups;
using Soga.Identity;
using Soga.Persistence;

namespace Soga.Authorization;

/// <summary>
/// Applies the host's <see cref="ISogaAuthorizationPolicy"/> instances after Soga's own checks.
/// </summary>
/// <param name="store">The persistence store, used to confirm membership.</param>
/// <param name="policies">The registered host policies, evaluated in registration order.</param>
/// <param name="logger">The logger that records policies that throw.</param>
/// <remarks>
/// <para>
/// For an operation on an existing conversation, membership is confirmed before any policy runs. A non-participant
/// therefore always sees "not found" and can never learn from a policy denial that a conversation exists.
/// </para>
/// <para>
/// Evaluation fails closed: a policy that throws denies the operation.
/// </para>
/// </remarks>
internal sealed class SogaAuthorizer(
    ISogaStore store,
    IEnumerable<ISogaAuthorizationPolicy> policies,
    ILogger<SogaAuthorizer> logger)
{
    /// <summary>
    /// The registered host policies, materialized once so they are not re-enumerated on every check.
    /// </summary>
    private readonly ISogaAuthorizationPolicy[] _policies = [.. policies];

    /// <summary>
    /// Gets a value indicating whether any host policy is registered.
    /// </summary>
    /// <remarks>Callers can skip work that only serves policies when this is <see langword="false"/>.</remarks>
    public bool HasPolicies => _policies.Length > 0;

    /// <summary>
    /// Confirms the caller is an active participant, then applies host policies.
    /// </summary>
    /// <param name="action">The operation being attempted.</param>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. On success the task result contains no error and the caller's
    /// access; otherwise it contains <c>soga.conversation.not_found</c> or <c>soga.authorization.denied</c>.
    /// </returns>
    /// <remarks>
    /// Realtime operations (subscribe, typing, and presence) use this method because they need membership even when no
    /// policy is registered.
    /// </remarks>
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

    /// <summary>
    /// Applies host policies to an operation that does not have a conversation yet, such as creating one.
    /// </summary>
    /// <param name="action">The operation being attempted.</param>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="targetUserId">The other user involved, if any.</param>
    /// <param name="externalReference">The host reference the operation is about, if any.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="null"/> when the operation
    /// is allowed; otherwise, <c>soga.authorization.denied</c>.
    /// </returns>
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

    /// <summary>
    /// Applies host policies to an operation on a conversation the caller must participate in.
    /// </summary>
    /// <param name="action">The operation being attempted.</param>
    /// <param name="caller">The trusted caller.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="targetUserId">The participant the operation is about, if any.</param>
    /// <param name="messageId">The message the operation is about, if any.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="null"/> when the operation
    /// is allowed; otherwise, <c>soga.conversation.not_found</c> or <c>soga.authorization.denied</c>.
    /// </returns>
    /// <remarks>
    /// When no policy is registered this method returns immediately, and membership is checked by the store as part
    /// of the operation itself.
    /// </remarks>
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

    /// <summary>
    /// Runs every host policy and stops at the first denial.
    /// </summary>
    /// <param name="context">The operation to decide.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="true"/> if every policy
    /// allows the operation; <see langword="false"/> if any denies it or throws.
    /// </returns>
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
