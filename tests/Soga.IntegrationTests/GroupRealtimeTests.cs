using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Soga.Authorization;
using Soga.Contracts;
using Soga.Errors;
using Soga.Synchronization;

namespace Soga.IntegrationTests;

public sealed class GroupRealtimeTests
{
    private const string P = SogaTestServer.Prefix;

    private static T Data<T>(SogaEvent evt) => SogaEventSerializer.ReadData<T>(evt);

    private static async Task<ParticipantChangedData> NextChangeAsync(EventInbox inbox, string change)
    {
        while (true)
        {
            var data = Data<ParticipantChangedData>(await inbox.NextAsync(SogaEventTypes.ParticipantChanged));
            if (data.Change == change)
            {
                return data;
            }
        }
    }

    private static async Task<(HubConnection Hub, EventInbox Inbox)> ConnectAsync(SogaTestServer server, string user, params Guid[] subscribeTo)
    {
        var hub = server.Hub(user);
        var inbox = new EventInbox(hub);
        await hub.StartAsync();
        await hub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version); // returns only after the server finished setting up the connection
        foreach (var id in subscribeTo)
        {
            await hub.InvokeAsync("SubscribeToConversation", id);
        }

        return (hub, inbox);
    }

    [Fact]
    public async Task MembershipEvents_ReachTheGroupAndTheAffectedUserInRealtime()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _ = bobHub;
        await using var daveHub = server.Hub("dave");
        var dave = new EventInbox(daveHub);
        await daveHub.StartAsync();
        await daveHub.InvokeAsync<int>("NegotiateProtocol", SogaProtocol.Version);

        await alice.AddParticipantAsync(group.Id, "dave", "admin");
        await alice.ChangeRoleAsync(group.Id, "bob", "admin");
        await alice.PatchAsync($"{P}/conversations/{group.Id}", JsonContent.Create(new UpdateConversationRequest("Renamed")));
        await alice.PostAsync($"{P}/conversations/{group.Id}/archive", null);

        // The group hears each change; dave, who is not subscribed to the conversation yet, hears about himself.
        Assert.Equal(new ParticipantChangedData("dave", "added", "admin", "alice"), Data<ParticipantChangedData>(await bob.NextAsync(SogaEventTypes.ParticipantChanged)));
        Assert.Equal("role_changed", Data<ParticipantChangedData>(await bob.NextAsync(SogaEventTypes.ParticipantChanged)).Change);
        Assert.Equal(("Renamed", false), (Data<ConversationUpdatedData>(await bob.NextAsync(SogaEventTypes.ConversationUpdated)).Title, false));
        Assert.True(Data<ConversationArchivedData>(await bob.NextAsync(SogaEventTypes.ConversationArchived)).Archived);
        Assert.Equal(new ParticipantChangedData("dave", "added", "admin", "alice"), Data<ParticipantChangedData>(await dave.NextAsync(SogaEventTypes.ParticipantChanged)));
    }

    [Fact]
    public async Task RemovedParticipant_StopsReceivingOnEveryDeviceWhileStillConnected()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob", "carol");
        var (phoneHub, phone) = await ConnectAsync(server, "bob", group.Id);
        var (laptopHub, laptop) = await ConnectAsync(server, "bob", group.Id);
        var (carolHub, carol) = await ConnectAsync(server, "carol", group.Id);
        await using var _1 = phoneHub;
        await using var _2 = laptopHub;
        await using var _3 = carolHub;

        var before = await alice.SendAsync(group.Id, "c1", "hello everyone");
        Assert.Equal(before.Id, Data<MessageCreatedData>(await phone.NextAsync(SogaEventTypes.MessageCreated)).Message.Id);
        Assert.Equal(before.Id, Data<MessageCreatedData>(await laptop.NextAsync(SogaEventTypes.MessageCreated)).Message.Id);
        await carol.NextAsync(SogaEventTypes.MessageCreated);

        await alice.RemoveParticipantAsync(group.Id, "bob");

        // Both of Bob's devices are told, then cut off from the conversation.
        foreach (var device in new[] { phone, laptop })
        {
            var removal = Data<ParticipantChangedData>(await device.NextAsync(SogaEventTypes.ParticipantChanged));
            Assert.Equal(("bob", "removed"), (removal.UserId, removal.Change));
        }

        await Task.Delay(300);
        var secret = await alice.SendAsync(group.Id, "c2", "for members only");
        var carolGets = Data<MessageCreatedData>(await carol.NextAsync(SogaEventTypes.MessageCreated)).Message;

        Assert.Equal(secret.Id, carolGets.Id);
        Assert.True(await phone.StaysQuietAsync(TimeSpan.FromMilliseconds(600), SogaEventTypes.MessageCreated), "The phone still received group traffic after removal.");
        Assert.True(await phone.StaysQuietAsync(TimeSpan.FromMilliseconds(50), SogaEventTypes.ParticipantChanged), "The same membership event was delivered twice.");
        Assert.True(await laptop.StaysQuietAsync(TimeSpan.FromMilliseconds(600), SogaEventTypes.MessageCreated), "The laptop still received group traffic after removal.");
        var resubscribe = await Assert.ThrowsAsync<HubException>(() => phoneHub.InvokeAsync("SubscribeToConversation", group.Id));
        Assert.Contains(SogaErrorCodes.ConversationNotFound, resubscribe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParticipantWhoLeaves_IsCutOffTheSameWay()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _ = bobHub;

        await server.Client("bob").RemoveParticipantAsync(group.Id, "bob");
        Assert.Equal("left", Data<ParticipantChangedData>(await bob.NextAsync(SogaEventTypes.ParticipantChanged)).Change);
        await Task.Delay(300);
        await alice.SendAsync(group.Id, "c1", "after you left");

        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(600), SogaEventTypes.MessageCreated));
    }

    [Fact]
    public async Task EditAndDelete_ArePushedInRealtimeAndTheSameEventsAreInSync()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bobHttp = server.Client("bob");
        var group = await alice.CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _ = bobHub;
        var cursor = (await (await bobHttp.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;
        var message = await alice.SendAsync(group.Id, "c1", "helo");
        await bob.NextAsync(SogaEventTypes.MessageCreated);

        await alice.PatchAsync($"{P}/conversations/{group.Id}/messages/{message.Id}", JsonContent.Create(new EditMessageRequest("hello")));
        await alice.DeleteAsync($"{P}/conversations/{group.Id}/messages/{message.Id}");

        var updated = await bob.NextAsync(SogaEventTypes.MessageUpdated);
        var deleted = await bob.NextAsync(SogaEventTypes.MessageDeleted);
        Assert.Equal(("hello", message.Sequence), (Data<MessageCreatedData>(updated).Message.Content, updated.Sequence));
        Assert.Equal(message.Id, Data<MessageDeletedData>(deleted).MessageId);

        var sync = await (await bobHttp.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();
        Assert.Contains(sync.Events, e => e.EventId == updated.EventId);
        Assert.Contains(sync.Events, e => e.EventId == deleted.EventId);
    }

    // ---- typing ----

    [Fact]
    public async Task Typing_ReachesOthersOnlyIsThrottledCarriesNoTextAndExpires()
    {
        await using var server = await SogaTestServer.StartAsync(o =>
        {
            o.Typing.Timeout = TimeSpan.FromMilliseconds(1200);
            o.Typing.MinimumBroadcastInterval = TimeSpan.FromMilliseconds(400);
        });
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var (aliceHub, alice) = await ConnectAsync(server, "alice", group.Id);
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _1 = aliceHub;
        await using var _2 = bobHub;

        for (var i = 0; i < 5; i++)
        {
            await aliceHub.InvokeAsync("SetTyping", group.Id, true);
        }

        var started = await bob.NextAsync(SogaEventTypes.TypingChanged);
        Assert.Equal(new TypingChangedData("alice", true), Data<TypingChangedData>(started));
        Assert.Equal(["userId", "typing"], started.Data.EnumerateObject().Select(p => p.Name).ToList());
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(250), SogaEventTypes.TypingChanged), "Rapid repeats must be throttled to one broadcast.");
        Assert.True(await alice.StaysQuietAsync(TimeSpan.FromMilliseconds(100), SogaEventTypes.TypingChanged), "The typist must not hear their own typing.");

        var stopped = await bob.NextAsync(SogaEventTypes.TypingChanged);
        Assert.Equal(new TypingChangedData("alice", false), Data<TypingChangedData>(stopped));
    }

    [Fact]
    public async Task Typing_StopsWhenTheTypistDisconnectsOrStopsExplicitly()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Typing.Timeout = TimeSpan.FromSeconds(30));
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var (aliceHub, _) = await ConnectAsync(server, "alice", group.Id);
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _2 = bobHub;

        await aliceHub.InvokeAsync("SetTyping", group.Id, true);
        Assert.True(Data<TypingChangedData>(await bob.NextAsync(SogaEventTypes.TypingChanged)).Typing);
        await aliceHub.InvokeAsync("SetTyping", group.Id, false);
        Assert.False(Data<TypingChangedData>(await bob.NextAsync(SogaEventTypes.TypingChanged)).Typing);

        await aliceHub.InvokeAsync("SetTyping", group.Id, true);
        Assert.True(Data<TypingChangedData>(await bob.NextAsync(SogaEventTypes.TypingChanged)).Typing);
        await aliceHub.DisposeAsync();
        Assert.False(Data<TypingChangedData>(await bob.NextAsync(SogaEventTypes.TypingChanged)).Typing);
    }

    [Fact]
    public async Task Typing_IsRefusedForNonMembersAndDisabledByConfiguration()
    {
        await using var server = await SogaTestServer.StartAsync();
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var (outsiderHub, _) = await ConnectAsync(server, "mallory");
        await using var _ = outsiderHub;

        var refused = await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync("SetTyping", group.Id, true));
        Assert.Contains(SogaErrorCodes.ConversationNotFound, refused.Message, StringComparison.Ordinal);

        await using var disabled = await SogaTestServer.StartAsync(o => o.Typing.Enabled = false);
        var quietGroup = await disabled.Client("alice").CreateGroupAsync("Team", "bob");
        var (a, _2) = await ConnectAsync(disabled, "alice", quietGroup.Id);
        var (b, bob) = await ConnectAsync(disabled, "bob", quietGroup.Id);
        await using var _3 = a;
        await using var _4 = b;
        await a.InvokeAsync("SetTyping", quietGroup.Id, true);
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Typing_IsNeverStoredOrRecoveredBySync()
    {
        await using var server = await SogaTestServer.StartAsync();
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var cursor = (await (await server.Client("bob").GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;
        var (aliceHub, _) = await ConnectAsync(server, "alice", group.Id);
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _1 = aliceHub;
        await using var _2 = bobHub;

        await aliceHub.InvokeAsync("SetTyping", group.Id, true);
        await bob.NextAsync(SogaEventTypes.TypingChanged);

        var sync = await (await server.Client("bob").GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(cursor)}")).ReadAsync<SyncResponse>();
        Assert.DoesNotContain(sync.Events, e => e.Type is SogaEventTypes.TypingChanged or SogaEventTypes.PresenceChanged);
    }

    // ---- presence ----

    [Fact]
    public async Task Presence_GoesOnlineOnFirstConnectionAndOfflineOnlyAfterTheLastPlusGrace()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Presence.OfflineGracePeriod = TimeSpan.FromMilliseconds(500));
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _ = bobHub;

        var (phone, _1) = await ConnectAsync(server, "alice");
        var online = Data<PresenceChangedData>(await bob.NextAsync(SogaEventTypes.PresenceChanged));
        Assert.Equal(("alice", true, null), (online.UserId, online.Online, online.LastSeenAt));

        var present = await bobHub.InvokeAsync<PresenceResponse[]>("GetPresence", group.Id);
        Assert.True(present.Single(p => p.UserId == "alice").Online);

        // A second device causes no new "online" broadcast, and closing one device keeps her online.
        var (laptop, _2) = await ConnectAsync(server, "alice");
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(400)));
        await phone.DisposeAsync();
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(1200)));
        Assert.True((await bobHub.InvokeAsync<PresenceResponse[]>("GetPresence", group.Id)).Single(p => p.UserId == "alice").Online);

        await laptop.DisposeAsync();
        var offline = Data<PresenceChangedData>(await bob.NextAsync(SogaEventTypes.PresenceChanged));
        Assert.Equal(("alice", false, null), (offline.UserId, offline.Online, offline.LastSeenAt));
        Assert.False((await bobHub.InvokeAsync<PresenceResponse[]>("GetPresence", group.Id)).Single(p => p.UserId == "alice").Online);
    }

    [Fact]
    public async Task Presence_ReconnectingWithinTheGraceNeverShowsOffline()
    {
        await using var server = await SogaTestServer.StartAsync(o => o.Presence.OfflineGracePeriod = TimeSpan.FromSeconds(3));
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
        await using var _ = bobHub;
        var (first, _1) = await ConnectAsync(server, "alice");
        await bob.NextAsync(SogaEventTypes.PresenceChanged);

        await first.DisposeAsync();
        var (second, _2) = await ConnectAsync(server, "alice");
        await using var _3 = second;

        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(1500)), "A flaky reconnect must not flap the presence indicator.");
    }

    [Fact]
    public async Task Presence_LastSeenIsSharedOnlyWhenTheHostAllowsIt()
    {
        foreach (var expose in new[] { false, true })
        {
            await using var server = await SogaTestServer.StartAsync(o =>
            {
                o.Presence.ExposeLastSeen = expose;
                o.Presence.OfflineGracePeriod = TimeSpan.FromMilliseconds(200);
            });
            var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
            var (bobHub, bob) = await ConnectAsync(server, "bob", group.Id);
            await using var _ = bobHub;
            var (aliceHub, _1) = await ConnectAsync(server, "alice");
            await bob.NextAsync(SogaEventTypes.PresenceChanged);
            await aliceHub.DisposeAsync();

            var offline = Data<PresenceChangedData>(await bob.NextAsync(SogaEventTypes.PresenceChanged));
            var report = (await bobHub.InvokeAsync<PresenceResponse[]>("GetPresence", group.Id)).Single(p => p.UserId == "alice");

            Assert.Equal(expose, offline.LastSeenAt is not null);
            Assert.Equal(expose, report.LastSeenAt is not null);
        }
    }

    [Fact]
    public async Task Presence_IsAdvisoryOnly_NonMembersCannotReadItAndDisablingItSilencesIt()
    {
        await using var server = await SogaTestServer.StartAsync();
        var group = await server.Client("alice").CreateGroupAsync("Team", "bob");
        var (outsiderHub, _) = await ConnectAsync(server, "mallory");
        await using var _1 = outsiderHub;

        await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync<PresenceResponse[]>("GetPresence", group.Id));

        await using var off = await SogaTestServer.StartAsync(o => o.Presence.Enabled = false);
        var quiet = await off.Client("alice").CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(off, "bob", quiet.Id);
        await using var _2 = bobHub;
        var (aliceHub, _3) = await ConnectAsync(off, "alice");
        await using var _4 = aliceHub;
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Empty(await bobHub.InvokeAsync<PresenceResponse[]>("GetPresence", quiet.Id));
    }

    // ---- subscriptions and policy ----

    [Fact]
    public async Task SubscribeToAll_AfterAReconnect_RestoresOnlyWhatIsStillAuthorized()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var kept = await alice.CreateGroupAsync("Kept", "bob");
        var lost = await alice.CreateGroupAsync("Lost", "bob");
        var direct = await alice.OpenConversationAsync("bob");
        await alice.RemoveParticipantAsync(lost.Id, "bob");

        var (bobHub, bob) = await ConnectAsync(server, "bob");
        await using var _ = bobHub;
        var restored = await bobHub.InvokeAsync<Guid[]>("SubscribeToAll");
        await alice.SendAsync(lost.Id, "c1", "not for bob");
        await alice.SendAsync(kept.Id, "c2", "for bob");

        Assert.Equal(new[] { kept.Id, direct.Id }.Order(), restored.Order());
        Assert.Equal("for bob", Data<MessageCreatedData>(await bob.NextAsync(SogaEventTypes.MessageCreated)).Message.Content);
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task HostPolicy_AppliesToRealtimeSubscriptionsLikeHttp()
    {
        await using var server = await SogaTestServer.StartAsync(
            services: s => s.AddSingleton<ISogaAuthorizationPolicy, NoSubscribeForBob>());
        var alice = server.Client("alice");
        var group = await alice.CreateGroupAsync("Team", "bob");
        var (bobHub, bob) = await ConnectAsync(server, "bob");
        await using var _ = bobHub;

        var denied = await Assert.ThrowsAsync<HubException>(() => bobHub.InvokeAsync("SubscribeToConversation", group.Id));
        var restored = await bobHub.InvokeAsync<Guid[]>("SubscribeToAll");
        await alice.SendAsync(group.Id, "c1", "hello");

        Assert.Contains(SogaErrorCodes.AuthorizationDenied, denied.Message, StringComparison.Ordinal);
        Assert.Empty(restored);
        Assert.True(await bob.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
    }

    // ---- the V2 proof ----

    [Fact]
    public async Task V2Proof_GroupRolesMessagesLifecycleDevicesMembershipReconnectAndSync()
    {
        await using var server = await SogaTestServer.StartAsync();
        var alice = server.Client("alice");
        var bob = server.Client("bob");
        var carol = server.Client("carol");

        // Create a group and assign roles.
        var group = await alice.CreateGroupAsync("Launch", "bob", "carol");
        await alice.ChangeRoleAsync(group.Id, "bob", "admin");
        var carolCursor = (await (await carol.GetAsync($"{P}/sync")).ReadAsync<SyncResponse>()).Cursor;

        // Bob has two devices connected and subscribed; Carol has one.
        var (bobPhoneHub, bobPhone) = await ConnectAsync(server, "bob", group.Id);
        var (bobLaptopHub, bobLaptop) = await ConnectAsync(server, "bob", group.Id);
        var (carolHub, carolLive) = await ConnectAsync(server, "carol", group.Id);
        await using var _1 = bobPhoneHub;
        await using var _2 = bobLaptopHub;

        // Exchange ordered messages, reply, edit and delete under policy.
        var first = await alice.SendAsync(group.Id, "a1", "Kickoff at nine");
        var reply = await (await bob.PostAsJsonAsync($"{P}/conversations/{group.Id}/messages", new SendMessageRequest("b1", "Works for me", first.Id))).ReadAsync<MessageResponse>();
        Assert.Equal(first.Id, reply.ReplyTo!.MessageId);
        Assert.Equal([1L, 2L], [first.Sequence, reply.Sequence]);
        await alice.PatchAsync($"{P}/conversations/{group.Id}/messages/{first.Id}", JsonContent.Create(new EditMessageRequest("Kickoff at ten")));
        Assert.Equal("Kickoff at ten", Data<MessageCreatedData>(await bobPhone.NextAsync(SogaEventTypes.MessageUpdated)).Message.Content);
        Assert.Equal("Kickoff at ten", Data<MessageCreatedData>(await bobLaptop.NextAsync(SogaEventTypes.MessageUpdated)).Message.Content);

        // Typing is live and ephemeral.
        await carolHub.InvokeAsync("SetTyping", group.Id, true);
        Assert.Equal(new TypingChangedData("carol", true), Data<TypingChangedData>(await bobPhone.NextAsync(SogaEventTypes.TypingChanged)));

        // Carol disconnects. While she is away: a message, a deletion, and a membership change.
        await carolHub.DisposeAsync();
        var missed = await alice.SendAsync(group.Id, "a2", "Agenda attached");
        await bob.DeleteAsync($"{P}/conversations/{group.Id}/messages/{reply.Id}");
        await alice.AddParticipantAsync(group.Id, "dave");
        await alice.RemoveParticipantAsync(group.Id, "bob");

        // Both of Bob's devices are told and cut off.
        await NextChangeAsync(bobPhone, "removed");
        await NextChangeAsync(bobLaptop, "removed");

        // Carol reconnects, restores only authorized subscriptions, and recovers every durable change in order.
        var (carolAgainHub, carolAgain) = await ConnectAsync(server, "carol");
        await using var _3 = carolAgainHub;
        Assert.Equal([group.Id], await carolAgainHub.InvokeAsync<Guid[]>("SubscribeToAll"));
        var sync = await (await carol.GetAsync($"{P}/sync?cursor={Uri.EscapeDataString(carolCursor)}")).ReadAsync<SyncResponse>();

        var recovered = sync.Events.Select(e => e.Type).ToList();
        Assert.Contains(SogaEventTypes.MessageUpdated, recovered);
        Assert.Contains(sync.Events, e => e.Type == SogaEventTypes.MessageCreated && Data<MessageCreatedData>(e).Message.Id == missed.Id);
        Assert.Contains(sync.Events, e => e.Type == SogaEventTypes.MessageDeleted && Data<MessageDeletedData>(e).MessageId == reply.Id);
        Assert.Equal(
            ["added", "removed"],
            sync.Events.Where(e => e.Type == SogaEventTypes.ParticipantChanged).Select(e => Data<ParticipantChangedData>(e).Change).Where(c => c is "added" or "removed"));
        Assert.DoesNotContain(sync.Events, e => e.Type == SogaEventTypes.TypingChanged);

        // Read state is correct after all of that, and live delivery resumes for the remaining members.
        var history = await (await carol.GetAsync($"{P}/conversations/{group.Id}/messages")).ReadAsync<MessagePageResponse>();
        var read = await carol.PostAsJsonAsync($"{P}/conversations/{group.Id}/read", new MarkReadRequest(history.Items[0].Id));
        Assert.Equal(0, (await read.ReadAsync<ReadStateResponse>()).UnreadCount);
        var live = await alice.SendAsync(group.Id, "a3", "Welcome dave");
        Assert.Equal(live.Id, Data<MessageCreatedData>(await carolAgain.NextAsync(SogaEventTypes.MessageCreated)).Message.Id);

        // Bob, removed, sees nothing of it: not over HTTP, not live.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await bob.GetAsync($"{P}/conversations/{group.Id}")).StatusCode);
        Assert.True(await bobPhone.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
        Assert.True(await bobLaptop.StaysQuietAsync(TimeSpan.FromMilliseconds(500)));
        _ = carolLive;
    }

    private sealed class NoSubscribeForBob : ISogaAuthorizationPolicy
    {
        public ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(!(context.Action == SogaAction.Subscribe && context.UserId == "bob"));
    }
}
