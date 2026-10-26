# 0011: Idempotency and conflicts

- Status: Accepted
- Date: 2026-09-18

## Context

A client that times out must be able to retry a send without creating a duplicate, and a client bug that reuses a key for different content must be detectable.

## Decision

`clientMessageId` is required, 1 to 100 characters of letters, digits, `.`, `_`, `-`, `:`. The idempotency tuple is `(TenantId, ConversationId, SenderId, ClientMessageId)`, enforced by a unique index; an application precheck only makes the common path cheap.

- Same key and identical text: `200 OK` with the originally stored message. Nothing new is written, so no second change or outbox event exists.
- Same key and different text: `409` with `soga.message.idempotency_conflict`. The response carries **no data from the existing message**, so a conflict cannot be used to read content.
- Concurrent identical requests: one commits; the other hits the unique index, retries, and returns the committed message.

Text is compared ordinally and exactly, with no trimming.

## Alternatives considered

Returning the existing message on a conflict was rejected: it discloses content to whoever guesses a key (only the same sender can, but the safer default costs nothing). Keying by content hash was rejected: two intentional identical messages must both be accepted.

## Consequences

Keys are scoped per sender and conversation, so two users may reuse the same key safely. Keys are kept as long as the message exists.

## Security and migration implications

The key is bounded and validated; an unbounded header value is never stored.
