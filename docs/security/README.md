# Security

Authentication is required by default. The acting user and tenant come from trusted host context, never from request payloads. **Soga V1 does not provide end-to-end encryption**; it relies on HTTPS for transport security, and message content is stored in your database as plain text.

Report vulnerabilities privately as described in `SECURITY.md`.

## Trust model

| Fact | Source | Never from |
|---|---|---|
| Acting user | Authenticated principal (`Soga:Identity:UserIdClaimType`) | Request body, query, headers |
| Tenant | `ISogaTenantContext` (default: configured single tenant) | Request body, query |
| Message sender | The acting user | A `senderId` field |
| Conversation access | The database, checked on every operation | SignalR group membership |

Every protected read and write goes through one application workflow that checks active participation. HTTP endpoints and the hub call the same workflows, so the rules cannot drift apart.

## Threat model

| Threat | Mitigation | Test |
|---|---|---|
| Sender impersonation | Sender derived from the principal; body fields ignored | `Send_IgnoresAnySenderSuppliedByTheClient` |
| Insecure direct object reference | Membership checked for every conversation ID | `Outsider_SeesIdenticalNotFound...` |
| Conversation enumeration | Missing, foreign, and other-tenant conversations answer identically | same |
| Cross-tenant access | Tenant in every key, index, cursor, and group name; store methods require it | `Tenants_AreIsolated...`, `Hub_ScopesGroupsByTenant` |
| Unauthorized SignalR subscription | Access re-checked on subscribe; groups are not authorization | `Subscription_ByANonParticipant...` |
| Duplicate-send replay | Unique idempotency key; changed content is a conflict | `Send_WhenSameKeyIsReused...` |
| Cursor tampering or replay | Data Protection, bound to kind, tenant, and scope | `SogaCursorProtectorTests`, `History_WithTamperedOrForeignCursor...` |
| Oversized content (DoS) | Text limit, bounded body reader, page and batch limits | `Send_WhenBodyIsOversized...` |
| Reconnect and sync abuse | Bounded batches; rate-limit policy on all routes | `RateLimit_ReturnsStableCode...` |
| Sensitive data in logs | No content, tokens, or IDs logged or used as metric labels; outbox failures store only the exception type | code review, `SogaDiagnostics` |
| Information leaks in errors | Generic `soga.internal_error`; no stack traces | `UnexpectedFailure_ReturnsGenericProblem...` |

## Known limits of V1

- **No contact policy.** Any authenticated user can open a conversation with any user ID, and Soga cannot check the ID exists. Use the rate-limit policy, and restrict who may reach `MapSoga()` if needed. A policy hook is the recommended next addition.
- **No end-to-end encryption**, moderation, blocking, or message retention controls.
- **Message content is plain text at rest.** Use database encryption and access controls.
- **Cursors depend on Data Protection keys.** Persist and share the key ring in multi-instance deployments.
- **A custom `ISogaUserContext` does not apply to SignalR**, which reads the connection principal.
- **Only the in-memory and SQLite providers are tested in this repository.** The concurrency guarantees (row-lock sequence allocation, unique-constraint retries) are designed for PostgreSQL and SQL Server but are not yet exercised against them here. Run the store tests against your production engine before relying on it (see the deployment checklist).

## Host responsibilities

- Serve over HTTPS only. Send SignalR access tokens the way your scheme supports (for browsers, the `access_token` query string over WebSockets), and make sure your proxy and logs do not record query strings.
- Configure CORS with explicit allowed origins for browser clients.
- Apply `UseRateLimiter()` and set `Soga:Security:RateLimitPolicyName`; add `.UseSogaRejectionResponse()` for stable error bodies.
- Do not log request bodies.
