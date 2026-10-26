using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Realtime;

namespace Soga.Tests.Realtime;

public sealed class RealtimeStateTests
{
    private static SogaTypingTracker Typing(TestClock clock, Action<SogaOptions>? configure = null)
    {
        var options = new SogaOptions();
        options.Typing.Timeout = TimeSpan.FromSeconds(6);
        options.Typing.MinimumBroadcastInterval = TimeSpan.FromSeconds(2);
        configure?.Invoke(options);
        return new SogaTypingTracker(clock, Options.Create(options));
    }

    private static TestClock NewClock() => new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));

    // ---- typing ----

    [Fact]
    public void Typing_FirstStartBroadcastsAndRapidRepeatsAreThrottled()
    {
        var clock = NewClock();
        var typing = Typing(clock);
        var conversation = Guid.NewGuid();

        var first = typing.Start("t", conversation, "alice", "c1");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var tooSoon = typing.Start("t", conversation, "alice", "c1");
        clock.Advance(TimeSpan.FromSeconds(2));
        var afterInterval = typing.Start("t", conversation, "alice", "c1");

        Assert.Equal([true, false, true], [first, tooSoon, afterInterval]);
    }

    [Fact]
    public void Typing_ExpiresOnItsOwnUnlessRefreshed()
    {
        var clock = NewClock();
        var typing = Typing(clock);
        var conversation = Guid.NewGuid();
        typing.Start("t", conversation, "alice", "c1");
        typing.Start("t", conversation, "bob", "c2");

        clock.Advance(TimeSpan.FromSeconds(5));
        typing.Start("t", conversation, "bob", "c2");
        clock.Advance(TimeSpan.FromSeconds(2));

        var expired = typing.Expire();

        Assert.Equal([("t", conversation, "alice")], expired);
        Assert.Empty(typing.Expire());
    }

    [Fact]
    public void Typing_StopReportsWhetherAnythingWasTyping()
    {
        var typing = Typing(NewClock());
        var conversation = Guid.NewGuid();
        typing.Start("t", conversation, "alice", "c1");

        Assert.True(typing.Stop("t", conversation, "alice"));
        Assert.False(typing.Stop("t", conversation, "alice"));
    }

    [Fact]
    public void Typing_ClosingAConnectionStopsWhatItDrove()
    {
        var typing = Typing(NewClock());
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        typing.Start("t", one, "alice", "phone");
        typing.Start("t", two, "alice", "phone");
        typing.Start("t", one, "bob", "laptop");

        var stopped = typing.RemoveForConnection("phone");

        Assert.Equal(2, stopped.Count);
        Assert.Contains(("t", one, "alice"), stopped);
        Assert.True(typing.Stop("t", one, "bob"));
    }

    [Fact]
    public void Typing_IsScopedByTenantConversationAndUser()
    {
        var typing = Typing(NewClock());
        var conversation = Guid.NewGuid();
        typing.Start("tenant-a", conversation, "alice", "c1");

        Assert.False(typing.Stop("tenant-b", conversation, "alice"));
        Assert.False(typing.Stop("tenant-a", Guid.NewGuid(), "alice"));
        Assert.False(typing.Stop("tenant-a", conversation, "bob"));
        Assert.True(typing.Stop("tenant-a", conversation, "alice"));
    }

    // ---- connection registry ----

    [Fact]
    public void Registry_TracksEveryConnectionOfAUser()
    {
        var registry = new SogaConnectionRegistry();
        registry.Add("t", "alice", "phone");
        registry.Add("t", "alice", "laptop");
        registry.Add("t", "bob", "tablet");

        Assert.Equal(["laptop", "phone"], registry.GetConnections("t", "alice").Order());
        Assert.Empty(registry.GetConnections("other-tenant", "alice"));
        Assert.Equal(("t", "alice"), registry.Remove("phone"));
        Assert.Equal(["laptop"], registry.GetConnections("t", "alice"));
        Assert.Null(registry.Remove("phone"));
    }

    // ---- presence ----

    private static InMemoryPresenceProvider Presence(TimeSpan grace)
    {
        var options = new SogaOptions();
        options.Presence.OfflineGracePeriod = grace;
        return new InMemoryPresenceProvider(Options.Create(options));
    }

    [Fact]
    public async Task Presence_FirstConnectionMakesTheUserOnlineOnlyOnce()
    {
        var presence = Presence(TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;

        var first = await presence.ConnectionOpenedAsync("t", "alice", "phone", now, default);
        var second = await presence.ConnectionOpenedAsync("t", "alice", "laptop", now, default);

        Assert.Equal([true, false], [first, second]);
        Assert.True((await presence.GetPresenceAsync("t", ["alice"], now, default))[0].Online);
    }

    [Fact]
    public async Task Presence_StaysOnlineUntilTheLastConnectionClosesAndTheGracePeriodPasses()
    {
        var presence = Presence(TimeSpan.FromSeconds(10));
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        await presence.ConnectionOpenedAsync("t", "alice", "phone", now, default);
        await presence.ConnectionOpenedAsync("t", "alice", "laptop", now, default);

        await presence.ConnectionClosedAsync("t", "alice", "phone", now.AddSeconds(1), default);
        Assert.Empty(await presence.CollectExpiredAsync(now.AddMinutes(5), default));

        await presence.ConnectionClosedAsync("t", "alice", "laptop", now.AddSeconds(2), default);
        Assert.Empty(await presence.CollectExpiredAsync(now.AddSeconds(11), default));
        Assert.True((await presence.GetPresenceAsync("t", ["alice"], now, default))[0].Online);

        var expired = await presence.CollectExpiredAsync(now.AddSeconds(13), default);

        var (tenant, offline) = Assert.Single(expired);
        Assert.Equal(("t", "alice", false, now.AddSeconds(2)), (tenant, offline.UserId, offline.Online, offline.LastSeenAt));
        Assert.False((await presence.GetPresenceAsync("t", ["alice"], now, default))[0].Online);
        Assert.Empty(await presence.CollectExpiredAsync(now.AddHours(1), default));
    }

    [Fact]
    public async Task Presence_ReconnectingWithinTheGraceNeverGoesOffline()
    {
        var presence = Presence(TimeSpan.FromSeconds(10));
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        await presence.ConnectionOpenedAsync("t", "alice", "phone", now, default);
        await presence.ConnectionClosedAsync("t", "alice", "phone", now.AddSeconds(1), default);

        var cameBack = await presence.ConnectionOpenedAsync("t", "alice", "phone2", now.AddSeconds(5), default);

        Assert.False(cameBack);
        Assert.Empty(await presence.CollectExpiredAsync(now.AddMinutes(1), default));
        Assert.True((await presence.GetPresenceAsync("t", ["alice"], now, default))[0].Online);
    }

    [Fact]
    public async Task Presence_UnknownUsersAreOfflineAndTenantsAreSeparate()
    {
        var presence = Presence(TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        await presence.ConnectionOpenedAsync("tenant-a", "alice", "c", now, default);

        var report = await presence.GetPresenceAsync("tenant-b", ["alice", "nobody"], now, default);

        Assert.All(report, p => Assert.False(p.Online));
    }
}
