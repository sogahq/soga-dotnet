using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soga.Contracts;
using Soga.Errors;
using Soga.Realtime;
using Soga.Synchronization;

namespace Soga.IntegrationTests;

public sealed class RealtimeTests
{
    private const string P = SogaTestServer.Prefix;

    private static MessageResponse MessageOf(SogaEvent evt) => SogaEventSerializer.ReadData<MessageCreatedData>(evt).Message;

    [Fact]
    public async Task ReleaseProof_ConnectSendDisconnectMissReconnectSyncAndMarkRead()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");

        // 1. Bob takes a sync cursor first, then loads his state over HTTP.
        var cursor = (await (await bob.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;

        // 2. Alice opens the conversation; Bob connects and subscribes.
        var conversation = await alice.OpenConversationAsync("bob");
        await using var bobHub = server.Hub("bob");
        var bobInbox = new EventInbox(bobHub);
        await bobHub.StartAsync();
        Assert.Equal(SogaProtocol.Version, await bobHub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version));
        await bobHub.InvokeAsync("SubscribeToConversation", conversation.Id);

        // 3. Alice sends; the message is stored first and then arrives in realtime, once, through the outbox.
        var first = await alice.SendAsync(conversation.Id, "c1", "one");
        var live = await bobInbox.NextAsync(SogaEventTypes.MessageCreated);
        Assert.Equal(first.Id, MessageOf(live).Id);
        Assert.Equal((1L, "alice", "one"), (live.Sequence, MessageOf(live).SenderId, MessageOf(live).Content));
        Assert.Equal(SogaProtocol.Version, live.ProtocolVersion);

        // 4. Bob goes offline. Alice keeps sending.
        await bobHub.StopAsync();
        var second = await alice.SendAsync(conversation.Id, "c2", "two while offline");

        // 5. Bob reconnects, restores his subscription, and synchronizes from his cursor.
        await using var bobAgain = server.Hub("bob");
        var againInbox = new EventInbox(bobAgain);
        await bobAgain.StartAsync();
        await bobAgain.InvokeAsync("SubscribeToConversation", conversation.Id);
        var sync = await (await bob.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();

        // Sync replays the message he already saw live (same event ID, so he deduplicates) and the one he missed.
        var syncedMessageEvents = sync.Events.Where(e => e.Type == SogaEventTypes.MessageCreated).ToList();
        Assert.Contains(syncedMessageEvents, e => e.EventId == live.EventId);
        var missed = syncedMessageEvents.Where(e => e.EventId != live.EventId).Select(MessageOf).Single();
        Assert.Equal((second.Id, "two while offline"), (missed.Id, missed.Content));

        // 6. Live delivery resumes after reconnect.
        var third = await alice.SendAsync(conversation.Id, "c3", "three");
        Assert.Equal(third.Id, MessageOf(await againInbox.NextAsync(SogaEventTypes.MessageCreated)).Id);

        // 7. Bob marks the conversation read; unread drops to zero and both devices are told.
        var read = await bob.PostAsJsonAsync($"{P}/conversations/{conversation.Id}/read", new MarkReadRequest(third.Id));
        Assert.Equal(0, (await read.ReadAsync<ReadStateResponse>()).UnreadCount);
        var readEvent = await againInbox.NextAsync(SogaEventTypes.ReadPositionChanged);
        Assert.Equal(new ReadPositionChangedData("bob", 3), SogaEventSerializer.ReadData<ReadPositionChangedData>(readEvent));
        Assert.Equal(0, (await (await bob.GetAsync($"{P}/unread")).ReadAsync<UnreadResponse>()).TotalUnread);
    }

    [Fact]
    public async Task ConversationCreated_ReachesTheRecipientWithoutASubscription()
    {
        await using var server = await SogaTestServer.StartAsync();
        await using var bobHub = server.Hub("bob");
        var inbox = new EventInbox(bobHub);
        await bobHub.StartAsync();
        await bobHub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version);

        var conversation = await server.Client("alice").OpenConversationAsync("bob");

        var evt = await inbox.NextAsync(SogaEventTypes.ConversationCreated);
        Assert.Equal(conversation.Id, evt.ConversationId);
        Assert.Equal(["alice", "bob"], SogaEventSerializer.ReadData<ConversationCreatedData>(evt).ParticipantUserIds);
    }

    [Fact]
    public async Task EveryConnectionOfTheSameUser_ReceivesTheEvent()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        await using var phone = server.Hub("bob");
        await using var laptop = server.Hub("bob");
        var phoneInbox = new EventInbox(phone);
        var laptopInbox = new EventInbox(laptop);
        foreach (var hub in new[] { phone, laptop })
        {
            await hub.StartAsync();
            await hub.InvokeAsync("SubscribeToConversation", conversation.Id);
        }

        var message = await alice.SendAsync(conversation.Id, "c1", "hi both");

        Assert.Equal(message.Id, MessageOf(await phoneInbox.NextAsync(SogaEventTypes.MessageCreated)).Id);
        Assert.Equal(message.Id, MessageOf(await laptopInbox.NextAsync(SogaEventTypes.MessageCreated)).Id);
    }

    [Fact]
    public async Task Subscription_ByANonParticipant_IsRejectedAndReceivesNothing()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var conversation = await alice.OpenConversationAsync("bob");
        await using var carolHub = server.Hub("carol");
        var inbox = new EventInbox(carolHub);
        await carolHub.StartAsync();

        var real = await Assert.ThrowsAsync<HubException>(() => carolHub.InvokeAsync("SubscribeToConversation", conversation.Id));
        var unknown = await Assert.ThrowsAsync<HubException>(() => carolHub.InvokeAsync("SubscribeToConversation", Guid.NewGuid()));
        await alice.SendAsync(conversation.Id, "c1", "private");

        Assert.Contains(SogaErrorCodes.ConversationNotFound, real.Message, StringComparison.Ordinal);
        Assert.Equal(unknown.Message, real.Message);
        Assert.True(await inbox.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Hub_WithoutAuthentication_CannotConnect()
    {
        await using var server = await SogaTestServer.StartAsync();
        await using var anonymous = server.Hub(user: null);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => anonymous.StartAsync());
    }

    [Fact]
    public async Task NegotiateProtocol_RejectsUnsupportedVersions()
    {
        await using var server = await SogaTestServer.StartAsync();
        await using var hub = server.Hub("alice");
        await hub.StartAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<int>("NegotiateProtocol", 99));

        Assert.Contains(SogaErrorCodes.ProtocolMismatch, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_ArePublishedOnlyByTheOutbox_AndRemainRecoverableWhenPublishingIsDown()
    {
        // With the dispatcher disabled, a committed message is durable but not yet announced: this is the
        // "realtime publication failed" situation. Nothing is lost, and the outbox delivers later.
        await using var server = await SogaTestServer.StartAsync(o => o.Outbox.DispatcherEnabled = false);
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var cursor = (await (await bob.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;
        var conversation = await alice.OpenConversationAsync("bob");
        await using var bobHub = server.Hub("bob");
        var inbox = new EventInbox(bobHub);
        await bobHub.StartAsync();
        await bobHub.InvokeAsync("SubscribeToConversation", conversation.Id);

        var message = await alice.SendAsync(conversation.Id, "c1", "stored first");

        Assert.True(await inbox.StaysQuietAsync(TimeSpan.FromMilliseconds(400)), "Nothing may be broadcast before the outbox runs.");
        var recovered = await (await bob.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();
        Assert.Contains(recovered.Events, e => e.Type == SogaEventTypes.MessageCreated && MessageOf(e).Id == message.Id);

        var dispatcher = server.Services.GetServices<IHostedService>().OfType<SogaOutboxDispatcher>().Single();
        Assert.True(await dispatcher.DispatchOnceAsync(default) >= 1);

        var evt = await inbox.NextAsync(SogaEventTypes.MessageCreated);
        Assert.Equal(message.Id, MessageOf(evt).Id);
        Assert.Contains(recovered.Events, e => e.EventId == evt.EventId);
    }

    [Fact]
    public async Task Hub_ScopesGroupsByTenant()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddScoped<Soga.Tenancy.ISogaTenantContext, HeaderTenantContext>());
        var conversation = await server.Client("alice", "acme").OpenConversationAsync("bob");
        await using var bobInAcme = server.Hub("bob", "acme");
        await using var bobInGlobex = server.Hub("bob", "globex");
        var acmeInbox = new EventInbox(bobInAcme);
        var globexInbox = new EventInbox(bobInGlobex);
        await bobInAcme.StartAsync();
        await bobInGlobex.StartAsync();
        await bobInAcme.InvokeAsync("SubscribeToConversation", conversation.Id);
        await Assert.ThrowsAsync<HubException>(() => bobInGlobex.InvokeAsync("SubscribeToConversation", conversation.Id));

        await server.Client("alice", "acme").SendAsync(conversation.Id, "c1", "acme only");

        Assert.Equal("acme only", MessageOf(await acmeInbox.NextAsync(SogaEventTypes.MessageCreated)).Content);
        Assert.True(await globexInbox.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
    }
}
