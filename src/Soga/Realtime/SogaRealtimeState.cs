using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Extensibility;

namespace Soga.Realtime;

/// <summary>
/// Which live connections belong to which user, within this process. It exists so that when a participant is
/// removed their connections can be taken out of the conversation's SignalR group immediately, on every device.
/// </summary>
internal sealed class SogaConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Tenant, string User)> _byConnection = [];
    private readonly Dictionary<(string Tenant, string User), HashSet<string>> _byUser = [];
    private readonly Dictionary<string, HashSet<Guid>> _subscriptions = [];

    public void Add(string tenantId, string userId, string connectionId)
    {
        lock (_gate)
        {
            _byConnection[connectionId] = (tenantId, userId);
            if (!_byUser.TryGetValue((tenantId, userId), out var connections))
            {
                connections = [];
                _byUser[(tenantId, userId)] = connections;
            }

            connections.Add(connectionId);
        }
    }

    public (string Tenant, string User)? Remove(string connectionId)
    {
        lock (_gate)
        {
            _subscriptions.Remove(connectionId);
            if (!_byConnection.Remove(connectionId, out var owner))
            {
                return null;
            }

            if (_byUser.TryGetValue(owner, out var connections))
            {
                connections.Remove(connectionId);
                if (connections.Count == 0)
                {
                    _byUser.Remove(owner);
                }
            }

            return owner;
        }
    }

    public void Subscribe(string connectionId, Guid conversationId)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(connectionId, out var set))
            {
                set = [];
                _subscriptions[connectionId] = set;
            }

            set.Add(conversationId);
        }
    }

    public void Unsubscribe(string connectionId, Guid conversationId)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(connectionId, out var set))
            {
                set.Remove(conversationId);
            }
        }
    }

    public bool IsSubscribed(string connectionId, Guid conversationId)
    {
        lock (_gate)
        {
            return _subscriptions.TryGetValue(connectionId, out var set) && set.Contains(conversationId);
        }
    }

    public IReadOnlyList<string> GetConnections(string tenantId, string userId)
    {
        lock (_gate)
        {
            return _byUser.TryGetValue((tenantId, userId), out var connections) ? [.. connections] : [];
        }
    }
}

/// <summary>
/// Ephemeral typing state. It lives only in memory and is never stored, recovered by sync, or given text: it says
/// only "this user is typing here" until it is refreshed, stopped, or expires.
/// </summary>
internal sealed class SogaTypingTracker(TimeProvider clock, IOptions<SogaOptions> options)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, Guid Conversation, string User), Entry> _entries = [];

    /// <summary>Records typing. Returns <see langword="true"/> when the change should be broadcast: a new entry, or a refresh after the minimum interval.</summary>
    public bool Start(string tenantId, Guid conversationId, string userId, string connectionId)
    {
        var settings = options.Value.Typing;
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var key = (tenantId, conversationId, userId);
            if (_entries.TryGetValue(key, out var entry))
            {
                var broadcast = now - entry.LastBroadcastAt >= settings.MinimumBroadcastInterval;
                _entries[key] = entry with
                {
                    ConnectionId = connectionId,
                    ExpiresAt = now + settings.Timeout,
                    LastBroadcastAt = broadcast ? now : entry.LastBroadcastAt,
                };
                return broadcast;
            }

            _entries[key] = new Entry(connectionId, now + settings.Timeout, now);
            return true;
        }
    }

    /// <summary>Stops typing. Returns <see langword="true"/> when the user was typing.</summary>
    public bool Stop(string tenantId, Guid conversationId, string userId)
    {
        lock (_gate)
        {
            return _entries.Remove((tenantId, conversationId, userId));
        }
    }

    /// <summary>Removes and returns everything that has not been refreshed within the timeout.</summary>
    public IReadOnlyList<(string Tenant, Guid Conversation, string User)> Expire()
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var expired = _entries.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList();
            foreach (var key in expired)
            {
                _entries.Remove(key);
            }

            return expired;
        }
    }

    /// <summary>Removes and returns the typing state last driven by a connection that just closed.</summary>
    public IReadOnlyList<(string Tenant, Guid Conversation, string User)> RemoveForConnection(string connectionId)
    {
        lock (_gate)
        {
            var owned = _entries.Where(e => e.Value.ConnectionId == connectionId).Select(e => e.Key).ToList();
            foreach (var key in owned)
            {
                _entries.Remove(key);
            }

            return owned;
        }
    }

    private sealed record Entry(string ConnectionId, DateTimeOffset ExpiresAt, DateTimeOffset LastBroadcastAt);
}

/// <summary>Counts connections inside this process. First connection means online; last connection plus the grace period means offline.</summary>
internal sealed class InMemoryPresenceProvider(IOptions<SogaOptions> options) : ISogaPresenceProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string User), State> _states = [];

    public ValueTask<bool> ConnectionOpenedAsync(string tenantId, string userId, string connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue((tenantId, userId), out var state))
            {
                state = new State();
                _states[(tenantId, userId)] = state;
            }

            state.Connections.Add(connectionId);
            state.OfflineAt = null;
            var becameOnline = !state.Online;
            state.Online = true;
            return ValueTask.FromResult(becameOnline);
        }
    }

    public ValueTask ConnectionClosedAsync(string tenantId, string userId, string connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_states.TryGetValue((tenantId, userId), out var state) && state.Connections.Remove(connectionId) && state.Connections.Count == 0)
            {
                state.LastSeen = now;
                state.OfflineAt = now + options.Value.Presence.OfflineGracePeriod;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SogaPresence>> GetPresenceAsync(
        string tenantId,
        IReadOnlyCollection<string> userIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<SogaPresence> result = [.. userIds.Select(id =>
                _states.TryGetValue((tenantId, id), out var state)
                    ? new SogaPresence(id, state.Online, state.LastSeen)
                    : new SogaPresence(id, false, null))];
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<IReadOnlyList<(string TenantId, SogaPresence Presence)>> CollectExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var expired = new List<(string, SogaPresence)>();
            foreach (var ((tenant, user), state) in _states)
            {
                if (state.Online && state.Connections.Count == 0 && state.OfflineAt is { } at && at <= now)
                {
                    state.Online = false;
                    state.OfflineAt = null;
                    expired.Add((tenant, new SogaPresence(user, false, state.LastSeen)));
                }
            }

            IReadOnlyList<(string TenantId, SogaPresence Presence)> result = expired;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class State
    {
        public HashSet<string> Connections { get; } = [];
        public bool Online { get; set; }
        public DateTimeOffset? LastSeen { get; set; }
        public DateTimeOffset? OfflineAt { get; set; }
    }
}
