using Microsoft.EntityFrameworkCore;
using Soga.Abstractions;
using Soga.Contracts;
using Soga.EntityFrameworkCore.Entities;
using Soga.Groups;
using Soga.Persistence;
using Soga.Synchronization;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>
/// The guarantees that only a real multi-connection database can prove: row-lock sequence allocation, unique-constraint
/// retries, and a synchronization cursor that never skips a change while writers race.
/// </summary>
public abstract class EfConcurrencyTestsBase(EfStoreFixture fixture) : IDisposable
{
    private const string Tenant = "t-concurrency";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly ISogaIdGenerator _ids = new SogaIdGenerator();

    public void Dispose()
    {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ConversationRecord> DirectAsync(string user = "alice", string other = "bob", string tenant = Tenant) =>
        (await fixture.CreateStore().GetOrCreateDirectConversationAsync(
            new(tenant, user, other, null, Guid.NewGuid(), Guid.NewGuid(), Now), default)).Conversation;

    [SkippableFact]
    public async Task ConcurrentSends_GetGaplessUniqueSequencesAndContiguousChanges()
    {
        var conversation = await DirectAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 40).Select(i => Task.Run(() =>
            fixture.CreateStore().AppendMessageAsync(
                new(Tenant, i % 2 == 0 ? "alice" : "bob", conversation.Id, $"c{i}", $"m{i}", Guid.NewGuid(), Guid.NewGuid(), Now),
                default))));

        Assert.All(outcomes, o => Assert.Equal(AppendStatus.Created, o.Status));
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), outcomes.Select(o => o.Message!.Sequence).Order());

        // 1 conversation-created change + 40 message changes, numbered 1..41 with no gap and no duplicate.
        await using var context = fixture.CreateContext();
        Assert.Equal(Enumerable.Range(1, 41).Select(i => (long)i), await context.Set<ChangeRow>().Select(c => c.Sequence).OrderBy(s => s).ToListAsync());
        Assert.Equal(41, await context.Set<OutboxRow>().CountAsync());
        Assert.Equal(40, (await context.Set<ConversationRow>().SingleAsync()).LastSequence);
    }

    [SkippableFact]
    public async Task ConcurrentIdenticalSends_CreateExactlyOneMessage()
    {
        var conversation = await DirectAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 20).Select(_ => Task.Run(() =>
            fixture.CreateStore().AppendMessageAsync(
                new(Tenant, "alice", conversation.Id, "same-key", "same text", Guid.NewGuid(), Guid.NewGuid(), Now),
                default))));

        Assert.Equal(1, outcomes.Count(o => o.Status == AppendStatus.Created));
        Assert.Equal(19, outcomes.Count(o => o.Status == AppendStatus.Duplicate));
        Assert.Single(outcomes.Select(o => o.Message!.Id).Distinct());
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Set<MessageRow>().CountAsync());
        Assert.Equal(2, await context.Set<ChangeRow>().CountAsync());
    }

    [SkippableFact]
    public async Task ConcurrentSameKeyDifferentText_YieldsOneWinnerAndConflictsForTheRest()
    {
        var conversation = await DirectAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Task.Run(() =>
            fixture.CreateStore().AppendMessageAsync(
                new(Tenant, "alice", conversation.Id, "one-key", $"text {i}", Guid.NewGuid(), Guid.NewGuid(), Now),
                default))));

        Assert.Equal(1, outcomes.Count(o => o.Status == AppendStatus.Created));
        Assert.Equal(11, outcomes.Count(o => o.Status == AppendStatus.IdempotencyConflict));
    }

    [SkippableFact]
    public async Task ConcurrentDirectConversationCreation_YieldsOneConversation()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(() =>
            fixture.CreateStore().GetOrCreateDirectConversationAsync(
                new(Tenant, i % 2 == 0 ? "alice" : "bob", i % 2 == 0 ? "bob" : "alice", null, Guid.NewGuid(), Guid.NewGuid(), Now),
                default))));

        Assert.Equal(1, results.Count(r => r.Created));
        Assert.Single(results.Select(r => r.Conversation.Id).Distinct());
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Set<ConversationRow>().CountAsync());
        Assert.Equal(2, await context.Set<ParticipantRow>().CountAsync());
    }

    [SkippableFact]
    public async Task ConcurrentFirstWritesForANewTenant_AllSucceedWithUniqueChangeSequences()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, 15).Select(i => Task.Run(() =>
            DirectAsync($"user{i}", $"peer{i}", "brand-new-tenant"))));

        Assert.Equal(15, results.Select(r => r.Id).Distinct().Count());
        await using var context = fixture.CreateContext();
        var sequences = await context.Set<ChangeRow>().Where(c => c.TenantId == "brand-new-tenant").Select(c => c.Sequence).OrderBy(s => s).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 15).Select(i => (long)i), sequences);
        Assert.Equal(15, (await context.Set<TenantStateRow>().SingleAsync(t => t.TenantId == "brand-new-tenant")).LastChangeSequence);
    }

    [SkippableFact]
    public async Task ASyncReaderPollingWhileWritersRace_NeverMissesOrRepeatsAChange()
    {
        // ADR 0008 on a real database: because change numbers are handed out under a lock held until commit, a
        // change with a lower number can never become visible after a higher one was read.
        var conversation = await DirectAsync();
        var seen = new List<long>();
        var after = 0L;
        var writersDone = false;

        async Task PollOnceAsync()
        {
            while (true)
            {
                var page = await fixture.CreateStore().GetChangesAsync(Tenant, "alice", after, 7, default);
                seen.AddRange(page.Changes.Select(c => c.Sequence));
                if (page.Changes.Count > 0)
                {
                    after = page.Changes[^1].Sequence;
                }

                if (!page.HasMore)
                {
                    return;
                }
            }
        }

        var poller = Task.Run(async () =>
        {
            while (!Volatile.Read(ref writersDone))
            {
                await PollOnceAsync();
            }
        });

        var writers = Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                var result = await fixture.CreateStore().AppendMessageAsync(
                    new(Tenant, w % 2 == 0 ? "alice" : "bob", conversation.Id, $"w{w}-{i}", "x", Guid.NewGuid(), Guid.NewGuid(), Now),
                    default);
                Assert.Equal(AppendStatus.Created, result.Status);
            }
        }));

        await Task.WhenAll(writers);
        Volatile.Write(ref writersDone, true);
        await poller;
        await PollOnceAsync();

        var head = await fixture.CreateStore().GetChangeHeadAsync(Tenant, default);
        Assert.Equal(49, head);
        Assert.Equal(Enumerable.Range(1, 49).Select(i => (long)i), seen);
    }

    [SkippableFact]
    public async Task ConcurrentGroupAdds_RespectTheSizeLimit()
    {
        var group = await fixture.CreateStore().CreateGroupAsync(
            new(Tenant, "alice", "Team", ["bob"], null, null, Guid.NewGuid(), Guid.NewGuid(), Now), default);

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(() =>
            fixture.CreateStore().MutateGroupAsync(
                new(Tenant, "alice", group.Id, new AddParticipantMutation($"user{i}", SogaRole.Member), 21, false, Guid.NewGuid(), _ids, Now),
                default))));

        Assert.Equal(19, outcomes.Count(o => o is { Error: GroupError.None, Changed: true }));
        Assert.Equal(1, outcomes.Count(o => o.Error == GroupError.TooLarge));
        Assert.Equal(21, (await fixture.CreateStore().GetConversationAsync(Tenant, "alice", group.Id, default))!.ParticipantCount);
    }

    [SkippableFact]
    public async Task ConcurrentLastOwnerDepartures_NeverLeaveAGroupWithoutAnOwner()
    {
        var group = await fixture.CreateStore().CreateGroupAsync(
            new(Tenant, "alice", "Team", ["bob"], null, null, Guid.NewGuid(), Guid.NewGuid(), Now), default);
        await fixture.CreateStore().MutateGroupAsync(
            new(Tenant, "alice", group.Id, new ChangeRoleMutation("bob", SogaRole.Owner), 100, false, Guid.NewGuid(), _ids, Now), default);

        var outcomes = await Task.WhenAll(
            Task.Run(() => fixture.CreateStore().MutateGroupAsync(
                new(Tenant, "alice", group.Id, new RemoveParticipantMutation("alice"), 100, false, Guid.NewGuid(), _ids, Now), default)),
            Task.Run(() => fixture.CreateStore().MutateGroupAsync(
                new(Tenant, "bob", group.Id, new RemoveParticipantMutation("bob"), 100, false, Guid.NewGuid(), _ids, Now), default)));

        Assert.Equal(1, outcomes.Count(o => o.Error == GroupError.None && o.Changed));
        Assert.Equal(1, outcomes.Count(o => o.Error == GroupError.LastOwner));
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Set<ParticipantRow>().CountAsync(p => p.Role == SogaRole.Owner && p.LeftAt == null));
    }

    [SkippableFact]
    public async Task ConcurrentReadAdvances_AreMonotonicAndEmitOnlyIncreasingEvents()
    {
        var conversation = await DirectAsync();
        var messages = new List<MessageRecord>();
        for (var i = 1; i <= 12; i++)
        {
            messages.Add((await fixture.CreateStore().AppendMessageAsync(
                new(Tenant, "alice", conversation.Id, $"c{i}", $"m{i}", Guid.NewGuid(), Guid.NewGuid(), Now), default)).Message!);
        }

        // Twelve devices race to report different read positions, in scrambled order.
        var order = Enumerable.Range(0, 12).OrderBy(i => (i * 7) % 12).ToList();
        await Task.WhenAll(order.Select(i => Task.Run(() =>
            fixture.CreateStore().AdvanceReadPositionAsync(
                new(Tenant, "bob", conversation.Id, messages[i].Id, Guid.NewGuid(), Now), default))));

        var final = await fixture.CreateStore().GetConversationAsync(Tenant, "bob", conversation.Id, default);
        Assert.Equal(12, final!.LastReadSequence);
        await using var context = fixture.CreateContext();
        var advances = (await context.Set<ChangeRow>()
            .Where(c => c.ChangeType == SogaEventTypes.ReadPositionChanged)
            .OrderBy(c => c.Sequence)
            .Select(c => c.Payload)
            .ToListAsync())
            .Select(p => SogaEventSerializer.ReadData<ReadPositionChangedData>(SogaEventSerializer.Deserialize(p)).LastReadSequence)
            .ToList();
        Assert.Equal(advances.Order(), advances);
        Assert.Equal(advances.Count, advances.Distinct().Count());
    }

    [SkippableFact]
    public async Task CompetingOutboxWorkers_NeverClaimTheSameEventTwice()
    {
        var conversation = await DirectAsync();
        for (var i = 1; i <= 29; i++)
        {
            await fixture.CreateStore().AppendMessageAsync(
                new(Tenant, "alice", conversation.Id, $"c{i}", $"m{i}", Guid.NewGuid(), Guid.NewGuid(), Now), default);
        }

        var claimed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var batch = await fixture.CreateStore().ClaimOutboxAsync(Now, 5, TimeSpan.FromMinutes(5), default);
                if (batch.Count == 0)
                {
                    return;
                }

                foreach (var message in batch)
                {
                    claimed.Add(message.Id);
                }
            }
        })));

        Assert.Equal(30, claimed.Count);
        Assert.Equal(30, claimed.Distinct().Count());
    }

    [SkippableFact]
    public async Task ConcurrentEdits_KeepContiguousHistoryVersions()
    {
        var conversation = await DirectAsync();
        var message = (await fixture.CreateStore().AppendMessageAsync(
            new(Tenant, "alice", conversation.Id, "c1", "original", Guid.NewGuid(), Guid.NewGuid(), Now), default)).Message!;

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() =>
            fixture.CreateStore().EditMessageAsync(
                new(Tenant, "alice", conversation.Id, message.Id, $"edit {i}", Guid.NewGuid(), _ids, Now, null, true), default))));

        Assert.All(outcomes, o => Assert.Equal(EditStatus.Updated, o.Status));
        await using var context = fixture.CreateContext();
        var versions = await context.Set<MessageEditRow>().Select(e => e.Version).OrderBy(v => v).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 8), versions);
        Assert.Equal(8, await context.Set<ChangeRow>().CountAsync(c => c.ChangeType == SogaEventTypes.MessageUpdated));
    }

    [SkippableFact]
    public async Task SendsRacingARemoval_NeverLeakAMessageToTheRemovedMember()
    {
        var group = await fixture.CreateStore().CreateGroupAsync(
            new(Tenant, "alice", "Team", ["bob", "carol"], null, null, Guid.NewGuid(), Guid.NewGuid(), Now), default);
        var removedAt = 0L;

        var remove = Task.Run(async () =>
        {
            var outcome = await fixture.CreateStore().MutateGroupAsync(
                new(Tenant, "alice", group.Id, new RemoveParticipantMutation("bob"), 100, false, Guid.NewGuid(), _ids, Now), default);
            Assert.Equal(GroupError.None, outcome.Error);
            Interlocked.Exchange(ref removedAt, 1);
        });
        var sends = Enumerable.Range(1, 20).Select(i => Task.Run(() =>
            fixture.CreateStore().AppendMessageAsync(
                new(Tenant, "alice", group.Id, $"c{i}", $"m{i}", Guid.NewGuid(), Guid.NewGuid(), Now), default)));
        await Task.WhenAll(sends.Append(remove));

        // After removal nothing is visible to Bob, whatever the interleaving was.
        Assert.Null(await fixture.CreateStore().GetMessagesAsync(Tenant, "bob", group.Id, null, 50, default));
        var carolSees = await fixture.CreateStore().GetMessagesAsync(Tenant, "carol", group.Id, null, 50, default);
        Assert.Equal(20, carolSees!.Count);
        Assert.Equal(1, Interlocked.Read(ref removedAt));
    }
}
