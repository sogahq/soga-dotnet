# 0012: Group conversations and roles

- Status: Accepted
- Date: 2026-09-19

## Context

V2 adds group conversations with owner, administrator, and member roles. Membership and ownership changes race with each other, and a removed participant must lose access immediately, on every device.

## Decision

**Model.** A conversation has a kind (`Direct` or `Group`). A participant has a role: `owner`, `admin`, or `member`. A participant row is never deleted: leaving or removal sets `LeftAt`, so the change is auditable and re-adding reactivates the row instead of duplicating it. Direct conversations always have two `member` participants and cannot be managed.

**Rules** live in one pure class, `GroupRules`, used by both stores:

| Action | Who | Extra rules |
|---|---|---|
| Add a participant | owner, admin | only an owner can grant `admin` or `owner`; adding a current member changes nothing; the group size limit applies |
| Remove a participant | owner (anyone); admin (members only) | removing someone who already left changes nothing |
| Leave | any participant | the last owner cannot leave |
| Change a role | owner only | the last owner cannot be demoted |
| Rename, edit metadata, archive, restore | owner, admin | no-ops publish nothing |

A group always keeps at least one owner. Ownership is transferred by promoting another participant to `owner` first (several owners are allowed).

**Atomicity.** Each change runs in one transaction that first takes the conversation row lock (a no-op `UPDATE`), then loads a snapshot, lets `GroupRules` decide, and applies the result together with its change and outbox rows. Concurrent changes to one group therefore run one at a time and always see the previous result, which makes outcomes deterministic (for example, two owners leaving at once: exactly one succeeds).

**Access is revoked at once.** Every membership event is published to the conversation group and to the affected user's own group; the realtime publisher then removes all of that user's connections from the conversation group, on every device. Re-subscribing is refused by the membership check.

**Sync.** Visibility is decided when sync is read: only current participants see a conversation's changes. Membership events removing a user carry a `TargetUserId`, so the removed user still receives exactly one thing through sync: the notice that they were removed. A former participant has **no** access to history or to changes made before their removal (default deny).

**New members** start caught up (their read position is set to the current sequence) so joining an old group does not create a wall of unread messages, but they can read the full history.

**Group creation is not idempotent.** There is no natural key, so retrying a timed-out `POST /conversations/groups` can create a second group. Clients should not blindly retry it.

## Alternatives considered

Deleting participant rows was rejected: it loses the audit trail and makes re-adding non-idempotent. Optimistic concurrency on membership was rejected: it makes the last-owner rule racy. History visible only from the join date was rejected for V2 as needless complexity; a host that wants it can deny reads through an authorization policy.

## Consequences

Large, busy groups serialize membership changes (not messages). The default group limit is 256; larger groups need measurement first.

## Security and migration implications

Privilege escalation is prevented by the rules table above and covered by role-matrix unit tests and HTTP escalation tests. Removed users cannot subscribe, read, or send.
