# 0009: Outbox dispatch

- Status: Accepted
- Date: 2026-09-18

## Context

A realtime event must never be announced for a message that was not committed, and a committed message must eventually be announced even if the process crashes.

## Decision

The outbox row is written in the same transaction as the message. A hosted service (`SogaOutboxDispatcher`) is the only publisher; the request path never broadcasts.

**Claiming.** Rows are claimed without provider-specific locking: candidates are read, then each is leased with a conditional `UPDATE` that succeeds only while no live lease exists. Competing workers never double-claim, and a crashed worker's lease simply expires and the event is retried.

**Retry.** Failure releases the lease and schedules a retry with exponential backoff and plus or minus 20 percent jitter (base 2 s, cap 5 min). After `MaximumAttempts` (10) the row is marked failed and kept for inspection; sync still delivers the event. Only the exception type name is stored, never its message.

**Delivery** is at least once. A crash after publishing and before completing republishes; clients deduplicate by event ID.

**Ordering.** Events are claimed oldest first. Events with the same timestamp are claimed in commit order (their change-log sequence), so a conversation is never announced after a message sent in it in the same instant. Retries can still reorder, so clients order by `sequence`, never by arrival.

**Latency.** Workflows signal the dispatcher after commit, so delivery does not wait for the poll interval (default 1 s), which remains as a fallback and for other instances.

**Cleanup.** Published rows older than `Retention` (7 days) are purged.

**Routing.** `MessageCreated` and `ReadPositionChanged` go to the conversation group; `ConversationCreated` goes to each participant's user group.

## Alternatives considered

Publishing from the request path after commit was rejected: a crash between commit and publish loses the event. A message broker was rejected as a required dependency for V1.

## Consequences

Any instance can dispatch. `DispatcherEnabled` turns it off on instances that should not publish. Multi-instance realtime fan-out needs a SignalR backplane, which is out of scope for V1.

## Security and migration implications

Outbox payloads contain message content, so the table needs the same protection as the messages table.
