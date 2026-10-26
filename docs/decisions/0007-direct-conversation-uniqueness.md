# 0007: Direct conversation uniqueness

- Status: Accepted
- Date: 2026-09-18

## Context

"Create or retrieve" must return the same conversation however many times, from either user, and under concurrent requests.

## Decision

There is exactly one direct conversation per **unordered user pair and external reference** within a tenant. A missing reference is its own "general" conversation; `order:1` and `order:2` between the same two users are separate conversations, and `""` normalizes to no reference.

The rule is enforced by the database: `DirectKey` is the SHA-256 (hex, 64 characters) of a length-prefixed encoding of the ordinally-sorted user IDs and the reference, with a unique index on `(TenantId, DirectKey)`. Hashing keeps the indexed value at a fixed length under every provider's index-size limit.

Creation prechecks the key, then inserts. If a concurrent request wins the unique constraint, the transaction retries and the precheck returns the winner's conversation.

A user cannot open a conversation with themselves.

## Alternatives considered

Ignoring the reference in uniqueness was rejected: order-scoped threads are a primary use case. A composite index over both user IDs and the reference was rejected: it needs the pair ordered by the application anyway and can exceed index-size limits.

## Consequences

Soga does not verify that the other user exists (it cannot see the host's users). Any authenticated user can open a conversation with any user ID. Hosts should combine this with rate limiting today; a contact-policy hook is a recommended post-V1 addition.

## Security and migration implications

The key is derived from trusted identities only.
