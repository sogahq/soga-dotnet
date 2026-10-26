using Soga.Contracts;

namespace Soga.Groups;

/// <summary>Participant roles ordered by privilege.</summary>
internal enum SogaRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

internal static class SogaRoles
{
    public static string ToWire(SogaRole role) => role switch
    {
        SogaRole.Owner => SogaRoleNames.Owner,
        SogaRole.Admin => SogaRoleNames.Admin,
        _ => SogaRoleNames.Member,
    };

    /// <summary>Parses a wire role. Unknown values return <see langword="false"/> so a typo can never grant a role.</summary>
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

internal enum GroupError
{
    None,
    NotFound,
    NotGroup,
    Forbidden,
    LastOwner,
    TooLarge,
    ParticipantNotFound,
}

internal abstract record GroupMutation;

internal sealed record AddParticipantMutation(string UserId, SogaRole Role) : GroupMutation;

internal sealed record RemoveParticipantMutation(string UserId) : GroupMutation;

internal sealed record ChangeRoleMutation(string UserId, SogaRole Role) : GroupMutation;

internal sealed record UpdateConversationMutation(bool SetTitle, string? Title, bool SetMetadata, string? MetadataJson) : GroupMutation;

internal sealed record ArchiveMutation(bool Archived) : GroupMutation;

internal sealed record ParticipantSnapshot(string UserId, SogaRole Role, bool Active);

internal sealed record GroupSnapshot(
    bool IsGroup,
    bool Archived,
    string? Title,
    string? MetadataJson,
    IReadOnlyList<ParticipantSnapshot> Participants);

/// <summary>An event a mutation should publish. <see cref="TargetUserId"/> makes it visible to that user even after they lost access.</summary>
internal sealed record PlannedEvent(string Type, object Data, string? TargetUserId = null);

internal sealed record GroupDecision(GroupError Error, bool Changed, IReadOnlyList<PlannedEvent> Events)
{
    public static GroupDecision Fail(GroupError error) => new(error, false, []);

    public static GroupDecision Unchanged() => new(GroupError.None, false, []);

    public static GroupDecision Apply(params PlannedEvent[] events) => new(GroupError.None, true, events);
}

/// <summary>
/// The authoritative membership and role rules. It is pure: both stores load a snapshot inside a transaction that
/// holds the conversation row lock, call <see cref="Decide"/>, and apply the result, so concurrent changes are
/// serialized and the outcome is deterministic.
/// </summary>
internal static class GroupRules
{
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

    private static int ActiveCount(GroupSnapshot group) => group.Participants.Count(p => p.Active);

    private static int OwnerCount(GroupSnapshot group) => group.Participants.Count(p => p.Active && p.Role == SogaRole.Owner);
}
