# 0001: Public ID format

- Status: Accepted
- Date: 2026-09-18

## Context

Conversations, messages, and events need public identifiers that are unguessable enough not to be enumerable, cheap to generate without a database round trip, and roughly time-sortable for index locality.

## Decision

Public IDs are UUIDv7 (`Guid.CreateVersion7()`), generated through the internal `ISogaIdGenerator` so tests can be deterministic.

IDs and timestamps never define order. The authoritative order of messages is the per-conversation `Sequence` (ADR 0006). Idempotency and authorization never depend on ID structure.

## Alternatives considered

ULID was rejected: it needs an extra dependency or a hand-written implementation and adds a second textual format for no benefit over `Guid` in .NET. Random UUIDv4 was rejected because it fragments B-tree indexes.

## Consequences

Hosts on SQL Server should know `uniqueidentifier` sorts by a different byte order than UUIDv7's time prefix, so index locality is weaker there than on PostgreSQL. This affects performance only, never correctness.

## Security and migration implications

UUIDv7 embeds a millisecond timestamp, so an ID reveals roughly when it was created. Access control never relies on IDs being secret.
