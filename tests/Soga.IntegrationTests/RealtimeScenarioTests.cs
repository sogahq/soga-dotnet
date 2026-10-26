using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Soga.Contracts;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Synchronization;

namespace Soga.IntegrationTests;

/// <summary>Realtime scenarios over real SignalR connections: subscriptions, fan-out, ordering, recovery, and host hooks.</summary>
public sealed class RealtimeScenarioTests
{
    private const string P = SogaTestServer.Prefix;

    [Fact]
    public async Task Unsubscribe_StopsEventsForThatConnectionButNotTheUsersOtherDevices()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        await using var phone = server.Hub("bob");
        await using var laptop = server.Hub("bob");
        var phoneInbox = new EventInbox(phone);
        var laptopInbox = new EventInbox(laptop);
        await ConnectAndSubscribeAsync(phone, conversation.Id);
        await ConnectAndSubscribeAsync(laptop, conversation.Id);

        await phone.InvokeAsync("UnsubscribeFromConversation", conversation.Id);
        await alice.SendAsync(conversation.Id, "c1", "only the laptop");

        Assert.Equal("only the laptop", MessageOf(await laptopInbox.NextAsync(SogaEventTypes.MessageCreated)).Content);
        Assert.True(await phoneInbox.StaysQuietAsync(TimeSpan.FromMilliseconds(400), SogaEventTypes.MessageCreated));
    }

    [Fact]
    public async Task Unsubscribe_FromAConversationNeverSubscribedTo_IsHarmless()
    {
        await using var server = await SogaTestServer.StartAsync();
        await using var hub = server.Hub("bob");
        await hub.StartAsync();

        await hub.InvokeAsync("UnsubscribeFromConversation", Guid.NewGuid());

        Assert.Equal(SogaProtocol.Version, await hub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version));
    }

    [Fact]
    public async Task Subscribe_Twice_StillDeliversEachEventOnce()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        await using var hub = server.Hub("bob");
        var inbox = new EventInbox(hub);
        await ConnectAndSubscribeAsync(hub, conversation.Id);
        await hub.InvokeAsync("SubscribeToConversation", conversation.Id);

        await alice.SendAsync(conversation.Id, "c1", "once");

        await inbox.NextAsync(SogaEventTypes.MessageCreated);
        Assert.True(await inbox.StaysQuietAsync(TimeSpan.FromMilliseconds(400), SogaEventTypes.MessageCreated));
    }

    [Fact]
    public async Task Hub_WhenTheTenantCannotBeResolved_RefusesTheConnection()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddScoped<Soga.Tenancy.ISogaTenantContext, HeaderTenantContext>());
        await using var hub = server.Hub("bob", tenant: null);
        var closed = new TaskCompletionSource();
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await hub.StartAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or HubException)
        {
            // Refusing during the handshake is equally acceptable.
        }

        Assert.NotEqual(HubConnectionState.Connected, hub.State);
    }

    [Fact]
    public async Task ReadPosition_IsPushedToTheOtherParticipantInRealtime()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        var message = await alice.SendAsync(conversation.Id, "c1", "read me");
        await using var aliceHub = server.Hub("alice");
        var inbox = new EventInbox(aliceHub);
        await ConnectAndSubscribeAsync(aliceHub, conversation.Id);

        (await server.Client("bob").PostAsJsonAsync($"{P}/conversations/{conversation.Id}/read", new MarkReadRequest(message.Id))).EnsureSuccessStatusCode();

        var evt = await inbox.NextAsync(SogaEventTypes.ReadPositionChanged);
        var data = evt.Data.Deserialize<ReadPositionChangedData>(SogaEventSerializer.Options)!;
        Assert.Equal(("bob", message.Sequence), (data.UserId, data.LastReadSequence));
    }

    [Fact]
    public async Task NewGroup_IsAnnouncedToEveryMemberWithoutASubscription()
    {
        await using var server = await SogaTestServer.StartAsync();
        await using var bobHub = server.Hub("bob");
        await using var carolHub = server.Hub("carol");
        var bobInbox = new EventInbox(bobHub);
        var carolInbox = new EventInbox(carolHub);
        await bobHub.StartAsync();
        await carolHub.StartAsync();
        await bobHub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version);
        await carolHub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version);

        var group = await server.Client("alice").CreateGroupAsync("Launch", "bob", "carol");

        Assert.Equal(group.Id, (await bobInbox.NextAsync(SogaEventTypes.ConversationCreated)).ConversationId);
        Assert.Equal(group.Id, (await carolInbox.NextAsync(SogaEventTypes.ConversationCreated)).ConversationId);
    }

    [Fact]
    public async Task GroupRenameAndArchive_ArePushedToSubscribers()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Old", "bob");
        await using var hub = server.Hub("bob");
        var inbox = new EventInbox(hub);
        await ConnectAndSubscribeAsync(hub, group.Id);

        (await alice.PatchAsJsonAsync($"{P}/conversations/{group.Id}", new UpdateConversationRequest("New"))).EnsureSuccessStatusCode();
        (await alice.PostAsync($"{P}/conversations/{group.Id}/archive", null)).EnsureSuccessStatusCode();

        var renamed = (await inbox.NextAsync(SogaEventTypes.ConversationUpdated)).Data.Deserialize<ConversationUpdatedData>(SogaEventSerializer.Options)!;
        var archived = (await inbox.NextAsync(SogaEventTypes.ConversationArchived)).Data.Deserialize<ConversationArchivedData>(SogaEventSerializer.Options)!;
        Assert.Equal("New", renamed.Title);
        Assert.True(archived.Archived);
    }

    [Fact]
    public async Task ManyMessages_ArriveWithGaplessSequencesSoClientsCanOrderThem()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        await using var hub = server.Hub("bob");
        var inbox = new EventInbox(hub);
        await ConnectAndSubscribeAsync(hub, conversation.Id);

        await Task.WhenAll(Enumerable.Range(1, 10).Select(i => alice.SendAsync(conversation.Id, $"c{i}", $"message {i}")));

        var received = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            received.Add(MessageOf(await inbox.NextAsync(SogaEventTypes.MessageCreated)).Sequence);
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), received.Order());
    }

    [Fact]
    public async Task Reconnect_RecoversEveryMissedMessageExactlyOnceInOrder()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var conversation = await alice.OpenConversationAsync("bob");
        var cursor = (await (await bob.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;

        for (var i = 1; i <= 7; i++)
        {
            await alice.SendAsync(conversation.Id, $"c{i}", $"while away {i}");
        }

        var recovered = new List<SogaEvent>();
        bool hasMore;
        do
        {
            var page = await (await bob.GetAsync($"{P}/sync?limit=3&cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();
            recovered.AddRange(page.Events);
            cursor = page.Cursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        var messages = recovered.Where(e => e.Type == SogaEventTypes.MessageCreated).Select(MessageOf).ToList();
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"while away {i}"), messages.Select(m => m.Content));
        Assert.Equal(recovered.Count, recovered.Select(e => e.EventId).Distinct().Count());
        var caughtUp = await (await bob.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();
        Assert.Empty(caughtUp.Events);
    }

    [Fact]
    public async Task HostEventSubscriber_ReceivesEveryCommittedMessageFromTheRealPipeline()
    {
        var subscriber = new CollectingSubscriber();
        await using var server = await SogaTestServer.StartAsync(services: s => s.AddSingleton<ISogaEventSubscriber>(subscriber));
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");

        var message = await alice.SendAsync(conversation.Id, "c1", "push me");

        var notification = await subscriber.WaitForAsync(n => n.Event.Type == SogaEventTypes.MessageCreated);
        Assert.Equal(message.Id, MessageOf(notification.Event).Id);
        Assert.Equal(SogaTestServer.DefaultTenant, notification.TenantId);
    }

    [Fact]
    public async Task Presence_ForAConversationTheCallerJustLeft_IsRefused()
    {
        await using var server = await SogaTestServer.StartAsync();
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        await using var hub = server.Hub("bob");
        await hub.StartAsync();
        (await server.Client("bob").RemoveParticipantAsync(group.Id, "bob")).EnsureSuccessStatusCode();

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<IReadOnlyList<PresenceResponse>>("GetPresence", group.Id));

        Assert.Contains(SogaErrorCodes.ConversationNotFound, exception.Message, StringComparison.Ordinal);
    }

    private static async Task ConnectAndSubscribeAsync(HubConnection hub, Guid conversationId)
    {
        await hub.StartAsync();
        await hub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version);
        await hub.InvokeAsync("SubscribeToConversation", conversationId);
    }

    private static MessageResponse MessageOf(SogaEvent evt) =>
        evt.Data.Deserialize<MessageCreatedData>(SogaEventSerializer.Options)!.Message;

    private sealed class CollectingSubscriber : ISogaEventSubscriber
    {
        private readonly System.Threading.Channels.Channel<SogaEventNotification> _received =
            System.Threading.Channels.Channel.CreateUnbounded<SogaEventNotification>();

        public Task HandleAsync(SogaEventNotification notification, CancellationToken cancellationToken)
        {
            _received.Writer.TryWrite(notification);
            return Task.CompletedTask;
        }

        public async Task<SogaEventNotification> WaitForAsync(Func<SogaEventNotification, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var notification = await _received.Reader.ReadAsync(timeout.Token);
                if (match(notification))
                {
                    return notification;
                }
            }
        }
    }
}
