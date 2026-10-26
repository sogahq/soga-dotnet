# 0008: Synchronization log and cursor

- Status: Accepted
- Date: 2026-09-18

## Context

A reconnecting client must recover everything it missed. A cursor-based read of a change log is only safe if a change can never become visible *below* a cursor a client already holds.

## Decision

Every protocol event (`ConversationCreated`, `MessageCreated`, `ReadPositionChanged`) is written as a row in `SogaChanges` in the same transaction as the entity change and the outbox row. The change and the outbox event share one event ID and payload, so a client that receives an event live and again through sync deduplicates by ID.

**Sequence.** Changes are numbered per tenant from a counter row in `SogaTenantState`, advanced with an `UPDATE` that holds a row lock until commit. Commit order therefore equals sequence order, so a cursor can never skip a change. (An auto-increment column would let a lower number commit after a higher one was already read, silently losing that change.)

**Visibility.** `GET /sync` returns changes after the cursor for conversations the caller is an active participant of **now**, so lost access stops the flow.

**Cursor.** Opaque, produced by ASP.NET Core Data Protection, and bound to its kind, tenant, and scope (the user for sync, the conversation for history). Tampered, malformed, over-long, or replayed-elsewhere cursors return `soga.cursor.invalid`. The returned cursor is the **page boundary**; `hasMore` says whether to continue immediately.

**Start.** With no cursor, `/sync` returns the current head cursor and no events. Clients call `/sync` first, then load state over HTTP, so nothing falls in the gap.

## Alternatives considered

A timestamp cursor was rejected (clock skew, ties). Per-conversation cursors were rejected as too heavy for clients. An auto-increment log was rejected for the visibility race above.

## Consequences

All writes in a tenant briefly serialize on the tenant counter row. That is the price of a correct cursor; it is acceptable for V1 and is the first thing to revisit for very high per-tenant write rates. **Change rows are not purged in V1**, so a cursor never expires; retention will be added with a documented "cursor too old" behavior. Cursors depend on Data Protection keys: hosts running multiple instances or containers must persist and share them, otherwise clients receive `soga.cursor.invalid` and must resynchronize from scratch.

## Security and migration implications

Cursors are integrity-protected and scope-bound. The change payload is a stored copy of the protocol event, never an EF entity.
