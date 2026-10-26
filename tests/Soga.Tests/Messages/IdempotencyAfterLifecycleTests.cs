using Soga.Contracts;
using Soga.Errors;
using Soga.Messages;

namespace Soga.Tests.Messages;

/// <summary>
/// ADR 0011: the same client message ID with identical text returns the original message. These tests check that the
/// promise survives the V2 message lifecycle (edit and delete), which happened after the ADR was written.
/// </summary>
public sealed class IdempotencyAfterLifecycleTests
{
    [Fact]
    public async Task Retry_AfterTheMessageWasDeleted_ReturnsTheTombstoneInsteadOfADuplicate()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var sent = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "hello");
        await soga.Get<MessageWorkflow>().DeleteAsync(TestSoga.Alice, group.Id, sent.Id, default);

        var retry = await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, group.Id, new SendMessageRequest("c1", "hello"), default);

        Assert.False(retry.Value.Created);
        Assert.Equal(sent.Id, retry.Value.Value.Id);
        Assert.True(retry.Value.Value.Deleted);
    }

    [Fact]
    public async Task Retry_OfTheOriginalSend_AfterTheMessageWasEdited_ReturnsTheMessage()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var sent = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "hello");
        await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group.Id, sent.Id, new EditMessageRequest("hello, edited"), default);

        // A phone whose original send timed out retries the exact same request.
        var retry = await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, group.Id, new SendMessageRequest("c1", "hello"), default);

        Assert.True(retry.IsSuccess, $"Expected the original message, got {retry.Error?.Code}.");
        Assert.Equal(sent.Id, retry.Value.Value.Id);
    }

    [Fact]
    public async Task Retry_WithTheEditedText_IsNotTreatedAsTheOriginalRequest()
    {
        using var soga = TestSoga.Create();
        var group = await soga.CreateGroupAsync(TestSoga.Alice, "bob");
        var sent = await soga.SendAsync(TestSoga.Alice, group.Id, "c1", "hello");
        await soga.Get<MessageWorkflow>().EditAsync(TestSoga.Alice, group.Id, sent.Id, new EditMessageRequest("hello, edited"), default);

        var retry = await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, group.Id, new SendMessageRequest("c1", "hello, edited"), default);

        Assert.Equal(SogaErrorCodes.IdempotencyConflict, retry.Error?.Code);
    }
}
