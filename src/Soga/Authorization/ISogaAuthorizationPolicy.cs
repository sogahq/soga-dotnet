namespace Soga.Authorization;

/// <summary>
/// Specifies the protected operations a host <see cref="ISogaAuthorizationPolicy"/> can evaluate.
/// </summary>
public enum SogaAction
{
    /// <summary>Opening or retrieving a direct conversation.</summary>
    CreateDirectConversation,

    /// <summary>Creating a group.</summary>
    CreateGroup,

    /// <summary>Sending a message.</summary>
    SendMessage,

    /// <summary>Editing the caller's own message.</summary>
    EditMessage,

    /// <summary>Deleting a message, the caller's own or, for group administrators, another participant's.</summary>
    DeleteMessage,

    /// <summary>Adding a participant to a group.</summary>
    AddParticipant,

    /// <summary>Removing a participant from a group, or leaving it.</summary>
    RemoveParticipant,

    /// <summary>Changing a participant's role.</summary>
    ChangeRole,

    /// <summary>Changing a group's title or metadata.</summary>
    UpdateConversation,

    /// <summary>Archiving or restoring a group.</summary>
    ArchiveConversation,

    /// <summary>Subscribing a realtime connection to a conversation.</summary>
    Subscribe,

    /// <summary>Broadcasting a typing indicator.</summary>
    SetTyping,

    /// <summary>Reading who is online in a conversation.</summary>
    ViewPresence,
}

/// <summary>
/// Describes the operation a host <see cref="ISogaAuthorizationPolicy"/> is asked to decide.
/// </summary>
/// <param name="Action">The operation being attempted.</param>
/// <param name="TenantId">The trusted tenant. A policy can neither change nor cross it.</param>
/// <param name="UserId">The acting user.</param>
/// <param name="ConversationId">The conversation, when the operation has one.</param>
/// <param name="TargetUserId">The user the operation is about, such as the participant being added.</param>
/// <param name="ExternalReference">The host reference, such as an order, when known.</param>
/// <param name="Role">The acting user's role in the conversation (<c>member</c>, <c>admin</c>, <c>owner</c>), when known.</param>
/// <param name="MessageId">The message, when the operation has one.</param>
public sealed record SogaAuthorizationContext(
    SogaAction Action,
    string TenantId,
    string UserId,
    Guid? ConversationId = null,
    string? TargetUserId = null,
    string? ExternalReference = null,
    string? Role = null,
    Guid? MessageId = null);

/// <summary>
/// Defines a host rule that further restricts what Soga allows, for example "only the buyer and seller of an order may
/// talk about it" (ADR 0014).
/// </summary>
/// <remarks>
/// <para>
/// Soga always applies its own membership and role checks first. A policy runs afterwards and can only <b>deny</b>: it
/// can never grant access Soga would refuse, and it can never cross the tenant boundary.
/// </para>
/// <para>
/// The same policies run for HTTP and SignalR. A policy that throws denies the operation. Register any number of
/// policies with <c>services.AddSingleton&lt;ISogaAuthorizationPolicy, MyPolicy&gt;()</c>; every one must allow.
/// </para>
/// </remarks>
public interface ISogaAuthorizationPolicy
{
    /// <summary>
    /// Decides whether an operation may continue.
    /// </summary>
    /// <param name="context">The operation, the acting user, and what is known about the conversation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result is <see langword="true"/> to allow the
    /// operation to continue, or <see langword="false"/> to deny it with <c>soga.authorization.denied</c>.
    /// </returns>
    ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken);
}
