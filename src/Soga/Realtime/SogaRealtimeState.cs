using Microsoft.Extensions.Options;
using Soga.Configuration;
using Soga.Extensibility;

namespace Soga.Realtime;

/// <summary>
/// Tracks which live connections in this process belong to which user, and which conversations each is subscribed to.
/// </summary>
/// <remarks>
/// The registry lets Soga take a removed participant's connections out of the conversation group immediately, on every
/// device. It only knows about connections to this server instance. All members are thread-safe.
/// </remarks>
internal sealed class SogaConnectionRegistry
{
    /// <summary>
    /// The lock that guards every collection in the registry.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// The tenant and user of each connection, keyed by connection ID.
    /// </summary>
    private readonly Dictionary<string, (string Tenant, string User)> _byConnection = [];

    /// <summary>
    /// The connection IDs of each user, keyed by tenant and user.
    /// </summary>
    private readonly Dictionary<(string Tenant, string User), HashSet<string>> _byUser = [];

    /// <summary>
    /// The conversations each connection is subscribed to, keyed by connection ID.
    /// </summary>
    private readonly Dictionary<string, HashSet<Guid>> _subscriptions = [];

    /// <summary>
    /// Registers a new connection.
    /// </summary>
    /// <param name="tenantId">The tenant of the user.</param>
    /// <param name="userId">The user who owns the connection.</param>
    /// <param name="connectionId">The SignalR connection ID.</param>
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

    /// <summary>
    /// Forgets a closed connection and its subscriptions.
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID.</param>
    /// <returns>The tenant and user that owned the connection, or <see langword="null"/> if it was not registered.</returns>
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

    /// <summary>
    /// Records that a connection subscribed to a conversation.
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
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

    /// <summary>
    /// Records that a connection unsubscribed from a conversation.
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
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

    /// <summary>
    /// Determines whether a connection is subscribed to a conversation.
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns><see langword="true"/> if the connection is subscribed; otherwise, <see langword="false"/>.</returns>
    public bool IsSubscribed(string connectionId, Guid conversationId)
    {
        lock (_gate)
        {
            return _subscriptions.TryGetValue(connectionId, out var set) && set.Contains(conversationId);
        }
    }

    /// <summary>
    /// Gets a user's live connections in this process.
    /// </summary>
    /// <param name="tenantId">The tenant of the user.</param>
    /// <param name="userId">The user.</param>
    /// <returns>A snapshot of the user's connection IDs, which is empty if they have none.</returns>
    public IReadOnlyList<string> GetConnections(string tenantId, string userId)
    {
        lock (_gate)
        {
            return _byUser.TryGetValue((tenantId, userId), out var connections) ? [.. connections] : [];
        }
    }
}

/// <summary>
/// Tracks who is typing in which conversation.
/// </summary>
/// <param name="clock">The clock used for throttling and expiry.</param>
/// <param name="options">The Soga options that hold the typing timeout and broadcast interval.</param>
/// <remarks>
/// Typing state lives only in memory. It is never stored, never recovered by sync, and never carries text: it only says
/// "this user is typing here" until it is refreshed, stopped, or expires. All members are thread-safe.
/// </remarks>
internal sealed class SogaTypingTracker(TimeProvider clock, IOptions<SogaOptions> options)
{
    /// <summary>
    /// The lock that guards <see cref="_entries"/>.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// The active typing indicators, keyed by tenant, conversation, and user.
    /// </summary>
    private readonly Dictionary<(string Tenant, Guid Conversation, string User), Entry> _entries = [];

    /// <summary>
    /// Records that a user is typing, or refreshes an existing indicator.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="userId">The user who is typing.</param>
    /// <param name="connectionId">The connection that reported it, so the indicator stops if that connection closes.</param>
    /// <returns>
    /// <see langword="true"/> if the change should be broadcast: a new indicator, or a refresh after
    /// <see cref="SogaTypingOptions.MinimumBroadcastInterval"/>; otherwise, <see langword="false"/>.
    /// </returns>
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

    /// <summary>
    /// Records that a user stopped typing.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <param name="userId">The user.</param>
    /// <returns><see langword="true"/> if the user was typing and the change should be broadcast; otherwise, <see langword="false"/>.</returns>
    public bool Stop(string tenantId, Guid conversationId, string userId)
    {
        lock (_gate)
        {
            return _entries.Remove((tenantId, conversationId, userId));
        }
    }

    /// <summary>
    /// Removes every indicator that has not been refreshed within the timeout.
    /// </summary>
    /// <returns>The expired indicators, which should be broadcast as stopped.</returns>
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

    /// <summary>
    /// Removes every indicator last reported by a connection that has just closed.
    /// </summary>
    /// <param name="connectionId">The closed connection.</param>
    /// <returns>The removed indicators, which should be broadcast as stopped.</returns>
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

    /// <summary>
    /// Represents one active typing indicator.
    /// </summary>
    /// <param name="ConnectionId">The connection that last reported typing.</param>
    /// <param name="ExpiresAt">When the indicator expires unless refreshed.</param>
    /// <param name="LastBroadcastAt">When the indicator was last broadcast, used for throttling.</param>
    private sealed record Entry(string ConnectionId, DateTimeOffset ExpiresAt, DateTimeOffset LastBroadcastAt);
}

/// <summary>
/// The default <see cref="ISogaPresenceProvider"/>, which counts connections inside this process.
/// </summary>
/// <param name="options">The Soga options that hold the offline grace period.</param>
/// <remarks>
/// A user's first connection brings them online. When their last connection closes they stay online for
/// <see cref="SogaPresenceOptions.OfflineGracePeriod"/>, so a quick reconnect does not flicker their status. This
/// provider only sees one server instance.
/// </remarks>
internal sealed class InMemoryPresenceProvider(IOptions<SogaOptions> options) : ISogaPresenceProvider
{
    /// <summary>
    /// The lock that guards <see cref="_states"/>.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// The presence state of each known user, keyed by tenant and user.
    /// </summary>
    private readonly Dictionary<(string Tenant, string User), State> _states = [];

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <summary>
    /// Represents the presence state of one user.
    /// </summary>
    private sealed class State
    {
        /// <summary>Gets the user's open connection IDs.</summary>
        public HashSet<string> Connections { get; } = [];

        /// <summary>Gets or sets a value indicating whether the user is online, including during the grace period.</summary>
        public bool Online { get; set; }

        /// <summary>Gets or sets when the user's last connection closed.</summary>
        public DateTimeOffset? LastSeen { get; set; }

        /// <summary>Gets or sets when the grace period ends and the user goes offline, if it is running.</summary>
        public DateTimeOffset? OfflineAt { get; set; }
    }
}
