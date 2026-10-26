using Soga.Contracts;
using Soga.Groups;

namespace Soga.Messages;

/// <summary>
/// Specifies the result of checking whether a message can be edited.
/// </summary>
internal enum EditCheck
{
    /// <summary>The edit is allowed.</summary>
    Allowed,

    /// <summary>The actor is not the sender of the message.</summary>
    Forbidden,

    /// <summary>The message is deleted or is not a text message.</summary>
    NotEditable,

    /// <summary>The message is older than the configured edit window.</summary>
    WindowExpired,
}

/// <summary>
/// Specifies the result of checking whether a message can be deleted.
/// </summary>
internal enum DeleteCheck
{
    /// <summary>The deletion is allowed.</summary>
    Allowed,

    /// <summary>The message is already deleted. Deleting again succeeds without changing anything.</summary>
    AlreadyDeleted,

    /// <summary>The actor is neither the sender nor a permitted group administrator.</summary>
    Forbidden,

    /// <summary>The message is a system message, which participants can never delete.</summary>
    NotDeletable,
}

/// <summary>
/// Provides the edit and delete rules for messages.
/// </summary>
/// <remarks>
/// The rules are pure functions with no storage access, so the in-memory and Entity Framework Core stores apply exactly
/// the same decisions inside their own transactions.
/// </remarks>
internal static class MessageRules
{
    /// <summary>
    /// Determines whether an actor can edit a message.
    /// </summary>
    /// <param name="messageKind">The kind of the message, from <see cref="SogaMessageKinds"/>.</param>
    /// <param name="senderId">The ID of the user who sent the message.</param>
    /// <param name="createdAt">The time the message was sent.</param>
    /// <param name="deleted"><see langword="true"/> if the message has been deleted.</param>
    /// <param name="actorId">The ID of the user attempting the edit.</param>
    /// <param name="now">The current time.</param>
    /// <param name="editWindow">How long after sending a message can be edited, or <see langword="null"/> for no limit.</param>
    /// <returns>
    /// <see cref="EditCheck.Allowed"/> only when the actor is the original sender, the message is an undeleted text
    /// message, and the edit window has not passed; otherwise, the reason it is refused.
    /// </returns>
    public static EditCheck CanEdit(
        string messageKind,
        string senderId,
        DateTimeOffset createdAt,
        bool deleted,
        string actorId,
        DateTimeOffset now,
        TimeSpan? editWindow)
    {
        if (!string.Equals(senderId, actorId, StringComparison.Ordinal))
        {
            return EditCheck.Forbidden;
        }

        if (deleted || !string.Equals(messageKind, SogaMessageKinds.Text, StringComparison.Ordinal))
        {
            return EditCheck.NotEditable;
        }

        return editWindow is { } window && now - createdAt > window ? EditCheck.WindowExpired : EditCheck.Allowed;
    }

    /// <summary>
    /// Determines whether an actor can delete a message.
    /// </summary>
    /// <param name="messageKind">The kind of the message, from <see cref="SogaMessageKinds"/>.</param>
    /// <param name="senderId">The ID of the user who sent the message.</param>
    /// <param name="deleted"><see langword="true"/> if the message has already been deleted.</param>
    /// <param name="actorId">The ID of the user attempting the deletion.</param>
    /// <param name="actorRole">The actor's role in the conversation.</param>
    /// <param name="conversationIsGroup"><see langword="true"/> if the conversation is a group.</param>
    /// <param name="allowOwnDeletion"><see langword="true"/> if the host lets senders delete their own messages.</param>
    /// <param name="allowAdminDeletion">
    /// <see langword="true"/> if the host lets group owners and administrators delete other participants' messages.
    /// </param>
    /// <returns>
    /// <see cref="DeleteCheck.Allowed"/> or <see cref="DeleteCheck.AlreadyDeleted"/> when the actor may delete the
    /// message; otherwise, the reason it is refused.
    /// </returns>
    /// <remarks>
    /// Permission is checked before the deleted flag, so repeating a deletion succeeds only for someone who was allowed
    /// to delete the message in the first place.
    /// </remarks>
    public static DeleteCheck CanDelete(
        string messageKind,
        string senderId,
        bool deleted,
        string actorId,
        SogaRole actorRole,
        bool conversationIsGroup,
        bool allowOwnDeletion,
        bool allowAdminDeletion)
    {
        if (!string.Equals(messageKind, SogaMessageKinds.Text, StringComparison.Ordinal))
        {
            return DeleteCheck.NotDeletable;
        }

        var isAuthor = allowOwnDeletion && string.Equals(senderId, actorId, StringComparison.Ordinal);
        var isAdmin = conversationIsGroup && allowAdminDeletion && actorRole >= SogaRole.Admin;
        if (!isAuthor && !isAdmin)
        {
            return DeleteCheck.Forbidden;
        }

        return deleted ? DeleteCheck.AlreadyDeleted : DeleteCheck.Allowed;
    }
}
