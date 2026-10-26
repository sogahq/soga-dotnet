using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.EntityFrameworkCore.Configuration;
using Soga.EntityFrameworkCore.Extensions;
using Soga.EntityFrameworkCore.Repositories;
using Soga.Errors;
using Soga.Extensions;
using Soga.Groups;
using Soga.Identity;
using Soga.Messages;
using Soga.Persistence;
using Soga.ReadState;
using Soga.Realtime;
using Soga.Synchronization;
using Xunit;

namespace Soga.EntityFrameworkCore.Tests;

/// <summary>
/// Wires Soga the way a host does (<c>AddSoga().UseEntityFrameworkCore&lt;TContext&gt;()</c>) over a real SQLite
/// database, then drives the full feature set and a simulated restart through the real workflows.
/// </summary>
public sealed class HostRegistrationTests : IDisposable
{
    private const string Tenant = "acme";
    private static readonly SogaCaller Alice = new(Tenant, "alice");
    private static readonly SogaCaller Bob = new(Tenant, "bob");
    private static readonly SogaCaller Carol = new(Tenant, "carol");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public HostRegistrationTests()
    {
        _connection.Open();
        using var context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        context.Database.EnsureCreated();
    }

    [Fact]
    public void UseEntityFrameworkCore_ReplacesTheInMemoryStoreWithTheDurableStore()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<ISogaStore>();

        Assert.IsType<EfSogaStore<TestDbContext>>(store);
        Assert.True(store.IsDurable);
    }

    [Fact]
    public void UseEntityFrameworkCore_RegistersTheStoreAsScopedSoEachRequestGetsItsOwnContext()
    {
        using var provider = BuildHost();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(first.ServiceProvider.GetRequiredService<ISogaStore>(), second.ServiceProvider.GetRequiredService<ISogaStore>());
        Assert.Same(first.ServiceProvider.GetRequiredService<ISogaStore>(), first.ServiceProvider.GetRequiredService<ISogaStore>());
    }

    [Fact]
    public void UseEntityFrameworkCore_WithANullBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SogaBuilderExtensions.UseEntityFrameworkCore<TestDbContext>(null!));
    }

    [Fact]
    public async Task Startup_WhenTheContextIncludesTheSogaModel_Succeeds()
    {
        using var provider = BuildHost();

        foreach (var service in provider.GetServices<IHostedService>().OfType<SogaEfStartupCheck<TestDbContext>>())
        {
            await service.StartAsync(default);
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task Startup_WhenTheContextForgotAddSoga_FailsWithAnExplanation()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<PlainDbContext>(o => o.UseSqlite(_connection));
        services.AddSoga().UseEntityFrameworkCore<PlainDbContext>();
        using var provider = services.BuildServiceProvider();
        var check = provider.GetServices<IHostedService>().OfType<SogaEfStartupCheck<PlainDbContext>>().Single();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(default));

        Assert.Contains("modelBuilder.AddSoga()", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PlainDbContext), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelCheck_WithANullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SogaEfModelCheck.Validate(null!));
    }

    [Fact]
    public async Task FullLifecycle_OnARealDatabase_BehavesExactlyAsDocumented()
    {
        using var provider = BuildHost(o => o.Groups.PostSystemMessages = true);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var conversations = services.GetRequiredService<ConversationWorkflow>();
        var groups = services.GetRequiredService<GroupWorkflow>();
        var messages = services.GetRequiredService<MessageWorkflow>();
        var reads = services.GetRequiredService<ReadStateWorkflow>();
        var sync = services.GetRequiredService<SyncWorkflow>();
        var bobCursor = (await sync.SyncAsync(Bob, null, null, default)).Value.Cursor;

        // Direct conversation: created once, then returned.
        var direct = (await conversations.CreateDirectAsync(Alice, new CreateDirectConversationRequest("bob"), default)).Value;
        var again = (await conversations.CreateDirectAsync(Bob, new CreateDirectConversationRequest("alice"), default)).Value;
        Assert.True(direct.Created);
        Assert.False(again.Created);
        Assert.Equal(direct.Value.Id, again.Value.Id);

        // Idempotent send, conflicting retry, ordering.
        var first = (await messages.SendAsync(Alice, direct.Value.Id, new SendMessageRequest("c1", "hello"), default)).Value;
        var retry = (await messages.SendAsync(Alice, direct.Value.Id, new SendMessageRequest("c1", "hello"), default)).Value;
        var conflict = await messages.SendAsync(Alice, direct.Value.Id, new SendMessageRequest("c1", "different"), default);
        var second = (await messages.SendAsync(Bob, direct.Value.Id, new SendMessageRequest("c1", "hi alice"), default)).Value;
        Assert.Equal(first.Value.Id, retry.Value.Id);
        Assert.False(retry.Created);
        Assert.Equal(SogaErrorCodes.IdempotencyConflict, conflict.Error?.Code);
        Assert.Equal((1L, 2L), (first.Value.Sequence, second.Value.Sequence));

        // Read state never moves backward.
        Assert.Equal(1, (await reads.GetTotalUnreadAsync(Bob, default)).Value.TotalUnread);
        Assert.Equal(0, (await reads.AdvanceAsync(Bob, direct.Value.Id, new MarkReadRequest(first.Value.Id), default)).Value.UnreadCount);
        await reads.AdvanceAsync(Alice, direct.Value.Id, new MarkReadRequest(second.Value.Id), default);
        var backwards = (await reads.AdvanceAsync(Alice, direct.Value.Id, new MarkReadRequest(first.Value.Id), default)).Value;
        Assert.Equal(2, backwards.LastReadSequence);

        // Group roles, membership, system messages.
        var group = (await groups.CreateAsync(Alice, new CreateGroupRequest("Launch", ["bob"], "project-1"), default)).Value;
        Assert.True((await groups.AddParticipantAsync(Alice, group.Id, new AddParticipantRequest("carol", "admin"), default)).IsSuccess);
        Assert.Equal(SogaErrorCodes.AuthorizationDenied, (await groups.AddParticipantAsync(Bob, group.Id, new AddParticipantRequest("dave"), default)).Error?.Code);
        Assert.Equal(SogaErrorCodes.GroupLastOwner, (await groups.RemoveParticipantAsync(Alice, group.Id, "alice", default)).Error?.Code);
        Assert.True((await groups.ChangeRoleAsync(Alice, group.Id, "carol", new ChangeRoleRequest("owner"), default)).IsSuccess);
        Assert.Null((await groups.RemoveParticipantAsync(Alice, group.Id, "alice", default)).Value);
        var history = (await messages.GetHistoryAsync(Carol, group.Id, null, null, default)).Value.Items;
        Assert.Equal(3, history.Count(m => m.Kind == SogaMessageKinds.System));

        // Edit, reply, delete.
        var original = (await messages.SendAsync(Bob, group.Id, new SendMessageRequest("g1", "draft"), default)).Value.Value;
        var reply = (await messages.SendAsync(Carol, group.Id, new SendMessageRequest("g2", "reply", original.Id), default)).Value.Value;
        Assert.Equal("final", (await messages.EditAsync(Bob, group.Id, original.Id, new EditMessageRequest("final"), default)).Value.Content);
        Assert.True((await messages.DeleteAsync(Carol, group.Id, original.Id, default)).IsSuccess);
        var page = (await messages.GetHistoryAsync(Bob, group.Id, null, null, default)).Value.Items;
        Assert.True(page.Single(m => m.Id == original.Id).Deleted);
        Assert.True(page.Single(m => m.Id == reply.Id).ReplyTo!.Deleted);

        // Archive blocks new messages but keeps history readable.
        await groups.ArchiveAsync(Carol, group.Id, archived: true, default);
        Assert.Equal(SogaErrorCodes.ConversationArchived, (await messages.SendAsync(Bob, group.Id, new SendMessageRequest("g3", "late"), default)).Error?.Code);
        Assert.NotEmpty((await messages.GetHistoryAsync(Bob, group.Id, null, null, default)).Value.Items);

        // Alice left the group: she can no longer see it, and sync no longer shows her its new changes.
        Assert.Equal(SogaErrorCodes.ConversationNotFound, (await conversations.GetAsync(Alice, group.Id, default)).Error?.Code);

        // Sync replays everything Bob is allowed to see, without duplicates.
        var changes = (await sync.SyncAsync(Bob, bobCursor, 500, default)).Value.Events;
        Assert.Equal(changes.Count, changes.Select(e => e.EventId).Distinct().Count());
        Assert.Contains(changes, e => e.Type == SogaEventTypes.MessageDeleted);

        // Every committed change is published by the outbox.
        var dispatcher = provider.GetServices<IHostedService>().OfType<SogaOutboxDispatcher>().Single();
        while (await dispatcher.DispatchOnceAsync(default) > 0)
        {
        }

        Assert.Equal(0, (await services.GetRequiredService<ISogaStore>().GetOutboxStatsAsync(DateTimeOffset.UtcNow, default)).Pending);
    }

    [Fact]
    public async Task Restart_KeepsEveryMessageAndPublishesEventsThatWereNotSentBeforeTheCrash()
    {
        Guid conversationId;
        using (var before = BuildHost(o => o.Outbox.DispatcherEnabled = false))
        using (var scope = before.CreateScope())
        {
            var created = (await scope.ServiceProvider.GetRequiredService<ConversationWorkflow>()
                .CreateDirectAsync(Alice, new CreateDirectConversationRequest("bob"), default)).Value.Value;
            conversationId = created.Id;
            await scope.ServiceProvider.GetRequiredService<MessageWorkflow>()
                .SendAsync(Alice, conversationId, new SendMessageRequest("c1", "survives"), default);
        }

        using var after = BuildHost();
        using var newScope = after.CreateScope();
        var history = (await newScope.ServiceProvider.GetRequiredService<MessageWorkflow>()
            .GetHistoryAsync(Bob, conversationId, null, null, default)).Value.Items;
        var dispatcher = after.GetServices<IHostedService>().OfType<SogaOutboxDispatcher>().Single();

        Assert.Equal("survives", history.Single().Content);
        Assert.Equal(2, await dispatcher.DispatchOnceAsync(default));
    }

    [Fact]
    public async Task Restart_ContinuesTheSequenceWithoutGapsOrReuse()
    {
        Guid conversationId;
        using (var before = BuildHost())
        using (var scope = before.CreateScope())
        {
            conversationId = (await scope.ServiceProvider.GetRequiredService<ConversationWorkflow>()
                .CreateDirectAsync(Alice, new CreateDirectConversationRequest("bob"), default)).Value.Value.Id;
            await scope.ServiceProvider.GetRequiredService<MessageWorkflow>().SendAsync(Alice, conversationId, new SendMessageRequest("c1", "one"), default);
        }

        using var after = BuildHost();
        using var newScope = after.CreateScope();
        var next = (await newScope.ServiceProvider.GetRequiredService<MessageWorkflow>()
            .SendAsync(Bob, conversationId, new SendMessageRequest("c1", "two"), default)).Value.Value;

        Assert.Equal(2, next.Sequence);
    }

    [Fact]
    public async Task Retry_AfterAnEdit_ReturnsTheOriginalMessageAndTheEditedTextIsAConflict()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<MessageWorkflow>();
        var conversationId = (await scope.ServiceProvider.GetRequiredService<ConversationWorkflow>()
            .CreateDirectAsync(Alice, new CreateDirectConversationRequest("bob"), default)).Value.Value.Id;
        var sent = (await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "hello"), default)).Value.Value;
        await messages.EditAsync(Alice, conversationId, sent.Id, new EditMessageRequest("hello, edited"), default);

        var retry = await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "hello"), default);
        var editedText = await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "hello, edited"), default);

        Assert.Equal((sent.Id, false, "hello, edited"), (retry.Value.Value.Id, retry.Value.Created, retry.Value.Value.Content));
        Assert.Equal(SogaErrorCodes.IdempotencyConflict, editedText.Error?.Code);
    }

    [Fact]
    public async Task Delete_ErasesTheFingerprintTogetherWithTheText()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<MessageWorkflow>();
        var conversationId = (await scope.ServiceProvider.GetRequiredService<ConversationWorkflow>()
            .CreateDirectAsync(Alice, new CreateDirectConversationRequest("bob"), default)).Value.Value.Id;
        var sent = (await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "secret"), default)).Value.Value;

        await messages.DeleteAsync(Alice, conversationId, sent.Id, default);

        await using var context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        var row = await context.Set<Entities.MessageRow>().SingleAsync(m => m.Id == sent.Id);
        Assert.Equal((string.Empty, (string?)null), (row.Content, row.ContentFingerprint));
    }

    [Fact]
    public async Task Retry_OfAMessageStoredBeforeFingerprintsExisted_FallsBackToComparingTheText()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<MessageWorkflow>();
        var conversationId = (await scope.ServiceProvider.GetRequiredService<ConversationWorkflow>()
            .CreateDirectAsync(Alice, new CreateDirectConversationRequest("bob"), default)).Value.Value.Id;
        var sent = (await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "legacy"), default)).Value.Value;
        await using (var context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options))
        {
            await context.Set<Entities.MessageRow>().Where(m => m.Id == sent.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ContentFingerprint, (string?)null));
        }

        var same = await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "legacy"), default);
        var different = await messages.SendAsync(Alice, conversationId, new SendMessageRequest("c1", "other"), default);

        Assert.Equal(sent.Id, same.Value.Value.Id);
        Assert.Equal(SogaErrorCodes.IdempotencyConflict, different.Error?.Code);
    }

    [Fact]
    public async Task OutboxClaim_WhenEventsShareATimestamp_ReturnsThemInCommitOrderNotIdOrder()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var conversationId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options;

        // The later event gets the smaller ID, so ordering by ID would publish the message before its conversation.
        await new EfSogaStore<TestDbContext>(new TestDbContext(options)).GetOrCreateDirectConversationAsync(
            new(Tenant, "alice", "bob", null, conversationId, Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"), now), default);
        await new EfSogaStore<TestDbContext>(new TestDbContext(options)).AppendMessageAsync(
            new(Tenant, "alice", conversationId, "c1", "hi", Guid.NewGuid(), Guid.Parse("00000000-0000-7000-8000-000000000001"), now), default);

        var claimed = await new EfSogaStore<TestDbContext>(new TestDbContext(options)).ClaimOutboxAsync(now, 10, TimeSpan.FromSeconds(30), default);

        Assert.Equal([SogaEventTypes.ConversationCreated, SogaEventTypes.MessageCreated], claimed.Select(c => c.EventType));
    }

    public void Dispose() => _connection.Dispose();

    private ServiceProvider BuildHost(Action<SogaOptions>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<TestDbContext>(o => o.UseSqlite(_connection));
        services
            .AddSoga(options =>
            {
                options.DefaultTenantId = Tenant;
                configure?.Invoke(options);
            })
            .UseEntityFrameworkCore<TestDbContext>();
        return services.BuildServiceProvider();
    }

    /// <summary>A host context that forgot to call <c>modelBuilder.AddSoga()</c>.</summary>
    public sealed class PlainDbContext(DbContextOptions<PlainDbContext> options) : DbContext(options);
}
