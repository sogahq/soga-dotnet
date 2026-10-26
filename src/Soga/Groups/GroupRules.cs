using Soga.Contracts;

namespace Soga.Groups;

/// <summary>
/// Specifies a participant's role in a conversation. Values are ordered by privilege, so roles can be compared.
/// </summary>
internal enum SogaRole
{
    /// <summary>An ordinary participant. Every participant of a direct conversation is a member.</summary>
    Member = 0,

    /// <summary>A participant who can manage members and the group's details.</summary>
    Admin = 1,

    /// <summary>A participant with full control. A group always keeps at least one owner.</summary>
    Owner = 2,
}

/// <summary>
/// Converts <see cref="SogaRole"/> values to and from their wire names in <see cref="SogaRoleNames"/>.
/// </summary>
internal static class SogaRoles
{
    /// <summary>
    /// Gets the wire name of a role.
    /// </summary>
    /// <param name="role">The role to convert.</param>
    /// <returns><c>owner</c>, <c>admin</c>, or <c>member</c>.</returns>
    public static string ToWire(SogaRole role) => role switch
    {
        SogaRole.Owner => SogaRoleNames.Owner,
        SogaRole.Admin => SogaRoleNames.Admin,
        _ => SogaRoleNames.Member,
    };

    /// <summary>
    /// Parses a wire role name, ignoring case and surrounding whitespace.
    /// </summary>
    /// <param name="value">The role name, or <see langword="null"/> or empty for the default member role.</param>
    /// <param name="role">When this method returns <see langword="true"/>, contains the parsed role.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="value"/> is empty or a known role; otherwise, <see langword="false"/>,
    /// so a typo can never grant a role.
    /// </returns>
    public static bool TryParse(string? value, out SogaRole role)
    {
        role = SogaRole.Member;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or SogaRoleNames.Member:
                return true;
            case SogaRoleNames.Admin:
                role = SogaRole.Admin;
                return true;
            case SogaRoleNames.Owner:
                role = SogaRole.Owner;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Specifies why a group change was refused.
/// </summary>
internal enum GroupError
{
    /// <summary>The change was accepted.</summary>
    None,

    /// <summary>The conversation does not exist or the actor is not an active participant.</summary>
    NotFound,

    /// <summary>The conversation is a direct conversation, not a group.</summary>
    NotGroup,

    /// <summary>The actor's role does not allow the change.</summary>
    Forbidden,

    /// <summary>The change would leave the group without an owner.</summary>
    LastOwner,

    /// <summary>The group already has the maximum number of participants.</summary>
    TooLarge,

    /// <summary>The target user is not a participant.</summary>
    ParticipantNotFound,
}

/// <summary>
/// Represents a requested change to a group. Each derived record describes one kind of change.
/// </summary>
internal abstract record GroupMutation;

/// <summary>
/// Represents adding a participant to a group.
/// </summary>
/// <param name="UserId">The ID of the user to add.</param>
/// <param name="Role">The role the new participant receives.</param>
internal sealed record AddParticipantMutation(string UserId, SogaRole Role) : GroupMutation;

/// <summary>
/// Represents removing a participant from a group, or leaving it when the user is the actor.
/// </summary>
/// <param name="UserId">The ID of the participant to remove.</param>
internal sealed record RemoveParticipantMutation(string UserId) : GroupMutation;

/// <summary>
/// Represents changing a participant's role.
/// </summary>
/// <param name="UserId">The ID of the participant.</param>
/// <param name="Role">The new role.</param>
internal sealed record ChangeRoleMutation(string UserId, SogaRole Role) : GroupMutation;

/// <summary>
/// Represents changing a group's title, metadata, or both.
/// </summary>
/// <param name="SetTitle"><see langword="true"/> if the title should change.</param>
/// <param name="Title">The new title, used when <paramref name="SetTitle"/> is <see langword="true"/>.</param>
/// <param name="SetMetadata"><see langword="true"/> if the metadata should change.</param>
/// <param name="MetadataJson">The new canonical metadata JSON, used when <paramref name="SetMetadata"/> is <see langword="true"/>.</param>
internal sealed record UpdateConversationMutation(bool SetTitle, string? Title, bool SetMetadata, string? MetadataJson) : GroupMutation;

/// <summary>
/// Represents archiving or restoring a group.
/// </summary>
/// <param name="Archived"><see langword="true"/> to archive the group; <see langword="false"/> to restore it.</param>
internal sealed record ArchiveMutation(bool Archived) : GroupMutation;

/// <summary>
/// Represents one participant as loaded by a store for <see cref="GroupRules.Decide"/>.
/// </summary>
/// <param name="UserId">The participant's user ID.</param>
/// <param name="Role">The participant's role.</param>
/// <param name="Active"><see langword="false"/> if the participant left or was removed.</param>
internal sealed record ParticipantSnapshot(string UserId, SogaRole Role, bool Active);

/// <summary>
/// Represents the state of a conversation as loaded by a store for <see cref="GroupRules.Decide"/>.
/// </summary>
/// <param name="IsGroup"><see langword="true"/> if the conversation is a group.</param>
/// <param name="Archived"><see langword="true"/> if the group is archived.</param>
/// <param name="Title">The current group title.</param>
/// <param name="MetadataJson">The current canonical metadata JSON.</param>
/// <param name="Participants">Every current and former participant.</param>
internal sealed record GroupSnapshot(
    bool IsGroup,
    bool Archived,
    string? Title,
    string? MetadataJson,
    IReadOnlyList<ParticipantSnapshot> Participants);

/// <summary>
/// Represents an event that an accepted group change must publish.
/// </summary>
/// <param name="Type">The event type, from <see cref="SogaEventTypes"/>.</param>
/// <param name="Data">The event data contract.</param>
/// <param name="TargetUserId">
/// A user who must receive the event even if the change removes their access, so a removed participant is still told
/// they were removed.
/// </param>
internal sealed record PlannedEvent(string Type, object Data, string? TargetUserId = null);

/// <summary>
/// Represents the decision <see cref="GroupRules"/> makes about a group change.
/// </summary>
/// <param name="Error">Why the change was refused, or <see cref="GroupError.None"/> if it was accepted.</param>
/// <param name="Changed"><see langword="true"/> if the store must apply the change; <see langword="false"/> for a refusal or a no-op.</param>
/// <param name="Events">The events to publish when the change is applied.</param>
internal sealed record GroupDecision(GroupError Error, bool Changed, IReadOnlyList<PlannedEvent> Events)
{
    /// <summary>
    /// Creates a decision that refuses the change.
    /// </summary>
    /// <param name="error">Why the change is refused.</param>
    /// <returns>A refusal with no events.</returns>
    public static GroupDecision Fail(GroupError error) => new(error, false, []);

    /// <summary>
    /// Creates a decision that accepts the request but has nothing to change, such as adding an existing participant.
    /// </summary>
    /// <returns>A successful decision with no change and no events.</returns>
    public static GroupDecision Unchanged() => new(GroupError.None, false, []);

    /// <summary>
    /// Creates a decision that applies the change.
    /// </summary>
    /// <param name="events">The events to publish.</param>
    /// <returns>A successful decision that changes the group.</returns>
    public static GroupDecision Apply(params PlannedEvent[] events) => new(GroupError.None, true, events);
}

/// <summary>
/// Provides the authoritative membership and role rules for groups (ADR 0012).
/// </summary>
/// <remarks>
/// <para>
/// The rules are pure. Each store loads a <see cref="GroupSnapshot"/> inside a transaction that holds the conversation
/// row lock, calls <see cref="Decide"/>, and applies the result. Concurrent changes are therefore serialized, and the
/// in-memory and Entity Framework Core stores always reach the same outcome.
/// </para>
/// <para>The role matrix is:</para>
/// <list type="bullet">
/// <item><description>Owners can do everything, including granting admin or owner and changing roles.</description></item>
/// <item><description>Admins can add and remove members, and update or archive the group.</description></item>
/// <item><description>Members can only leave.</description></item>
/// <item><description>Any participant can leave, but the last owner cannot leave or be demoted.</description></item>
/// </list>
/// </remarks>
internal static class GroupRules
{
    /// <summary>
    /// Decides whether a group change is allowed and which events it produces.
    /// </summary>
    /// <param name="group">The current state of the conversation.</param>
    /// <param name="actorId">The ID of the user making the change.</param>
    /// <param name="mutation">The requested change.</param>
    /// <param name="maximumParticipants">The largest number of active participants a group may have.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mutation"/> is an unknown mutation type.</exception>
    public static GroupDecision Decide(GroupSnapshot group, string actorId, GroupMutation mutation, int maximumParticipants)
    {
        var actor = group.Participants.FirstOrDefault(p => p.Active && p.UserId == actorId);
        if (actor is null)
        {
            // A non-participant learns nothing about the conversation.
            return GroupDecision.Fail(GroupError.NotFound);
        }

        if (!group.IsGroup)
        {
            return GroupDecision.Fail(GroupError.NotGroup);
        }

        return mutation switch
        {
            AddParticipantMutation add => Add(group, actor, add, maximumParticipants),
            RemoveParticipantMutation remove => Remove(group, actor, remove),
            ChangeRoleMutation change => ChangeRole(group, actor, change),
            UpdateConversationMutation update => Update(group, actor, update),
            ArchiveMutation archive => Archive(group, actor, archive),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    /// <summary>
    /// Decides an add: admins can add members, only owners can add admins or owners, and the size limit applies.
    /// </summary>
    /// <param name="group">The current state of the group.</param>
    /// <param name="actor">The participant making the change.</param>
    /// <param name="add">The requested addition.</param>
    /// <param name="maximum">The largest number of active participants allowed.</param>
    /// <returns>The decision. Adding a current participant is a no-op that never changes their role.</returns>
    private static GroupDecision Add(GroupSnapshot group, ParticipantSnapshot actor, AddParticipantMutation add, int maximum)
    {
        if (actor.Role < SogaRole.Admin)
        {
            return GroupDecision.Fail(GroupError.Forbidden);
        }

        // Granting privilege needs privilege: an admin can only add members.
        if (add.Role > SogaRole.Member && actor.Role != SogaRole.Owner)
        {
            return GroupDecision.Fail(GroupError.Forbidden);
        }

        var existing = group.Participants.FirstOrDefault(p => p.UserId == add.UserId);
        if (existing is { Active: true })
        {
            // Idempotent: adding a current participant changes nothing and never alters their role.
            return GroupDecision.Unchanged();
        }

        if (ActiveCount(group) >= maximum)
        {
            return GroupDecision.Fail(GroupError.TooLarge);
        }

        return GroupDecision.Apply(new PlannedEvent(
            SogaEventTypes.ParticipantChanged,
            new ParticipantChangedData(add.UserId, "added", SogaRoles.ToWire(add.Role), actor.UserId)));
    }

    /// <summary>
    /// Decides a removal or a leave: anyone can leave, owners can remove anyone, admins can remove members, and the
    /// last owner can never be removed.
    /// </summary>
    /// <param name="group">The current state of the group.</param>
    /// <param name="actor">The participant making the change.</param>
    /// <param name="remove">The requested removal.</param>
    /// <returns>
    /// The decision. Its event targets the removed user, so they are told about the removal even after losing access.
    /// </returns>
    private static GroupDecision Remove(GroupSnapshot group, ParticipantSnapshot actor, RemoveParticipantMutation remove)
    {
        var target = group.Participants.FirstOrDefault(p => p.UserId == remove.UserId);
        if (target is null)
        {
            return GroupDecision.Fail(GroupError.ParticipantNotFound);
        }

        var isSelf = string.Equals(remove.UserId, actor.UserId, StringComparison.Ordinal);
        if (!isSelf)
        {
            var allowed = actor.Role == SogaRole.Owner || (actor.Role == SogaRole.Admin && target.Role == SogaRole.Member);
            if (!allowed)
            {
                return GroupDecision.Fail(GroupError.Forbidden);
            }
        }

        if (!target.Active)
        {
            // Idempotent: removing someone who already left changes nothing.
            return GroupDecision.Unchanged();
        }

        if (target.Role == SogaRole.Owner && OwnerCount(group) == 1)
        {
            return GroupDecision.Fail(GroupError.LastOwner);
        }

        return GroupDecision.Apply(new PlannedEvent(
            SogaEventTypes.ParticipantChanged,
            new ParticipantChangedData(remove.UserId, isSelf ? "left" : "removed", null, actor.UserId),
            TargetUserId: remove.UserId));
    }

    /// <summary>
    /// Decides a role change: only owners can change roles, and the last owner cannot be demoted.
    /// </summary>
    /// <param name="group">The current state of the group.</param>
    /// <param name="actor">The participant making the change.</param>
    /// <param name="change">The requested role change.</param>
    /// <returns>The decision. Setting the role a participant already has is a no-op.</returns>
    private static GroupDecision ChangeRole(GroupSnapshot group, ParticipantSnapshot actor, ChangeRoleMutation change)
    {
        if (actor.Role != SogaRole.Owner)
        {
            return GroupDecision.Fail(GroupError.Forbidden);
        }

        var target = group.Participants.FirstOrDefault(p => p.Active && p.UserId == change.UserId);
        if (target is null)
        {
            return GroupDecision.Fail(GroupError.ParticipantNotFound);
        }

        if (target.Role == change.Role)
        {
            return GroupDecision.Unchanged();
        }

        if (target.Role == SogaRole.Owner && OwnerCount(group) == 1)
        {
            return GroupDecision.Fail(GroupError.LastOwner);
        }

        return GroupDecision.Apply(new PlannedEvent(
            SogaEventTypes.ParticipantChanged,
            new ParticipantChangedData(change.UserId, "role_changed", SogaRoles.ToWire(change.Role), actor.UserId)));
    }

    /// <summary>
    /// Decides a title or metadata change, which requires the owner or admin role.
    /// </summary>
    /// <param name="group">The current state of the group.</param>
    /// <param name="actor">The participant making the change.</param>
    /// <param name="update">The requested update.</param>
    /// <returns>
    /// The decision. Setting the same values is a no-op. The event only says whether metadata changed; it never carries
    /// the metadata itself.
    /// </returns>
    private static GroupDecision Update(GroupSnapshot group, ParticipantSnapshot actor, UpdateConversationMutation update)
    {
        if (actor.Role < SogaRole.Admin)
        {
            return GroupDecision.Fail(GroupError.Forbidden);
        }

        var titleChanged = update.SetTitle && !string.Equals(update.Title, group.Title, StringComparison.Ordinal);
        var metadataChanged = update.SetMetadata && !string.Equals(update.MetadataJson, group.MetadataJson, StringComparison.Ordinal);
        if (!titleChanged && !metadataChanged)
        {
            return GroupDecision.Unchanged();
        }

        var title = update.SetTitle ? update.Title : group.Title;
        return GroupDecision.Apply(new PlannedEvent(
            SogaEventTypes.ConversationUpdated,
            new ConversationUpdatedData(title, actor.UserId, metadataChanged)));
    }

    /// <summary>
    /// Decides archiving or restoring a group, which requires the owner or admin role.
    /// </summary>
    /// <param name="group">The current state of the group.</param>
    /// <param name="actor">The participant making the change.</param>
    /// <param name="archive">The requested archive state.</param>
    /// <returns>The decision. Requesting the current state is a no-op.</returns>
    private static GroupDecision Archive(GroupSnapshot group, ParticipantSnapshot actor, ArchiveMutation archive)
    {
        if (actor.Role < SogaRole.Admin)
        {
            return GroupDecision.Fail(GroupError.Forbidden);
        }

        if (group.Archived == archive.Archived)
        {
            return GroupDecision.Unchanged();
        }

        return GroupDecision.Apply(new PlannedEvent(
            SogaEventTypes.ConversationArchived,
            new ConversationArchivedData(archive.Archived, actor.UserId)));
    }

    /// <summary>
    /// Counts the group's active participants.
    /// </summary>
    /// <param name="group">The group to count.</param>
    /// <returns>The number of participants who have not left or been removed.</returns>
    private static int ActiveCount(GroupSnapshot group) => group.Participants.Count(p => p.Active);

    /// <summary>
    /// Counts the group's active owners.
    /// </summary>
    /// <param name="group">The group to count.</param>
    /// <returns>The number of active participants with the <see cref="SogaRole.Owner"/> role.</returns>
    private static int OwnerCount(GroupSnapshot group) => group.Participants.Count(p => p.Active && p.Role == SogaRole.Owner);
}
