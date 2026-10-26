using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Messages;
using Soga.ReadState;

namespace Soga.Tests.ReadState;

public sealed class ReadStateWorkflowTests
{
    private sealed record Fixture(TestSoga Soga, Guid ConversationId, MessageResponse[] Messages);

    private static async Task<Fixture> ArrangeAsync(int messagesFromAlice)
    {
        var soga = TestSoga.Create();
        var id = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var sender = soga.Get<MessageWorkflow>();
        var messages = new List<MessageResponse>();
        for (var i = 1; i <= messagesFromAlice; i++)
        {
            messages.Add((await sender.SendAsync(TestSoga.Alice, id, new($"c{i}", $"m{i}"), default)).Value.Value);
        }

        return new Fixture(soga, id, [.. messages]);
    }

    [Fact]
    public async Task Unread_CountsOnlyOthersMessagesAfterTheReadPosition()
    {
        var f = await ArrangeAsync(3);
        using var soga = f.Soga;
        var conversations = soga.Get<ConversationWorkflow>();
        await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Bob, f.ConversationId, new("b1", "reply"), default);

        var forBob = (await conversations.GetAsync(TestSoga.Bob, f.ConversationId, default)).Value;
        var forAlice = (await conversations.GetAsync(TestSoga.Alice, f.ConversationId, default)).Value;

        Assert.Equal(3, forBob.UnreadCount);
        Assert.Equal(1, forAlice.UnreadCount);
        Assert.Equal(3, (await soga.Get<ReadStateWorkflow>().GetTotalUnreadAsync(TestSoga.Bob, default)).Value.TotalUnread);
    }

    [Fact]
    public async Task Advance_MovesPositionAndReducesUnread()
    {
        var f = await ArrangeAsync(4);
        using var soga = f.Soga;
        var workflow = soga.Get<ReadStateWorkflow>();

        var state = (await workflow.AdvanceAsync(TestSoga.Bob, f.ConversationId, new(f.Messages[1].Id), default)).Value;

        Assert.Equal(2, state.LastReadSequence);
        Assert.Equal(2, state.UnreadCount);
    }

    [Fact]
    public async Task Advance_WhenSequenceIsOlder_DoesNotMoveBackward()
    {
        var f = await ArrangeAsync(4);
        using var soga = f.Soga;
        var workflow = soga.Get<ReadStateWorkflow>();
        await workflow.AdvanceAsync(TestSoga.Bob, f.ConversationId, new(f.Messages[3].Id), default);

        var stale = (await workflow.AdvanceAsync(TestSoga.Bob, f.ConversationId, new(f.Messages[0].Id), default)).Value;

        Assert.Equal(4, stale.LastReadSequence);
        Assert.Equal(0, stale.UnreadCount);
    }

    [Fact]
    public async Task Advance_WhenRepeated_IsIdempotent()
    {
        var f = await ArrangeAsync(2);
        using var soga = f.Soga;
        var workflow = soga.Get<ReadStateWorkflow>();

        var first = (await workflow.AdvanceAsync(TestSoga.Bob, f.ConversationId, new(f.Messages[1].Id), default)).Value;
        var second = (await workflow.AdvanceAsync(TestSoga.Bob, f.ConversationId, new(f.Messages[1].Id), default)).Value;

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Advance_WhenMessageIsUnknown_ReturnsMessageNotFound()
    {
        var f = await ArrangeAsync(1);
        using var soga = f.Soga;

        var result = await soga.Get<ReadStateWorkflow>()
            .AdvanceAsync(TestSoga.Bob, f.ConversationId, new(Guid.NewGuid()), default);

        Assert.Equal(SogaErrorCodes.MessageNotFound, result.Error?.Code);
    }

    [Fact]
    public async Task Advance_WhenUserIsNotParticipant_ReturnsConversationNotFound()
    {
        var f = await ArrangeAsync(1);
        using var soga = f.Soga;

        var result = await soga.Get<ReadStateWorkflow>()
            .AdvanceAsync(TestSoga.Carol, f.ConversationId, new(f.Messages[0].Id), default);

        Assert.Equal(SogaErrorCodes.ConversationNotFound, result.Error?.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Advance_WhenMessageIdIsMissing_ReturnsInvalidRequest(bool nullRequest)
    {
        var f = await ArrangeAsync(1);
        using var soga = f.Soga;

        var result = await soga.Get<ReadStateWorkflow>()
            .AdvanceAsync(TestSoga.Bob, f.ConversationId, nullRequest ? null : new(Guid.Empty), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
