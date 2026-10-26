using Microsoft.EntityFrameworkCore;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;
using Soga.Synchronization;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

public sealed class EfSogaStoreTests : IDisposable
{
    private const string Tenant = "t1";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<ConversationRecord> CreateConversationAsync(string user = "alice", string other = "bob", string? reference = null)
    {
        var outcome = await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, user, other, reference, Guid.NewGuid(), Guid.NewGuid(), Now),
            default);
        return outcome.Conversation;
    }

    private Task<AppendMessageOutcome> SendAsync(
        Guid conversationId,
        string user,
        string clientId,
        string text,
        Guid? eventId = null,
        DateTimeOffset? at = null) =>
        _fixture.CreateStore().AppendMessageAsync(
            new(Tenant, user, conversationId, clientId, text, Guid.NewGuid(), eventId ?? Guid.NewGuid(), at ?? Now),
            default);

    [Fact]
    public async Task GetOrCreateDirectConversation_WhenRepeated_ReturnsTheSameConversation()
    {
        var first = await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "bob", null, Guid.NewGuid(), Guid.NewGuid(), Now), default);
        var second = await _fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(Tenant, "bob", "alice", null, Guid.NewGuid(), Guid.NewGuid(), Now), default);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Conversation.Id, second.Conversation.Id);
        Assert.Equal(["alice", "bob"], second.Conversation.ParticipantIds);
        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.Set<ConversationRow>().CountAsync());
        Assert.Equal(2, await context.Set<ParticipantRow>().CountAsync());
    }

    [Fact]
    public async Task GetOrCreateDirectConversation_WritesCreatedChangeAndOutboxAtomically()
    {
        var conversation = await CreateConversationAsync();

        await using var context = _fixture.CreateContext();
        var change = await context.Set<ChangeRow>().SingleAsync();
        var outbox = await context.Set<OutboxRow>().SingleAsync();
        Assert.Equal(SogaEventTypes.ConversationCreated, change.ChangeType);
        Assert.Equal(conversation.Id, change.ConversationId);
        Assert.Equal(change.EventId, outbox.Id);
        Assert.Equal(change.Payload, outbox.Payload);
        Assert.Equal(1, change.Sequence);
    }

    [Fact]
    public async Task GetOrCreateDirectConversation_WhenExternalReferenceDiffers_CreatesSeparateConversations()
    {
        var general = await CreateConversationAsync();
        var order = await CreateConversationAsync(reference: "order:1");

        Assert.NotEqual(general.Id, order.Id);
        Assert.Equal("order:1", order.ExternalReference);
    }

    [Fact]
    public async Task GetConversation_WhenUserIsNotParticipant_ReturnsNull()
    {
        var conversation = await CreateConversationAsync();

        var forCarol = await _fixture.CreateStore().GetConversationAsync(Tenant, "carol", conversation.Id, default);
        var otherTenant = await _fixture.CreateStore().GetConversationAsync("t2", "alice", conversation.Id, default);

        Assert.Null(forCarol);
        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task AppendMessage_AssignsGaplessSequencesAndWritesChangeAndOutboxWithSharedEventId()
    {
        var conversation = await CreateConversationAsync();

        var first = await SendAsync(conversation.Id, "alice", "c1", "one");
        var second = await SendAsync(conversation.Id, "bob", "c1", "two");

        Assert.Equal([1L, 2L], [first.Message!.Sequence, second.Message!.Sequence]);
        await using var context = _fixture.CreateContext();
        var changes = await context.Set<ChangeRow>().OrderBy(c => c.Sequence).ToListAsync();
        Assert.Equal([1L, 2L, 3L], changes.Select(c => c.Sequence));
        var messageChange = changes[1];
        Assert.Equal(SogaEventTypes.MessageCreated, messageChange.ChangeType);
        Assert.Contains(await context.Set<OutboxRow>().ToListAsync(), o => o.Id == messageChange.EventId && o.Payload == messageChange.Payload);
    }

    [Fact]
    public async Task AppendMessage_WhenRetried_ReturnsExistingMessageWithoutNewRows()
    {
        var conversation = await CreateConversationAsync();
        var first = await SendAsync(conversation.Id, "alice", "retry", "hello");

        var retry = await SendAsync(conversation.Id, "alice", "retry", "hello");

        Assert.Equal(AppendStatus.Duplicate, retry.Status);
        Assert.Equal(first.Message!.Id, retry.Message!.Id);
        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.Set<MessageRow>().CountAsync());
        Assert.Equal(2, await context.Set<ChangeRow>().CountAsync());
        Assert.Equal(2, await context.Set<OutboxRow>().CountAsync());
    }

    [Fact]
    public async Task AppendMessage_WhenKeyIsReusedWithDifferentContent_ReturnsConflict()
    {
        var conversation = await CreateConversationAsync();
        await SendAsync(conversation.Id, "alice", "key", "original");

        var conflict = await SendAsync(conversation.Id, "alice", "key", "changed");

        Assert.Equal(AppendStatus.IdempotencyConflict, conflict.Status);
        Assert.Equal("original", conflict.Message!.Content);
    }

    [Fact]
    public async Task AppendMessage_WhenUserIsNotParticipant_StoresNothing()
    {
        var conversation = await CreateConversationAsync();

        var outcome = await SendAsync(conversation.Id, "mallory", "c1", "hi");

        Assert.Equal(AppendStatus.ConversationNotFound, outcome.Status);
        await using var context = _fixture.CreateContext();
        Assert.Empty(await context.Set<MessageRow>().ToListAsync());
    }

    [Fact]
    public async Task AppendMessage_WhenCommitFails_RollsBackSequenceMessageChangeAndOutbox()
    {
        var conversation = await CreateConversationAsync();
        var reusedEventId = Guid.NewGuid();
        await SendAsync(conversation.Id, "alice", "c1", "one", reusedEventId);

        // Reusing an event ID violates the unique change/outbox key at the very end of the transaction,
        // after the sequence counters were already advanced. Everything must roll back.
        await Assert.ThrowsAsync<DbUpdateException>(() => SendAsync(conversation.Id, "alice", "c2", "two", reusedEventId));

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.Set<MessageRow>().CountAsync());
        Assert.Equal(2, await context.Set<ChangeRow>().CountAsync());
        Assert.Equal(2, await context.Set<OutboxRow>().CountAsync());
        Assert.Equal(1, (await context.Set<ConversationRow>().SingleAsync()).LastSequence);
        Assert.Equal(2, (await context.Set<TenantStateRow>().SingleAsync()).LastChangeSequence);

        var next = await SendAsync(conversation.Id, "alice", "c3", "three");
        Assert.Equal(2, next.Message!.Sequence);
    }

    [Fact]
    public async Task GetMessages_PagesNewestFirstByStableSequence()
    {
        var conversation = await CreateConversationAsync();
        for (var i = 1; i <= 5; i++)
        {
            await SendAsync(conversation.Id, "alice", $"c{i}", $"m{i}");
        }

        var store = _fixture.CreateStore();
        var page1 = await store.GetMessagesAsync(Tenant, "bob", conversation.Id, null, 2, default);
        var page2 = await store.GetMessagesAsync(Tenant, "bob", conversation.Id, page1![^1].Sequence, 2, default);
        var forStranger = await store.GetMessagesAsync(Tenant, "carol", conversation.Id, null, 2, default);

        Assert.Equal([5L, 4L], page1.Select(m => m.Sequence));
        Assert.Equal([3L, 2L], page2!.Select(m => m.Sequence));
        Assert.Null(forStranger);
    }

    [Fact]
    public async Task AdvanceReadPosition_IsMonotonicAndCountsOnlyOthersMessages()
    {
        var conversation = await CreateConversationAsync();
        var messages = new List<MessageRecord>();
        for (var i = 1; i <= 4; i++)
        {
            messages.Add((await SendAsync(conversation.Id, "alice", $"c{i}", $"m{i}")).Message!);
        }

        await SendAsync(conversation.Id, "bob", "b1", "reply");
        var store = _fixture.CreateStore();

        Task<AdvanceReadOutcome> Advance(int index) => _fixture.CreateStore().AdvanceReadPositionAsync(
            new(Tenant, "bob", conversation.Id, messages[index].Id, Guid.NewGuid(), Now), default);

        var toThird = await Advance(2);
        var stale = await Advance(0);
        var toFourth = await Advance(3);

        Assert.Equal((AdvanceStatus.Advanced, 3L, 1L), (toThird.Status, toThird.LastReadSequence, toThird.UnreadCount));
        Assert.Equal((AdvanceStatus.Unchanged, 3L), (stale.Status, stale.LastReadSequence));
        Assert.Equal((AdvanceStatus.Advanced, 4L, 0L), (toFourth.Status, toFourth.LastReadSequence, toFourth.UnreadCount));
        Assert.Equal(1, await store.GetTotalUnreadAsync(Tenant, "alice", default));
    }

    [Fact]
    public async Task AdvanceReadPosition_OnlyEmitsAnEventWhenThePositionMoves()
    {
        var conversation = await CreateConversationAsync();
        var message = (await SendAsync(conversation.Id, "alice", "c1", "one")).Message!;

        var command = new AdvanceReadCommand(Tenant, "bob", conversation.Id, message.Id, Guid.NewGuid(), Now);
        await _fixture.CreateStore().AdvanceReadPositionAsync(command, default);
        await _fixture.CreateStore().AdvanceReadPositionAsync(command with { EventId = Guid.NewGuid() }, default);

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.Set<ChangeRow>().CountAsync(c => c.ChangeType == SogaEventTypes.ReadPositionChanged));
    }

    [Fact]
    public async Task AdvanceReadPosition_WhenMessageBelongsToAnotherConversation_ReturnsMessageNotFound()
    {
        var first = await CreateConversationAsync();
        var second = await CreateConversationAsync(other: "carol");
        var foreignMessage = (await SendAsync(second.Id, "alice", "c1", "elsewhere")).Message!;

        var outcome = await _fixture.CreateStore().AdvanceReadPositionAsync(
            new(Tenant, "bob", first.Id, foreignMessage.Id, Guid.NewGuid(), Now), default);

        Assert.Equal(AdvanceStatus.MessageNotFound, outcome.Status);
    }

    [Fact]
    public async Task ListConversations_ReturnsUnreadCountsWithoutLeakingOtherUsers()
    {
        var withBob = await CreateConversationAsync();
        var withCarol = await CreateConversationAsync(other: "carol");
        await CreateConversationAsync("bob", "carol");
        await SendAsync(withBob.Id, "bob", "b1", "hi alice", at: Now.AddMinutes(1));
        await SendAsync(withBob.Id, "bob", "b2", "again", at: Now.AddMinutes(2));
        await SendAsync(withCarol.Id, "alice", "a1", "hi carol", at: Now.AddMinutes(3));

        var list = await _fixture.CreateStore().ListConversationsAsync(Tenant, "alice", 10, default);

        Assert.Equal([withCarol.Id, withBob.Id], list.Select(c => c.Id));
        Assert.Equal([0L, 2L], list.Select(c => c.UnreadCount));
        Assert.All(list, c => Assert.Contains("alice", c.ParticipantIds));
    }

    [Fact]
    public async Task GetChanges_ReturnsOnlyAccessibleChangesInOrderAndPages()
    {
        var shared = await CreateConversationAsync();
        var private_ = await CreateConversationAsync("bob", "carol");
        var head = await _fixture.CreateStore().GetChangeHeadAsync(Tenant, default);
        for (var i = 1; i <= 3; i++)
        {
            await SendAsync(shared.Id, "bob", $"c{i}", "x");
        }

        await SendAsync(private_.Id, "bob", "p1", "for carol only");

        var page1 = await _fixture.CreateStore().GetChangesAsync(Tenant, "alice", head, 2, default);
        var page2 = await _fixture.CreateStore().GetChangesAsync(Tenant, "alice", page1.Changes[^1].Sequence, 2, default);

        Assert.True(page1.HasMore);
        Assert.Equal(2, page1.Changes.Count);
        Assert.False(page2.HasMore);
        Assert.Single(page2.Changes);
        Assert.True(page1.Changes[0].Sequence < page1.Changes[1].Sequence);
        Assert.True(page1.Changes[1].Sequence < page2.Changes[0].Sequence);
        Assert.All(
            page1.Changes.Concat(page2.Changes),
            c => Assert.Equal(shared.Id, SogaEventSerializer.Deserialize(c.Payload).ConversationId));
    }

    [Fact]
    public async Task GetChanges_AreIsolatedPerTenant()
    {
        await CreateConversationAsync();
        var store = _fixture.CreateStore();

        Assert.Equal(1, await store.GetChangeHeadAsync(Tenant, default));
        Assert.Equal(0, await store.GetChangeHeadAsync("t2", default));
        Assert.Empty((await store.GetChangesAsync("t2", "alice", 0, 10, default)).Changes);
    }

    [Fact]
    public async Task Outbox_ClaimLeaseCompleteAndPurge()
    {
        await CreateConversationAsync();
        var store = _fixture.CreateStore();

        var claimed = (await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default)).Single();
        var competing = await _fixture.CreateStore().ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);
        await store.CompleteOutboxAsync(claimed, Now, default);

        Assert.Equal(1, claimed.Attempt);
        Assert.Empty(competing);
        Assert.Empty(await store.ClaimOutboxAsync(Now.AddHours(1), 10, TimeSpan.FromSeconds(30), default));
        Assert.Equal(1, await store.PurgeOutboxAsync(Now.AddDays(1), default));
    }

    [Fact]
    public async Task Outbox_WhenWorkerCrashes_RedeliversAfterLeaseExpiry()
    {
        await CreateConversationAsync();
        await _fixture.CreateStore().ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default);

        var redelivered = await _fixture.CreateStore().ClaimOutboxAsync(Now.AddSeconds(31), 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal(2, redelivered.Single().Attempt);
    }

    [Fact]
    public async Task Outbox_FailureRetriesLaterThenBecomesTerminal()
    {
        await CreateConversationAsync();
        var store = _fixture.CreateStore();
        var first = (await store.ClaimOutboxAsync(Now, 10, TimeSpan.FromSeconds(30), default)).Single();

        await store.FailOutboxAsync(first, Now.AddSeconds(10), Now, "IOException", default);
        Assert.Empty(await store.ClaimOutboxAsync(Now.AddSeconds(5), 10, TimeSpan.FromSeconds(30), default));

        var second = (await store.ClaimOutboxAsync(Now.AddSeconds(11), 10, TimeSpan.FromSeconds(30), default)).Single();
        await store.FailOutboxAsync(second, null, Now.AddSeconds(11), "IOException", default);

        var stats = await store.GetOutboxStatsAsync(Now.AddSeconds(12), default);
        Assert.Equal((0L, 1L), (stats.Pending, stats.Failed));
        Assert.Empty(await store.ClaimOutboxAsync(Now.AddDays(1), 10, TimeSpan.FromSeconds(30), default));
        await using var context = _fixture.CreateContext();
        Assert.Equal("IOException", (await context.Set<OutboxRow>().SingleAsync()).LastFailure);
    }

    [Fact]
    public async Task Outbox_StatsReportOldestPendingAge()
    {
        await CreateConversationAsync();

        var stats = await _fixture.CreateStore().GetOutboxStatsAsync(Now.AddMinutes(4), default);

        Assert.Equal(TimeSpan.FromMinutes(4), stats.OldestPendingAge);
    }

    [Fact]
    public async Task CheckHealth_WhenDatabaseIsReachable_ReturnsTrue()
    {
        Assert.True(await _fixture.CreateStore().CheckHealthAsync(default));
    }

    [Fact]
    public async Task Timestamps_RoundTripAsUtcWithoutLosingPrecisionOrder()
    {
        var conversation = await CreateConversationAsync();
        var sent = await SendAsync(conversation.Id, "alice", "c1", "hello");

        var loaded = (await _fixture.CreateStore().GetMessagesAsync(Tenant, "alice", conversation.Id, null, 1, default))!.Single();

        Assert.Equal(sent.Message!.CreatedAt, loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
    }
}
