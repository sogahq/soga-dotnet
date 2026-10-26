using Soga.Contracts;
using Soga.Groups;
using Soga.Messages;

namespace Soga.Tests.Messages;

public sealed class MessageRulesTests
{
    private static readonly DateTimeOffset Sent = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanEdit_OnlyTheSenderMay()
    {
        Assert.Equal(EditCheck.Allowed, MessageRules.CanEdit(SogaMessageKinds.Text, "alice", Sent, false, "alice", Sent, null));
        Assert.Equal(EditCheck.Forbidden, MessageRules.CanEdit(SogaMessageKinds.Text, "alice", Sent, false, "bob", Sent, null));
    }

    [Fact]
    public void CanEdit_ForbiddenTakesPrecedenceSoOthersLearnNothingAboutState()
    {
        Assert.Equal(EditCheck.Forbidden, MessageRules.CanEdit(SogaMessageKinds.System, "alice", Sent, true, "bob", Sent, TimeSpan.Zero));
    }

    [Fact]
    public void CanEdit_SystemAndDeletedMessagesAreNotEditable()
    {
        Assert.Equal(EditCheck.NotEditable, MessageRules.CanEdit(SogaMessageKinds.System, "alice", Sent, false, "alice", Sent, null));
        Assert.Equal(EditCheck.NotEditable, MessageRules.CanEdit(SogaMessageKinds.Text, "alice", Sent, true, "alice", Sent, null));
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void CanEdit_EnforcesTheEditWindow(int minutes, bool allowed)
    {
        var check = MessageRules.CanEdit(SogaMessageKinds.Text, "alice", Sent, false, "alice", Sent.AddMinutes(minutes), TimeSpan.FromMinutes(15));

        Assert.Equal(allowed ? EditCheck.Allowed : EditCheck.WindowExpired, check);
    }

    [Fact]
    public void CanEdit_WithoutAWindowThereIsNoTimeLimit()
    {
        Assert.Equal(EditCheck.Allowed, MessageRules.CanEdit(SogaMessageKinds.Text, "alice", Sent, false, "alice", Sent.AddYears(5), null));
    }

    [Fact]
    public void CanDelete_SenderMayDeleteOwnText()
    {
        var check = MessageRules.CanDelete(SogaMessageKinds.Text, "alice", false, "alice", SogaRole.Member, false, true, true);

        Assert.Equal(DeleteCheck.Allowed, check);
    }

    [Theory]
    [InlineData((int)SogaRole.Member, (int)DeleteCheck.Forbidden)]
    [InlineData((int)SogaRole.Admin, (int)DeleteCheck.Allowed)]
    [InlineData((int)SogaRole.Owner, (int)DeleteCheck.Allowed)]
    public void CanDelete_InAGroupOnlyAdminsMayDeleteOthers(int roleValue, int expectedValue)
    {
        var role = (SogaRole)roleValue;
        var expected = (DeleteCheck)expectedValue;
        Assert.Equal(expected, MessageRules.CanDelete(SogaMessageKinds.Text, "alice", false, "actor", role, true, true, true));
    }

    [Fact]
    public void CanDelete_InADirectConversationNobodyMayDeleteTheOthersMessage()
    {
        Assert.Equal(DeleteCheck.Forbidden, MessageRules.CanDelete(SogaMessageKinds.Text, "alice", false, "bob", SogaRole.Owner, false, true, true));
    }

    [Fact]
    public void CanDelete_AdminDeletionCanBeSwitchedOff()
    {
        Assert.Equal(DeleteCheck.Forbidden, MessageRules.CanDelete(SogaMessageKinds.Text, "alice", false, "admin", SogaRole.Admin, true, true, false));
    }

    [Fact]
    public void CanDelete_OwnDeletionCanBeSwitchedOff()
    {
        Assert.Equal(DeleteCheck.Forbidden, MessageRules.CanDelete(SogaMessageKinds.Text, "alice", false, "alice", SogaRole.Member, false, false, true));
    }

    [Fact]
    public void CanDelete_SystemMessagesAreNeverDeletableByParticipants()
    {
        Assert.Equal(DeleteCheck.NotDeletable, MessageRules.CanDelete(SogaMessageKinds.System, "$soga:system", false, "owner", SogaRole.Owner, true, true, true));
    }

    [Fact]
    public void CanDelete_DeletingTwiceIsRecognisedSoItCanBeIdempotent()
    {
        Assert.Equal(DeleteCheck.AlreadyDeleted, MessageRules.CanDelete(SogaMessageKinds.Text, "alice", true, "alice", SogaRole.Member, false, true, true));
    }
}
