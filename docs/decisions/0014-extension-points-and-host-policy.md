# 0014: Extension points and host authorization policy

- Status: Accepted
- Date: 2026-09-19

## Context

Hosts need to add their own rules ("only the buyer and seller of an order may talk about it", "no links", "log every role change") without Soga growing an option for each, and without a rule ever being able to open a hole.

## Decision

Soga exposes six small public interfaces. Persistence stays internal.

| Interface | Purpose |
|---|---|
| `ISogaAuthorizationPolicy` | Further restrict any operation (`SogaAction`) |
| `ISogaMessageValidator` | Reject a message or edit with a machine-readable reason |
| `ISogaAuditSink` | Receive audit entries for privileged operations |
| `ISogaEventSubscriber` | React to committed events (for example push notifications) |
| `ISogaSystemMessages` | Post system messages from trusted code |
| `ISogaPresenceProvider` | Replace the single-process presence tracker |

The user and tenant resolvers (`ISogaUserContext`, `ISogaTenantContext`) remain the identity extension points (ADR 0002).

**A policy can only deny.** Soga always applies its own membership and role checks first; only then are host policies consulted, and every registered policy must allow. A policy cannot grant what Soga refuses, cannot change the tenant (it is in the context, read-only), and cannot see or return message content except through the validator. A policy that throws denies the operation (fail closed) and only its type name is logged.

**Membership before policy.** For any operation on an existing conversation the caller's membership is confirmed first, so a non-participant always sees `soga.conversation.not_found` and never learns from a denial that a conversation exists.

**Same rules everywhere.** HTTP endpoints and the SignalR hub go through the same authorizer, so a policy that denies `Subscribe` also blocks `SubscribeToAll`.

**Validators** run after Soga's own validation and authorization, for sends and edits. They can accept or reject with a reason code (returned as the error detail); they cannot rewrite the message.

**Audit.** `group.created`, `group.updated`, `group.archived`, `group.unarchived`, `participant.added`, `participant.removed`, `participant.left`, `participant.role_changed`, `message.deleted_by_admin`. Entries hold IDs and short codes, never message text. A failing sink is logged and never undoes the completed operation. The default sink writes structured logs.

**Subscribers** run in the outbox dispatcher after realtime publication. Delivery is at least once: a failing subscriber fails the event, which is retried (so realtime may repeat, never skip). Subscribers must be idempotent and can deduplicate by event ID.

## Alternatives considered

Letting policies grant access was rejected: it makes Soga's guarantees depend on host code. A public repository abstraction was rejected: it would freeze internals that must preserve Soga's invariants. A user-profile provider was deferred: no V2 workflow needs display data.

## Consequences

These interfaces are a compatibility commitment from the stable release. A distributed presence provider (V3) implements `ISogaPresenceProvider`.

## Security and migration implications

Adversarial tests confirm that an allow-everything policy cannot let an outsider send or a member escalate, and that a broken policy denies.
