using System.Text.Json;
using Soga.Contracts;
using Soga.Groups;
using Soga.Messages;
using Soga.Persistence;

namespace Soga.Tests.Groups;

/// <summary>Every membership change posts a machine-readable system message when the host enables it, and none when it does not.</summary>
public sealed class SystemMessageTests
{
    public static TheoryData<string, string, string, string> Changes => new()
    {
        { "added", "participant_added", "alice", "carol" },
        { "removed", "participant_removed", "alice", "bob" },
        { "left", "participant_left", "bob", "bob" },
        { "role_changed", "participant_role_changed", "alice", "bob" },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task MembershipChange_PostsASystemMessageWithTheRightCodeActorAndTarget(string change, string code, string actor, string target)
    {
        using var soga = TestSoga.Create(o => o.Groups.PostSystemMessages = true);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var groups = soga.Get<GroupWorkflow>();
        var caller = actor == "alice" ? TestSoga.Alice : TestSoga.Bob;

        var result = change switch
        {
            "added" => await groups.AddParticipantAsync(caller, group.Id, new AddParticipantRequest(target), default),
            "role_changed" => await groups.ChangeRoleAsync(caller, group.Id, target, new ChangeRoleRequest("admin"), default),
            _ => await groups.RemoveParticipantAsync(caller, group.Id, target, default),
        };
        Assert.True(result.IsSuccess);

        var system = (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, group.Id, null, null, default)).Value.Items
            .Single(m => m.Kind == SogaMessageKinds.System);
        using var body = JsonDocument.Parse(system.Content);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(actor, body.RootElement.GetProperty("actor").GetString());
        Assert.Equal(target, body.RootElement.GetProperty("target").GetString());
        Assert.Equal(SogaSystemSender.UserId, system.SenderId);
    }

    [Fact]
    public async Task MembershipChange_WhenSystemMessagesAreOff_PostsNothing()
    {
        using var soga = TestSoga.Create(o => o.Groups.PostSystemMessages = false);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");

        await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new AddParticipantRequest("carol"), default);

        Assert.Empty((await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, group.Id, null, null, default)).Value.Items);
    }

    [Fact]
    public async Task NoOpChange_PostsNoSystemMessage()
    {
        using var soga = TestSoga.Create(o => o.Groups.PostSystemMessages = true);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");

        await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new AddParticipantRequest("bob"), default);
        await soga.Get<GroupWorkflow>().ChangeRoleAsync(TestSoga.Alice, group.Id, "bob", new ChangeRoleRequest("member"), default);

        Assert.Empty((await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, group.Id, null, null, default)).Value.Items);
    }

    [Fact]
    public async Task SystemMessages_CannotBeEditedOrDeletedByParticipants()
    {
        using var soga = TestSoga.Create(o => o.Groups.PostSystemMessages = true);
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new AddParticipantRequest("carol"), default);
        var system = (await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, group.Id, null, null, default)).Value.Items.Single();

        var edit = await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group.Id, system.Id, new EditMessageRequest("forged"), default);
        var delete = await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, system.Id, default);

        Assert.False(edit.IsSuccess);
        Assert.Equal(Errors.SogaErrorCodes.MessageNotDeletable, delete.Error?.Code);
    }

    [Theory]
    [InlineData("$soga:system")]
    [InlineData(" $soga:system ")]
    [InlineData("$soga:anything")]
    public void ReservedUserIds_AreNeverAcceptedFromTheHost(string userId)
    {
        Assert.Null(Identity.SogaIdentity.NormalizeUserId(userId));
    }

    [Theory]
    [InlineData("soga:system")]
    [InlineData("$SOGA:system")]
    [InlineData("x$soga:system")]
    public void IdsThatOnlyLookSimilar_AreOrdinaryUsers(string userId)
    {
        Assert.Equal(userId, Identity.SogaIdentity.NormalizeUserId(userId));
    }

    [Fact]
    public async Task ReservedUserIds_CannotBeInvitedOrAddressed()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");

        var direct = await soga.Get<Soga.Conversations.ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest(SogaSystemSender.UserId), default);
        var newGroup = await soga.Get<GroupWorkflow>().CreateAsync(TestSoga.Alice, new CreateGroupRequest("T", [SogaSystemSender.UserId]), default);
        var add = await soga.Get<GroupWorkflow>().AddParticipantAsync(TestSoga.Alice, group.Id, new AddParticipantRequest(SogaSystemSender.UserId), default);

        Assert.All([direct.Error?.Code, newGroup.Error?.Code, add.Error?.Code], code => Assert.Equal(Errors.SogaErrorCodes.InvalidRequest, code));
    }
}
