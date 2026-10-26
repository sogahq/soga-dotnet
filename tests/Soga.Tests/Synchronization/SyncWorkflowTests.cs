using Soga.Contracts;
using Soga.Conversations;
using Soga.Errors;
using Soga.Messages;
using Soga.ReadState;
using Soga.Synchronization;

namespace Soga.Tests.Synchronization;

public sealed class SyncWorkflowTests
{
    [Fact]
    public async Task Sync_WithoutCursor_ReturnsHeadCursorAndNoHistory()
    {
        using var soga = TestSoga.Create();
        var conversationId = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, conversationId, new("c1", "old"), default);
        var sync = soga.Get<SyncWorkflow>();

        var start = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value;
        var next = (await sync.SyncAsync(TestSoga.Bob, start.Cursor, null, default)).Value;

        Assert.Empty(start.Events);
        Assert.Empty(next.Events);
    }

    [Fact]
    public async Task Sync_AfterDisconnect_RecoversEveryMissedChangeInOrder()
    {
        using var soga = TestSoga.Create();
        var conversationId = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var messages = soga.Get<MessageWorkflow>();
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        // Bob is "offline" while Alice sends two messages.
        var m1 = (await messages.SendAsync(TestSoga.Alice, conversationId, new("c1", "one"), default)).Value.Value;
        var m2 = (await messages.SendAsync(TestSoga.Alice, conversationId, new("c2", "two"), default)).Value.Value;

        var recovered = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value;

        Assert.All(recovered.Events, e => Assert.Equal(SogaEventTypes.MessageCreated, e.Type));
        Assert.Equal(
            [m1.Id, m2.Id],
            recovered.Events.Select(e => SogaEventSerializer.ReadData<MessageCreatedData>(e).Message.Id));

        var caughtUp = (await sync.SyncAsync(TestSoga.Bob, recovered.Cursor, null, default)).Value;
        Assert.Empty(caughtUp.Events);
    }

    [Fact]
    public async Task Sync_ReturnsTheSameEventIdsAsTheOutbox()
    {
        using var soga = TestSoga.Create();
        var conversationId = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;
        await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, conversationId, new("c1", "one"), default);

        var synced = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value.Events.Single();
        var store = soga.Get<Soga.Persistence.ISogaStore>();
        var outbox = (await store.ClaimOutboxAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10, TimeSpan.FromMinutes(1), default))
            .Single(o => o.EventType == SogaEventTypes.MessageCreated);

        Assert.Equal(synced.EventId, outbox.Id);
        Assert.Equal(synced.EventId, SogaEventSerializer.Deserialize(outbox.Payload).EventId);
    }

    [Fact]
    public async Task Sync_IncludesConversationCreatedAndReadPositionChanges()
    {
        using var soga = TestSoga.Create();
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        var conversationId = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var message = (await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, conversationId, new("c1", "one"), default)).Value.Value;
        await soga.Get<ReadStateWorkflow>().AdvanceAsync(TestSoga.Bob, conversationId, new(message.Id), default);

        var events = (await sync.SyncAsync(TestSoga.Bob, cursor, null, default)).Value.Events;

        Assert.Equal(
            [SogaEventTypes.ConversationCreated, SogaEventTypes.MessageCreated, SogaEventTypes.ReadPositionChanged],
            events.Select(e => e.Type));
    }

    [Fact]
    public async Task Sync_PagesWithHasMoreUntilCaughtUp()
    {
        using var soga = TestSoga.Create();
        var conversationId = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;
        for (var i = 1; i <= 5; i++)
        {
            await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, conversationId, new($"c{i}", "x"), default);
        }

        var seen = new List<Guid>();
        SyncResponse page;
        do
        {
            page = (await sync.SyncAsync(TestSoga.Bob, cursor, 2, default)).Value;
            seen.AddRange(page.Events.Select(e => e.EventId));
            cursor = page.Cursor;
        }
        while (page.HasMore);

        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Sync_DoesNotLeakOtherUsersConversations()
    {
        using var soga = TestSoga.Create();
        var conversationId = (await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new("bob"), default)).Value.Value.Id;
        var sync = soga.Get<SyncWorkflow>();
        var cursor = (await sync.SyncAsync(TestSoga.Carol, null, null, default)).Value.Cursor;
        await soga.Get<MessageWorkflow>().SendAsync(TestSoga.Alice, conversationId, new("c1", "private"), default);

        var events = (await sync.SyncAsync(TestSoga.Carol, cursor, null, default)).Value.Events;

        Assert.Empty(events);
    }

    [Fact]
    public async Task Sync_WhenCursorBelongsToAnotherUser_ReturnsInvalidCursor()
    {
        using var soga = TestSoga.Create();
        var sync = soga.Get<SyncWorkflow>();
        var alicesCursor = (await sync.SyncAsync(TestSoga.Alice, null, null, default)).Value.Cursor;

        var result = await sync.SyncAsync(TestSoga.Bob, alicesCursor, null, default);

        Assert.Equal(SogaErrorCodes.CursorInvalid, result.Error?.Code);
    }

    [Fact]
    public async Task Sync_WhenCursorIsGarbage_ReturnsInvalidCursor()
    {
        using var soga = TestSoga.Create();

        var result = await soga.Get<SyncWorkflow>().SyncAsync(TestSoga.Alice, "garbage", null, default);

        Assert.Equal(SogaErrorCodes.CursorInvalid, result.Error?.Code);
    }
}
