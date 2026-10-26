using Soga.Contracts;
using Soga.Groups;

namespace Soga.Messages;

internal enum EditCheck
{
    Allowed,
    Forbidden,
    NotEditable,
    WindowExpired,
}

internal enum DeleteCheck
{
    Allowed,
    AlreadyDeleted,
    Forbidden,
    NotDeletable,
}

/// <summary>Pure edit and delete rules shared by both stores.</summary>
internal static class MessageRules
{
    /// <summary>Only the original sender may edit, only text messages, only while not deleted, and only inside the window.</summary>
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
    /// A sender may delete their own text message. In a group, an owner or administrator may delete others' text
    /// messages when the host allows it. System messages are never deletable by participants.
    /// </summary>
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
