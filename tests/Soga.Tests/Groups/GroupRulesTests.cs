using Soga.Contracts;
using Soga.Groups;

namespace Soga.Tests.Groups;

/// <summary>The role and membership rules are pure, so they are tested exhaustively without a store.</summary>
public sealed class GroupRulesTests
{
    private static ParticipantSnapshot Owner(string id = "owner") => new(id, SogaRole.Owner, true);

    private static ParticipantSnapshot Admin(string id = "admin") => new(id, SogaRole.Admin, true);

    private static ParticipantSnapshot Member(string id = "member") => new(id, SogaRole.Member, true);

    private static ParticipantSnapshot Left(string id, SogaRole role = SogaRole.Member) => new(id, role, false);

    private static GroupSnapshot Group(params ParticipantSnapshot[] participants) =>
        new(true, false, "Team", null, participants);

    private static GroupDecision Decide(GroupSnapshot group, string actor, GroupMutation mutation, int max = 100) =>
        GroupRules.Decide(group, actor, mutation, max);

    [Fact]
    public void Decide_WhenActorIsNotAnActiveParticipant_ReturnsNotFound()
    {
        var group = Group(Owner(), Left("gone"));

        Assert.Equal(GroupError.NotFound, Decide(group, "stranger", new ArchiveMutation(true)).Error);
        Assert.Equal(GroupError.NotFound, Decide(group, "gone", new ArchiveMutation(true)).Error);
    }

    [Fact]
    public void Decide_WhenConversationIsDirect_ReturnsNotGroup()
    {
        var direct = new GroupSnapshot(false, false, null, null, [Member("a"), Member("b")]);

        Assert.Equal(GroupError.NotGroup, Decide(direct, "a", new AddParticipantMutation("c", SogaRole.Member)).Error);
    }

    [Theory]
    [InlineData((int)SogaRole.Owner, (int)SogaRole.Member, true)]
    [InlineData((int)SogaRole.Admin, (int)SogaRole.Member, true)]
    [InlineData((int)SogaRole.Member, (int)SogaRole.Member, false)]
    [InlineData((int)SogaRole.Owner, (int)SogaRole.Admin, true)]
    [InlineData((int)SogaRole.Owner, (int)SogaRole.Owner, true)]
    [InlineData((int)SogaRole.Admin, (int)SogaRole.Admin, false)]
    [InlineData((int)SogaRole.Admin, (int)SogaRole.Owner, false)]
    public void Add_RequiresAdminAndOnlyOwnersGrantPrivilege(int actorRoleValue, int grantedValue, bool allowed)
    {
        var actorRole = (SogaRole)actorRoleValue;
        var granted = (SogaRole)grantedValue;
        var group = Group(Owner(), new ParticipantSnapshot("actor", actorRole, true));
        var actor = actorRole == SogaRole.Owner ? "owner" : "actor";

        var decision = Decide(group, actor, new AddParticipantMutation("newbie", granted));

        Assert.Equal(allowed ? GroupError.None : GroupError.Forbidden, decision.Error);
        Assert.Equal(allowed, decision.Changed);
    }

    [Fact]
    public void Add_WhenAlreadyActive_IsIdempotentAndNeverChangesTheirRole()
    {
        var group = Group(Owner(), Member("bob"));

        var decision = Decide(group, "owner", new AddParticipantMutation("bob", SogaRole.Admin));

        Assert.Equal((GroupError.None, false), (decision.Error, decision.Changed));
    }

    [Fact]
    public void Add_WhenGroupIsFull_ReturnsTooLarge()
    {
        var group = Group(Owner(), Member("a"), Member("b"));

        Assert.Equal(GroupError.TooLarge, Decide(group, "owner", new AddParticipantMutation("c", SogaRole.Member), max: 3).Error);
        Assert.Equal(GroupError.None, Decide(group, "owner", new AddParticipantMutation("c", SogaRole.Member), max: 4).Error);
    }

    [Fact]
    public void Add_LeftMembersDoNotCountTowardsTheLimit()
    {
        var group = Group(Owner(), Left("gone1"), Left("gone2"));

        Assert.Equal(GroupError.None, Decide(group, "owner", new AddParticipantMutation("new", SogaRole.Member), max: 2).Error);
    }

    [Fact]
    public void Add_PublishesAParticipantChangedEventForEveryoneToSee()
    {
        var decision = Decide(Group(Owner()), "owner", new AddParticipantMutation("bob", SogaRole.Admin));

        var planned = Assert.Single(decision.Events);
        Assert.Equal(SogaEventTypes.ParticipantChanged, planned.Type);
        Assert.Equal(new ParticipantChangedData("bob", "added", "admin", "owner"), planned.Data);
        Assert.Null(planned.TargetUserId);
    }

    [Theory]
    [InlineData((int)SogaRole.Owner, (int)SogaRole.Member, true)]
    [InlineData((int)SogaRole.Owner, (int)SogaRole.Admin, true)]
    [InlineData((int)SogaRole.Owner, (int)SogaRole.Owner, true)]
    [InlineData((int)SogaRole.Admin, (int)SogaRole.Member, true)]
    [InlineData((int)SogaRole.Admin, (int)SogaRole.Admin, false)]
    [InlineData((int)SogaRole.Admin, (int)SogaRole.Owner, false)]
    [InlineData((int)SogaRole.Member, (int)SogaRole.Member, false)]
    public void Remove_OwnersRemoveAnyoneAdminsOnlyMembersMembersNoOne(int actorRoleValue, int targetRoleValue, bool allowed)
    {
        var actorRole = (SogaRole)actorRoleValue;
        var targetRole = (SogaRole)targetRoleValue;
        // Two owners exist so removing an owner never trips the last-owner rule here.
        var group = Group(Owner("o1"), Owner("o2"), new ParticipantSnapshot("actor", actorRole, true), new ParticipantSnapshot("target", targetRole, true));
        var actor = actorRole == SogaRole.Owner ? "o1" : "actor";

        var decision = Decide(group, actor, new RemoveParticipantMutation("target"));

        Assert.Equal(allowed ? GroupError.None : GroupError.Forbidden, decision.Error);
        Assert.Equal(allowed, decision.Changed);
    }

    [Fact]
    public void Remove_TheRemovedUserGetsATargetedEventSoTheyLearnTheyLostAccess()
    {
        var decision = Decide(Group(Owner(), Member("bob")), "owner", new RemoveParticipantMutation("bob"));

        var planned = Assert.Single(decision.Events);
        Assert.Equal(new ParticipantChangedData("bob", "removed", null, "owner"), planned.Data);
        Assert.Equal("bob", planned.TargetUserId);
    }

    [Fact]
    public void Remove_Self_IsLeaving()
    {
        var decision = Decide(Group(Owner(), Member("bob")), "bob", new RemoveParticipantMutation("bob"));

        Assert.Equal(new ParticipantChangedData("bob", "left", null, "bob"), Assert.Single(decision.Events).Data);
    }

    [Fact]
    public void Remove_WhenTheLastOwnerLeaves_IsRefusedUntilOwnershipIsTransferred()
    {
        var group = Group(Owner(), Admin(), Member());

        Assert.Equal(GroupError.LastOwner, Decide(group, "owner", new RemoveParticipantMutation("owner")).Error);
    }

    [Fact]
    public void Remove_WhenOneOfSeveralOwnersLeaves_IsAllowed()
    {
        var group = Group(Owner("o1"), Owner("o2"));

        Assert.Equal(GroupError.None, Decide(group, "o1", new RemoveParticipantMutation("o1")).Error);
    }

    [Fact]
    public void Remove_OfSomeoneWhoAlreadyLeft_IsIdempotent()
    {
        var group = Group(Owner(), Left("gone"));

        var decision = Decide(group, "owner", new RemoveParticipantMutation("gone"));

        Assert.Equal((GroupError.None, false), (decision.Error, decision.Changed));
    }

    [Fact]
    public void Remove_OfSomeoneWhoWasNeverAMember_ReturnsParticipantNotFound()
    {
        Assert.Equal(GroupError.ParticipantNotFound, Decide(Group(Owner()), "owner", new RemoveParticipantMutation("nobody")).Error);
    }

    [Theory]
    [InlineData((int)SogaRole.Admin)]
    [InlineData((int)SogaRole.Member)]
    public void ChangeRole_OnlyOwnersMay(int actorRoleValue)
    {
        var actorRole = (SogaRole)actorRoleValue;
        var group = Group(Owner(), new ParticipantSnapshot("actor", actorRole, true), Member("target"));

        Assert.Equal(GroupError.Forbidden, Decide(group, "actor", new ChangeRoleMutation("target", SogaRole.Admin)).Error);
    }

    [Fact]
    public void ChangeRole_OwnerPromotesAndDemotes()
    {
        var group = Group(Owner(), Member("bob"));

        var promote = Decide(group, "owner", new ChangeRoleMutation("bob", SogaRole.Admin));
        var same = Decide(group, "owner", new ChangeRoleMutation("bob", SogaRole.Member));

        Assert.Equal(new ParticipantChangedData("bob", "role_changed", "admin", "owner"), Assert.Single(promote.Events).Data);
        Assert.Equal((GroupError.None, false), (same.Error, same.Changed));
    }

    [Fact]
    public void ChangeRole_CannotDemoteTheOnlyOwner()
    {
        Assert.Equal(GroupError.LastOwner, Decide(Group(Owner(), Member()), "owner", new ChangeRoleMutation("owner", SogaRole.Admin)).Error);
    }

    [Fact]
    public void ChangeRole_OwnershipCanBeTransferredInTwoSteps()
    {
        var before = Group(Owner(), Member("bob"));
        var promoted = Group(Owner(), new ParticipantSnapshot("bob", SogaRole.Owner, true));

        Assert.Equal(GroupError.None, Decide(before, "owner", new ChangeRoleMutation("bob", SogaRole.Owner)).Error);
        Assert.Equal(GroupError.None, Decide(promoted, "owner", new ChangeRoleMutation("owner", SogaRole.Member)).Error);
    }

    [Fact]
    public void ChangeRole_OfANonMember_ReturnsParticipantNotFound()
    {
        var group = Group(Owner(), Left("gone"));

        Assert.Equal(GroupError.ParticipantNotFound, Decide(group, "owner", new ChangeRoleMutation("gone", SogaRole.Admin)).Error);
    }

    [Fact]
    public void Update_RequiresAdminAndIgnoresNoOps()
    {
        var group = Group(Owner(), Admin(), Member());

        Assert.Equal(GroupError.Forbidden, Decide(group, "member", new UpdateConversationMutation(true, "New", false, null)).Error);
        Assert.True(Decide(group, "admin", new UpdateConversationMutation(true, "New", false, null)).Changed);
        Assert.False(Decide(group, "admin", new UpdateConversationMutation(true, "Team", false, null)).Changed);
        Assert.True(Decide(group, "admin", new UpdateConversationMutation(false, null, true, "{\"a\":1}")).Changed);
    }

    [Fact]
    public void Update_EventCarriesTheTitleButNeverTheMetadata()
    {
        var decision = Decide(Group(Owner()), "owner", new UpdateConversationMutation(true, "Renamed", true, "{\"secret\":\"x\"}"));

        var data = Assert.IsType<ConversationUpdatedData>(Assert.Single(decision.Events).Data);
        Assert.Equal(("Renamed", true), (data.Title, data.MetadataChanged));
    }

    [Fact]
    public void Archive_RequiresAdminAndIsIdempotent()
    {
        var group = Group(Owner(), Member());

        Assert.Equal(GroupError.Forbidden, Decide(group, "member", new ArchiveMutation(true)).Error);
        Assert.True(Decide(group, "owner", new ArchiveMutation(true)).Changed);
        Assert.False(Decide(group, "owner", new ArchiveMutation(false)).Changed);
    }

    [Theory]
    [InlineData("member", (int)SogaRole.Member)]
    [InlineData("MEMBER", (int)SogaRole.Member)]
    [InlineData(" admin ", (int)SogaRole.Admin)]
    [InlineData("Owner", (int)SogaRole.Owner)]
    [InlineData(null, (int)SogaRole.Member)]
    [InlineData("", (int)SogaRole.Member)]
    public void ParseRole_AcceptsKnownRolesCaseInsensitively(string? wire, int expectedValue)
    {
        var expected = (SogaRole)expectedValue;
        Assert.True(SogaRoles.TryParse(wire, out var role));
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("adm1n")]
    [InlineData("root")]
    public void ParseRole_RejectsUnknownRolesSoATypoNeverGrantsPrivilege(string wire)
    {
        Assert.False(SogaRoles.TryParse(wire, out _));
    }
}
