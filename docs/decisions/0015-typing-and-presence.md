# 0015: Typing indicators and presence

- Status: Accepted
- Date: 2026-09-19

## Context

Typing and presence are useful, high-frequency, and privacy-sensitive. They must not affect durable correctness or authorization.

## Decision

Both are **ephemeral and advisory**: held in memory, never written to the database, never recovered through sync, and never used to authorize anything.

**Typing.** Hub method `SetTyping(conversationId, typing)`. The caller must be a participant (and pass host policy). The server broadcasts `TypingChanged { userId, typing }` to the conversation's other subscribers. It never carries text. Broadcasts are throttled per user and conversation (`Typing:MinimumBroadcastInterval`, default 2 s; faster calls only extend the expiry). State expires by itself after `Typing:Timeout` (default 6 s) if not refreshed, and a background service then broadcasts `typing: false`. It also stops when the typist disconnects and when the participant is removed. Disable with `Typing:Enabled`.

**Presence.** The first connection makes a user online; the last connection plus `Presence:OfflineGracePeriod` (default 10 s) makes them offline, so a flaky reconnect never flaps. Multiple devices are counted. `PresenceChanged { userId, online, lastSeenAt }` goes to the user's conversations; `GetPresence(conversationId)` returns the current state for a participant. Last-seen time is **off by default** (`Presence:ExposeLastSeen`) and is then absent from both events and results. Disable with `Presence:Enabled`. The provider is an interface so a distributed implementation can replace it in V3.

**Single instance only.** The built-in tracker counts connections in one process. With several instances presence is only correct within each instance; that needs the V3 provider.

## Alternatives considered

Persisting typing was rejected (write amplification for no value). Broadcasting presence to everyone was rejected on privacy grounds: it only reaches conversations the user belongs to.

## Consequences

After a server restart typing and presence reset; connected clients re-announce by reconnecting. A tick failure can leave an indicator briefly wrong, never a durable inconsistency.

## Security and migration implications

Non-members cannot read presence or send typing (they get `not_found`). Typing carries no user content by construction (tested).
