using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Extensibility;
using Soga.Persistence;
using Soga.Realtime;
using Soga.Synchronization;
using Soga.Tests.Persistence;

namespace Soga.Tests.Realtime;

/// <summary>
/// The outbox is what turns "stored" into "delivered". These tests prove events are published once committed, retried
/// with backoff when something fails, given up on only after the configured attempts, and that the loop survives faults.
/// </summary>
public sealed class SogaOutboxDispatcherTests
{
    [Fact]
    public async Task DispatchOnce_PublishesEveryPendingEventAndMarksItComplete()
    {
        using var soga = TestSoga.Create();
        var conversation = await CreateDirectAsync(soga);
        await soga.SendAsync(TestSoga.Alice, conversation, "m1", "hello");

        var dispatcher = Dispatcher(soga);
        var published = await dispatcher.DispatchOnceAsync(default);

        Assert.Equal(2, published);
        Assert.Equal(0, (await soga.Get<ISogaStore>().GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending);
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));
    }

    [Fact]
    public async Task DispatchOnce_WhenNothingIsPending_ClaimsNothing()
    {
        using var soga = TestSoga.Create();

        Assert.Equal(0, await Dispatcher(soga).DispatchOnceAsync(default));
    }

    [Fact]
    public async Task DispatchOnce_GivesSubscribersTheSameEventsInTheSameOrderAsSync()
    {
        var recorder = new RecordingSubscriber();
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaEventSubscriber>(recorder));
        var sync = soga.Get<SyncWorkflow>();
        var start = (await sync.SyncAsync(TestSoga.Bob, null, null, default)).Value.Cursor;

        var conversation = await CreateDirectAsync(soga);
        await soga.SendAsync(TestSoga.Alice, conversation, "m1", "hello");
        await Dispatcher(soga).DispatchOnceAsync(default);

        var synced = (await sync.SyncAsync(TestSoga.Bob, start, null, default)).Value.Events.Select(e => e.EventId);
        // Events with the same timestamp are published in commit order, the same order sync returns.
        Assert.Equal(synced, recorder.Received.Select(n => n.Event.EventId));
        Assert.All(recorder.Received, n => Assert.Equal(TestSoga.Tenant, n.TenantId));
    }

    [Fact]
    public async Task DispatchOnce_WhenSubscriberFails_SchedulesARetryInsteadOfLosingTheEvent()
    {
        var subscriber = new FailingSubscriber(failures: 1);
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaEventSubscriber>(subscriber));
        await CreateDirectAsync(soga);
        var dispatcher = Dispatcher(soga);
        var store = soga.Get<ISogaStore>();

        await dispatcher.DispatchOnceAsync(default);

        var afterFailure = await store.GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default);
        Assert.Equal((1, 0), (afterFailure.Pending, afterFailure.Failed));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));

        soga.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(2, subscriber.Calls);
        Assert.Equal(0, (await store.GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending);
    }

    [Fact]
    public async Task DispatchOnce_WhenEveryAttemptFails_MarksTheEventFailedAfterMaximumAttempts()
    {
        var subscriber = new FailingSubscriber(failures: int.MaxValue);
        using var soga = TestSoga.Create(
            options => options.Outbox.MaximumAttempts = 3,
            services => services.AddSingleton<ISogaEventSubscriber>(subscriber));
        await CreateDirectAsync(soga);
        var dispatcher = Dispatcher(soga);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
            soga.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        var stats = await soga.Get<ISogaStore>().GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default);
        Assert.Equal((0, 1), (stats.Pending, stats.Failed));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(3, subscriber.Calls);
    }

    [Fact]
    public async Task DispatchOnce_WhenAnEarlierSubscriberFails_RetriesTheWholeEventSoLaterSubscribersStillReceiveIt()
    {
        var failing = new FailingSubscriber(failures: 1);
        var recorder = new RecordingSubscriber();
        using var soga = TestSoga.Create(services: s =>
        {
            s.AddSingleton<ISogaEventSubscriber>(failing);
            s.AddSingleton<ISogaEventSubscriber>(recorder);
        });
        await CreateDirectAsync(soga);
        var dispatcher = Dispatcher(soga);

        await dispatcher.DispatchOnceAsync(default);
        Assert.Empty(recorder.Received);

        soga.Clock.Advance(TimeSpan.FromSeconds(3));
        await dispatcher.DispatchOnceAsync(default);
        Assert.Single(recorder.Received);
    }

    [Fact]
    public async Task DispatchOnce_WhenTheEventIsReclaimedAfterAFailure_IncrementsTheAttempt()
    {
        var subscriber = new FailingSubscriber(failures: 2);
        using var soga = TestSoga.Create(services: s => s.AddSingleton<ISogaEventSubscriber>(subscriber));
        await CreateDirectAsync(soga);
        var dispatcher = Dispatcher(soga);

        await dispatcher.DispatchOnceAsync(default);
        soga.Clock.Advance(TimeSpan.FromSeconds(3));
        await dispatcher.DispatchOnceAsync(default);
        soga.Clock.Advance(TimeSpan.FromSeconds(6));
        await dispatcher.DispatchOnceAsync(default);

        Assert.Equal(3, subscriber.Calls);
        Assert.Equal(0, (await soga.Get<ISogaStore>().GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending);
    }

    [Fact]
    public async Task Run_WhenTheDispatcherIsDisabled_StopsImmediatelyAndLeavesEventsForAnotherInstance()
    {
        using var soga = TestSoga.Create(options => options.Outbox.DispatcherEnabled = false);
        await CreateDirectAsync(soga);
        var dispatcher = Dispatcher(soga);

        await dispatcher.StartAsync(default);
        await dispatcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(default);

        Assert.Equal(1, (await soga.Get<ISogaStore>().GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending);
    }

    [Fact]
    public async Task Run_WhenStarted_DrainsTheOutboxWithoutBeingCalledDirectly()
    {
        using var soga = TestSoga.Create(options => options.Outbox.PollInterval = TimeSpan.FromMilliseconds(20));
        var dispatcher = Dispatcher(soga);
        var store = soga.Get<ISogaStore>();

        await dispatcher.StartAsync(default);
        try
        {
            await CreateDirectAsync(soga);
            await WaitUntilAsync(async () => (await store.GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending == 0);
        }
        finally
        {
            await dispatcher.StopAsync(default);
        }
    }

    [Fact]
    public async Task Run_WhenAPassThrows_KeepsRunningAndPublishesOnceStorageRecovers()
    {
        var store = new FaultyStore { ClaimFailuresRemaining = 2 };
        using var soga = TestSoga.Create(
            options => options.Outbox.PollInterval = TimeSpan.FromMilliseconds(20),
            services => services.Replace(ServiceDescriptor.Singleton<ISogaStore>(store)));
        await CreateDirectAsync(soga);
        var dispatcher = Dispatcher(soga);

        await dispatcher.StartAsync(default);
        try
        {
            await WaitUntilAsync(async () => (await store.GetOutboxStatsAsync(soga.Clock.GetUtcNow(), default)).Pending == 0);
        }
        finally
        {
            await dispatcher.StopAsync(default);
        }

        Assert.True(store.ClaimCalls >= 3);
    }

    [Fact]
    public async Task Run_WhenStopped_EndsWithoutThrowing()
    {
        using var soga = TestSoga.Create(options => options.Outbox.PollInterval = TimeSpan.FromSeconds(30));
        var dispatcher = Dispatcher(soga);

        await dispatcher.StartAsync(default);
        await Task.Delay(50);
        await dispatcher.StopAsync(default);

        Assert.True(dispatcher.ExecuteTask!.IsCompleted);
        Assert.False(dispatcher.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task Signal_WhenNotifiedBeforeWaiting_ReturnsWithoutWaitingForTheTimeout()
    {
        var signal = new SogaOutboxSignal();
        signal.Notify();
        signal.Notify();

        var wait = signal.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, default);

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Signal_WhenNothingIsNotified_ReturnsAfterTheTimeout()
    {
        var signal = new SogaOutboxSignal();

        await signal.WaitAsync(TimeSpan.FromMilliseconds(20), TimeProvider.System, default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Signal_WhenCanceled_Throws()
    {
        var signal = new SogaOutboxSignal();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => signal.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, cancellation.Token));
    }

    private static SogaOutboxDispatcher Dispatcher(TestSoga soga) =>
        soga.Get<IEnumerable<IHostedService>>().OfType<SogaOutboxDispatcher>().Single();

    private static async Task<Guid> CreateDirectAsync(TestSoga soga)
    {
        var result = await soga.Get<ConversationWorkflow>().CreateDirectAsync(TestSoga.Alice, new CreateDirectConversationRequest("bob"), default);
        return result.Value.Value.Id;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met within 10 seconds.");
            await Task.Delay(20);
        }
    }

    private sealed class RecordingSubscriber : ISogaEventSubscriber
    {
        public List<SogaEventNotification> Received { get; } = [];

        public Task HandleAsync(SogaEventNotification notification, CancellationToken cancellationToken)
        {
            Received.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSubscriber(int failures) : ISogaEventSubscriber
    {
        public int Calls { get; private set; }

        public Task HandleAsync(SogaEventNotification notification, CancellationToken cancellationToken)
        {
            Calls++;
            return Calls <= failures
                ? Task.FromException(new InvalidOperationException("The push service is down."))
                : Task.CompletedTask;
        }
    }
}
