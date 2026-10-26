using Soga.Persistence;
using Soga.Persistence.InMemory;

namespace Soga.Tests.Persistence;

public sealed class InMemoryOutboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(InMemorySogaStore Store, Guid ConversationId)> ArrangeAsync()
    {
        var store = new InMemorySogaStore();
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateDirectConversationAsync(
            new("t1", "alice", "bob", null, conversationId, Guid.NewGuid(), Now),
            default);
        return (store, conversationId);
    }

    [Fact]
    public async Task Claim_LeasesEventsSoCompetingWorkersDoNotDoubleClaim()
    {
        var (store, _) = await ArrangeAsync();

        var first = await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);
        var second = await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(1, first[0].Attempt);
    }

    [Fact]
    public async Task Claim_WhenLeaseExpires_RedeliversAfterAWorkerCrash()
    {
        var (store, _) = await ArrangeAsync();
        await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);

        var afterLease = await store.ClaimOutboxAsync(Now.AddSeconds(31), 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal(2, afterLease.Single().Attempt);
    }

    [Fact]
    public async Task Complete_RemovesEventFromPendingAndCleanupPurgesIt()
    {
        var (store, _) = await ArrangeAsync();
        var claimed = (await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default)).Single();

        await store.CompleteOutboxAsync(claimed, Now, default);

        Assert.Empty(await store.ClaimOutboxAsync(Now.AddHours(1), 10, TimeSpan.FromSeconds(30), default));
        Assert.Equal(0, (await store.GetOutboxStatsAsync(Now, default)).Pending);
        Assert.Equal(1, await store.PurgeOutboxAsync(Now.AddDays(1), default));
    }

    [Fact]
    public async Task Fail_WithRetry_RedeliversOnlyAfterTheRetryTime()
    {
        var (store, _) = await ArrangeAsync();
        var claimed = (await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default)).Single();

        await store.FailOutboxAsync(claimed, Now.AddSeconds(10), Now, "IOException", default);

        Assert.Empty(await store.ClaimOutboxAsync(Now.AddSeconds(5), 10, TimeSpan.FromSeconds(30), default));
        Assert.Single(await store.ClaimOutboxAsync(Now.AddSeconds(11), 10, TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Fail_WithoutRetry_MarksEventTerminalAndVisibleInStats()
    {
        var (store, _) = await ArrangeAsync();
        var claimed = (await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default)).Single();

        await store.FailOutboxAsync(claimed, null, Now, "IOException", default);

        var stats = await store.GetOutboxStatsAsync(Now, default);
        Assert.Empty(await store.ClaimOutboxAsync(Now.AddDays(1), 10, TimeSpan.FromSeconds(30), default));
        Assert.Equal((0, 1), (stats.Pending, stats.Failed));
    }

    [Fact]
    public async Task Stats_ReportsOldestPendingAge()
    {
        var (store, _) = await ArrangeAsync();

        var stats = await store.GetOutboxStatsAsync(Now.AddMinutes(3), default);

        Assert.Equal(1, stats.Pending);
        Assert.Equal(TimeSpan.FromMinutes(3), stats.OldestPendingAge);
    }

    [Fact]
    public async Task Append_WritesMessageChangeAndOutboxTogether()
    {
        var (store, conversationId) = await ArrangeAsync();

        var outcome = await store.AppendMessageAsync(
            new("t1", "alice", conversationId, "c1", "hello", Guid.NewGuid(), Guid.NewGuid(), Now),
            default);

        Assert.Equal(AppendStatus.Created, outcome.Status);
        Assert.Equal(2, (await store.GetOutboxStatsAsync(Now, default)).Pending);
        Assert.Equal(2, await store.GetChangeHeadAsync("t1", default));
    }

    [Fact]
    public async Task Append_WhenRetried_DoesNotWriteASecondChangeOrOutboxEvent()
    {
        var (store, conversationId) = await ArrangeAsync();
        var command = new AppendMessageCommand("t1", "alice", conversationId, "c1", "hello", Guid.NewGuid(), Guid.NewGuid(), Now);
        await store.AppendMessageAsync(command, default);

        var retry = await store.AppendMessageAsync(command with { MessageId = Guid.NewGuid(), EventId = Guid.NewGuid() }, default);

        Assert.Equal(AppendStatus.Duplicate, retry.Status);
        Assert.Equal(2, (await store.GetOutboxStatsAsync(Now, default)).Pending);
        Assert.Equal(2, await store.GetChangeHeadAsync("t1", default));
    }

    [Fact]
    public async Task Append_WhenManySendersRace_AssignsGaplessUniqueSequences()
    {
        var (store, conversationId) = await ArrangeAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 50).Select(i => Task.Run(() =>
            store.AppendMessageAsync(
                new("t1", i % 2 == 0 ? "alice" : "bob", conversationId, $"c{i}", $"m{i}", Guid.NewGuid(), Guid.NewGuid(), Now),
                default))));

        var sequences = outcomes.Select(o => o.Message!.Sequence).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), sequences);
    }
}
