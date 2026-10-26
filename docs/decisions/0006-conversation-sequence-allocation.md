# 0006: Conversation sequence allocation

- Status: Accepted
- Date: 2026-09-18

## Context

Each message needs a per-conversation number that is unique, increasing, and safe under concurrent sends. It drives history order, pagination cursors, read positions, and unread counts.

## Decision

The conversation row holds `LastSequence`. Inside the send transaction Soga runs `UPDATE ... SET LastSequence = LastSequence + 1` and reads the value back. The UPDATE takes a row lock that is held until commit, so concurrent senders to one conversation serialize, numbers have no gaps, and a rolled-back send returns its number.

A unique index on `(TenantId, ConversationId, Sequence)` is the backstop.

Lock order is always: conversation row, then tenant counter row (ADR 0008). A fixed order prevents deadlocks.

## Alternatives considered

Optimistic concurrency on `LastSequence` was rejected: it turns every contended send into a retry storm. Database sequence objects were rejected: they leave gaps, are provider-specific, and one per conversation does not scale. `MAX(Sequence)+1` was rejected as a race.

## Consequences

Sends to the same conversation are serialized by the database. A conversation between two people is nowhere near a throughput limit; a future group conversation with heavy traffic will need to revisit this.

## Security and migration implications

None beyond the unique index.
