using Soga.Contracts;
using Soga.Persistence;
using Soga.Persistence.InMemory;

namespace Soga.Tests.Persistence;

/// <summary>Events committed in the same instant are claimed in commit order, whatever their IDs.</summary>
public sealed class OutboxOrderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Claim_WhenEventsShareATimestamp_ReturnsThemInCommitOrderNotIdOrder()
    {
        var store = new InMemorySogaStore();
        var conversationId = Guid.NewGuid();

        // The later event gets the smaller ID, so ordering by ID would publish the message before its conversation.
        await store.GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", null, conversationId, Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"), Now), default);
        await store.AppendMessageAsync(
            new("t1", "alice", conversationId, "c1", "hi", Guid.NewGuid(), Guid.Parse("00000000-0000-7000-8000-000000000001"), Now), default);

        var claimed = await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal([SogaEventTypes.ConversationCreated, SogaEventTypes.MessageCreated], claimed.Select(c => c.EventType));
    }

    [Fact]
    public async Task Claim_StillPutsOlderEventsFirst()
    {
        var store = new InMemorySogaStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await store.GetOrCreateDirectConversationAsync(new("t1", "alice", "bob", null, first, Guid.NewGuid(), Now.AddSeconds(5)), default);
        await store.GetOrCreateDirectConversationAsync(new("t1", "alice", "carol", null, second, Guid.NewGuid(), Now), default);

        var claimed = await store.ClaimOutboxAsync(Now.AddMinutes(1), 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal([second, first], claimed.Select(c => Soga.Synchronization.SogaEventSerializer.Deserialize(c.Payload).ConversationId));
    }
}
