# 0017: V2 protocol and compatibility

- Status: Accepted
- Date: 2026-09-19

## Context

V2 adds many contracts. ADR 0010 says changes within a protocol version are additive. V1 was only ever a preview.

## Decision

The protocol version stays `1`. Almost every V2 change is additive:

- new endpoints, hub methods (`SubscribeToAll`, `SetTyping`, `GetPresence`), and event types (`MessageUpdated`, `MessageDeleted`, `ConversationUpdated`, `ConversationArchived`, `ParticipantChanged`, and the ephemeral `TypingChanged` and `PresenceChanged`);
- new optional fields: on messages `editedAt`, `deleted`, `replyTo`; on conversations `title`, `metadata`, `archivedAt`, `myRole`, `participantCount`, `participants`, `lastMessage`; on `ConversationCreated` `kind` and `title`; on requests `replyToMessageId` and `metadata`;
- new error codes; none is removed or changes meaning.

**One deliberate breaking change during preview:** `GET /conversations` now returns `{ "items": [...], "nextCursor": ... }` instead of a bare array, because cursor pagination cannot be expressed by an array. V1 never left preview (`0.x`), where breaking changes are allowed, so the version number is not bumped. After `1.0` a change like this requires protocol version 2.

**Clients must ignore unknown fields, event types, message kinds, and error codes.** This is what makes the additive rules safe and is documented as a client requirement.

Canonical JSON fixtures pin every shape, including the new fields.

## Alternatives considered

Adding a new list route and keeping the old array was rejected: it leaves two ways to do one thing in a protocol that is still forming. Bumping to version 2 now was rejected: no stable V1 exists to stay compatible with.

## Consequences

V1 preview clients that read the conversation list must be updated. See the migration guide.

## Security and migration implications

None beyond the schema changes in the migration guide.
