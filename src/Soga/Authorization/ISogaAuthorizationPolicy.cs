namespace Soga.Authorization;

/// <summary>The protected operations a host policy can evaluate.</summary>
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

/// <summary>What a host policy is asked to decide.</summary>
/// <param name="Action">The operation.</param>
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
/// A host rule that can further restrict what Soga allows, for example "only the buyer and seller of an order may talk
/// about it". Soga always applies its own membership and role checks first; a policy runs afterwards and can only
/// <b>deny</b>. It can never grant access Soga would refuse, and it can never cross the tenant boundary.
/// </summary>
/// <remarks>
/// The same policies run for HTTP and SignalR. A policy that throws denies the operation. Register any number with
/// <c>services.AddSingleton&lt;ISogaAuthorizationPolicy, MyPolicy&gt;()</c>; all must allow.
/// </remarks>
public interface ISogaAuthorizationPolicy
{
    /// <summary>Returns <see langword="true"/> to allow the operation to continue, or <see langword="false"/> to deny it.</summary>
    ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken);
}
