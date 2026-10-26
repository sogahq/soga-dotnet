# Concepts

## The guarantees

1. Only authorized users can access a conversation. Access is checked for every operation, over HTTP and SignalR alike.
2. An accepted message is durable. It is stored, together with its sync change and outbox event, in one transaction before anything is announced.
3. A retry does not create a duplicate.
4. Messages in a conversation have a deterministic order.
5. A disconnected client can recover everything it missed.
6. Read state stays correct.

Realtime delivery means an event was *offered* to an active connection. It does not mean the recipient read it, and it is never exactly once: clients deduplicate by `eventId`.

## Direct conversations

One conversation per unordered pair of users and optional `externalReference`. `alice→bob` and `bob→alice` are the same conversation. `order:1` and `order:2` between the same users are separate conversations. Opening one twice returns the same conversation (`201` the first time, `200` after). See ADR 0007.

A user who is not a participant, a conversation that does not exist, and a conversation in another tenant all answer with the identical `404 soga.conversation.not_found`, so private conversations cannot be enumerated.

## Messages and idempotency

`clientMessageId` is required. Sending the same key with the same text returns the original message (`200`). The same key with different text returns `409 soga.message.idempotency_conflict`. Keys are scoped per sender and conversation. See ADR 0011.

Content is plain text, at most `Messages:MaximumTextLength` UTF-16 units (default 8000, else `422 soga.message.too_long`). It is untrusted: encode it when rendering.

## Ordering and history

Every message has a per-conversation `sequence` (1, 2, 3, ..., no gaps). Order by it, never by timestamp or ID.

`GET /conversations/{id}/messages?limit=&cursor=` returns newest first. `nextCursor` is opaque and points to a position, not a page number, so new messages arriving between pages never shift them. `nextCursor` is `null` when history is exhausted. `limit` above the maximum is clamped; below 1 is an error.

## Read state and unread counts

Each participant has one `lastReadSequence`. `POST /conversations/{id}/read { "messageId": ... }` advances it through that message. It never moves backward: reading an older message is a no-op that returns the current state.

Unread = messages from *others* after `lastReadSequence`. Your own messages are never unread for you. `GET /unread` sums it across conversations, and conversation summaries carry their own `unreadCount`.

## Synchronization

See the recovery steps in [Getting started](../getting-started/README.md). Points to remember:

- A cursor is a position, integrity-protected, and bound to the user and tenant. Invalid or replayed cursors return `soga.cursor.invalid`; on that error, start over with no cursor and reload state.
- The returned cursor is the **page boundary**. Keep calling while `hasMore` is true.
- Sync re-checks access each time.
- Sync and realtime deliver the same `eventId` for the same change.
- Realtime is an optimization. If it fails, sync still has the change. See ADR 0008.

## Events

Every event has `eventId`, `protocolVersion`, `type`, `occurredAt`, `conversationId`, `sequence` (when relevant), and `data`.

| Type | `data` | Sent to |
|---|---|---|
| `MessageCreated` | `{ message }` | Subscribers of the conversation |
| `ReadPositionChanged` | `{ userId, lastReadSequence }` | Subscribers of the conversation |
| `ConversationCreated` | `{ participantUserIds, externalReference, createdAt }` | Each participant, no subscription needed |
| `ConversationAccessChanged` | `{ userId, access }` | Reserved; not emitted in V1 |

## HTTP API

All routes are under the route prefix (default `/soga/v1`) and require authentication.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/conversations/direct` | Create or return a direct conversation |
| `GET` | `/conversations?limit=` | List conversations with unread counts |
| `GET` | `/conversations/{id}` | Get one conversation |
| `GET` | `/conversations/{id}/messages?limit=&cursor=` | Cursor-paginated history |
| `POST` | `/conversations/{id}/messages` | Send an idempotent text message |
| `POST` | `/conversations/{id}/read` | Advance the read position |
| `GET` | `/unread` | Total unread count |
| `GET` | `/sync?limit=&cursor=` | Durable changes after a cursor |
| `GET` | `/context` | The trusted user and tenant Soga sees |
| `*` | `/hub` | SignalR hub |

Hub methods: `NegotiateProtocol(int)`, `SubscribeToConversation(Guid)`, `UnsubscribeFromConversation(Guid)`.

## Errors

Errors are `application/problem+json` with a stable `code`. Branch on `code`, never on the English text.

| Code | Status | Meaning |
|---|---|---|
| `soga.auth.unauthorized` | 401 | No authenticated user |
| `soga.tenant.unavailable` | 403 | Tenant could not be resolved |
| `soga.conversation.not_found` | 404 | Missing, inaccessible, or wrong tenant |
| `soga.message.not_found` | 404 | Message is not in the conversation |
| `soga.message.too_long` | 422 | Text over the limit |
| `soga.message.idempotency_conflict` | 409 | Key reused for different text |
| `soga.cursor.invalid` | 400 | Bad, tampered, or replayed cursor |
| `soga.request.invalid` | 400 / 413 | Malformed or invalid request; oversized body |
| `soga.rate_limited` | 429 | Rate limit exceeded (needs `UseSogaRejectionResponse`) |
| `soga.protocol.mismatch` | (hub) | Unsupported protocol version |
| `soga.internal_error` | 500 | Unexpected failure; details are only in server logs |
| `soga.conversation.not_participant` | (reserved) | Not returned in V1 |

A non-JSON content type is answered by ASP.NET Core itself with a plain `415`.
