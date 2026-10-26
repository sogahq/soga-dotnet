using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Messages;

namespace Soga.Tests.Messages;

public sealed class MessageWorkflowTests
{
    private static async Task<Guid> OpenConversationAsync(TestSoga soga) =>
        (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;

    [Fact]
    public async Task Send_AssignsIncreasingSequencesAndDerivesSenderFromCaller()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();

        var first = (await workflow.SendAsync(TestSoga.Alice, id, new("c1", "one"), default)).Value.Value;
        var second = (await workflow.SendAsync(TestSoga.Bob, id, new("c1", "two"), default)).Value.Value;

        Assert.Equal([1L, 2L], [first.Sequence, second.Sequence]);
        Assert.Equal(["alice", "bob"], [first.SenderId, second.SenderId]);
        Assert.Equal("Text", first.Kind);
    }

    [Fact]
    public async Task Send_WhenRequestIsRetried_ReturnsExistingMessage()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();

        var first = await workflow.SendAsync(TestSoga.Alice, id, new("retry-1", "hello"), default);
        var retry = await workflow.SendAsync(TestSoga.Alice, id, new("retry-1", "hello"), default);
        var history = (await workflow.GetHistoryAsync(TestSoga.Alice, id, null, null, default)).Value;

        Assert.True(first.Value.Created);
        Assert.False(retry.Value.Created);
        Assert.Equal(first.Value.Value.Id, retry.Value.Value.Id);
        Assert.Single(history.Items);
    }

    [Fact]
    public async Task Send_WhenSameKeyIsReusedWithDifferentContent_ReturnsIdempotencyConflict()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();
        await workflow.SendAsync(TestSoga.Alice, id, new("key-1", "original"), default);

        var result = await workflow.SendAsync(TestSoga.Alice, id, new("key-1", "changed"), default);

        Assert.Equal(SogaErrorCodes.IdempotencyConflict, result.Error?.Code);
    }

    [Fact]
    public async Task Send_WhenDifferentSendersUseTheSameKey_BothAreAccepted()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();

        var fromAlice = await workflow.SendAsync(TestSoga.Alice, id, new("shared", "a"), default);
        var fromBob = await workflow.SendAsync(TestSoga.Bob, id, new("shared", "b"), default);

        Assert.True(fromAlice.IsSuccess);
        Assert.True(fromBob.Value.Created);
    }

    [Fact]
    public async Task Send_WhenUserIsNotParticipant_ReturnsNotFoundAndStoresNothing()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();

        var result = await workflow.SendAsync(TestSoga.Carol, id, new("c1", "let me in"), default);
        var history = await workflow.GetHistoryAsync(TestSoga.Alice, id, null, null, default);

        Assert.Equal(SogaErrorCodes.ConversationNotFound, result.Error?.Code);
        Assert.Empty(history.Value.Items);
    }

    [Fact]
    public async Task Send_WhenTextExceedsLimit_ReturnsTooLong()
    {
        using var soga = TestSoga.Create(options => options.Messages.MaximumTextLength = 10);
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();

        var exact = await workflow.SendAsync(TestSoga.Alice, id, new("c1", new string('a', 10)), default);
        var over = await workflow.SendAsync(TestSoga.Alice, id, new("c2", new string('a', 11)), default);

        Assert.True(exact.IsSuccess);
        Assert.Equal(SogaErrorCodes.MessageTooLong, over.Error?.Code);
    }

    [Theory]
    [InlineData(null, "text")]
    [InlineData("", "text")]
    [InlineData("has space", "text")]
    [InlineData("emoji-\U0001F600", "text")]
    [InlineData("valid-id", null)]
    [InlineData("valid-id", "   ")]
    public async Task Send_WhenRequestIsInvalid_ReturnsInvalidRequest(string? clientMessageId, string? text)
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);

        var result = await soga.Get<MessageWorkflow>()
            .SendAsync(TestSoga.Alice, id, new(clientMessageId!, text!), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task Send_WhenClientMessageIdIsTooLong_ReturnsInvalidRequest()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);

        var result = await soga.Get<MessageWorkflow>()
            .SendAsync(TestSoga.Alice, id, new(new string('a', 101), "text"), default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task GetHistory_PagesNewestFirstWithStableCursors()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();
        for (var i = 1; i <= 5; i++)
        {
            await workflow.SendAsync(TestSoga.Alice, id, new($"c{i}", $"message {i}"), default);
        }

        var page1 = (await workflow.GetHistoryAsync(TestSoga.Bob, id, null, 2, default)).Value;
        var page2 = (await workflow.GetHistoryAsync(TestSoga.Bob, id, page1.NextCursor, 2, default)).Value;

        // A message arriving between pages must not shift the pages: cursors are positions, not offsets.
        await workflow.SendAsync(TestSoga.Alice, id, new("c6", "message 6"), default);
        var page3 = (await workflow.GetHistoryAsync(TestSoga.Bob, id, page2.NextCursor, 2, default)).Value;

        Assert.Equal([5L, 4L], page1.Items.Select(m => m.Sequence));
        Assert.Equal([3L, 2L], page2.Items.Select(m => m.Sequence));
        Assert.Equal([1L], page3.Items.Select(m => m.Sequence));
        Assert.Null(page3.NextCursor);
    }

    [Fact]
    public async Task GetHistory_WhenCursorBelongsToAnotherConversation_ReturnsInvalidCursor()
    {
        using var soga = TestSoga.Create();
        var conversations = soga.Get<ConversationWorkflow>();
        var workflow = soga.Get<MessageWorkflow>();
        var first = (await conversations.CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var second = (await conversations.CreateDirectAsync(TestSoga.Alice, new("carol"), default)).Value.Value.Id;
        for (var i = 1; i <= 3; i++)
        {
            await workflow.SendAsync(TestSoga.Alice, first, new($"c{i}", "x"), default);
        }

        var cursor = (await workflow.GetHistoryAsync(TestSoga.Alice, first, null, 1, default)).Value.NextCursor;
        var replayed = await workflow.GetHistoryAsync(TestSoga.Alice, second, cursor, 1, default);

        Assert.Equal(SogaErrorCodes.CursorInvalid, replayed.Error?.Code);
    }

    [Fact]
    public async Task GetHistory_WhenUserIsNotParticipant_DoesNotRevealConversation()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);
        var workflow = soga.Get<MessageWorkflow>();
        await workflow.SendAsync(TestSoga.Alice, id, new("c1", "secret"), default);

        var forCarol = await workflow.GetHistoryAsync(TestSoga.Carol, id, null, null, default);
        var forUnknown = await workflow.GetHistoryAsync(TestSoga.Carol, Guid.NewGuid(), null, null, default);

        Assert.Equal(SogaErrorCodes.ConversationNotFound, forCarol.Error?.Code);
        Assert.Equal(forUnknown.Error, forCarol.Error);
    }

    [Fact]
    public async Task GetHistory_WhenLimitIsInvalid_ReturnsInvalidRequest()
    {
        using var soga = TestSoga.Create();
        var id = await OpenConversationAsync(soga);

        var result = await soga.Get<MessageWorkflow>().GetHistoryAsync(TestSoga.Alice, id, null, 0, default);

        Assert.Equal(SogaErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
