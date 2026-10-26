# Security

Authentication is required by default. The acting user and tenant come from trusted host context, never from request payloads. **Soga does not provide end-to-end encryption**; it relies on HTTPS for transport security, and message content is stored in your database as plain text.

Report vulnerabilities privately as described in `SECURITY.md`.

## Trust model

| Fact | Source | Never from |
|---|---|---|
| Acting user | Authenticated principal (`Soga:Identity:UserIdClaimType`) | Request body, query, headers |
| Tenant | `ISogaTenantContext` (default: configured single tenant) | Request body, query |
| Message sender | The acting user (system messages: server code only) | A `senderId` field |
| Role | The database, read inside the transaction that acts on it | Request body, tokens, SignalR groups |
| Conversation access | The database, checked on every operation | SignalR group membership |

Every protected operation goes through one application workflow that checks active participation and role. HTTP endpoints and the hub call the same code, so the rules cannot drift apart. Host policies run after Soga's own checks and can only deny (ADR 0014).

## Threat model

| Threat | Mitigation | Test |
|---|---|---|
| Sender impersonation | Sender derived from the principal; body fields ignored | `Send_IgnoresAnySenderSuppliedByTheClient` |
| System-message impersonation | System kind is set only by trusted code; edit and delete refuse it | `SystemMessages_...CannotBeForgedByClients` |
| Posing as the system sender | User IDs starting with `$soga:` are reserved: such a caller is unauthenticated and such an ID cannot be invited | `ReservedUserIds_...`, `AUserWithAReservedId_IsRefusedOverHttpAndSignalR` |
| Insecure direct object reference | Membership checked for every conversation ID | `Outsider_SeesIdenticalNotFound...` |
| Conversation enumeration | Missing, foreign, and other-tenant conversations answer identically, for group routes too | `Outsider_SeesIdenticalNotFoundForEveryGroupRoute` |
| Privilege escalation through roles | Role rules table; only owners grant privilege; unknown roles are rejected, never downgraded | `RoleEscalationAttempts_AreAllRefused`, `GroupRulesTests` |
| Removing the last owner | Decided inside the locked transaction; concurrent departures yield exactly one success | `ConcurrentLastOwnerDepartures_...` |
| Unauthorized participant addition | Add needs owner/admin; policy hook on top; size limit | `Add_ByOwnerAndAdmin_RespectsWhoMayGrantWhat` |
| Former-participant access | Removal cuts HTTP, sync, and every live connection; only the removal notice remains | `RemovedParticipant_StopsReceivingOnEveryDeviceWhileStillConnected` |
| Subscription persisting after removal | The publisher removes the user's connections from the group; re-subscribe is refused | same |
| Edit or delete ownership bypass | Author-only edit; delete needs authorship or an admin role; both checked in the store | `MessageRulesTests`, `Message_EditDeleteAndReply_...` |
| Deleted content exposure | Text is erased on delete, including retained edit history and the retry fingerprint | `Delete_ErasesTextAndRetainedHistory...`, `Delete_ErasesTheFingerprintTogetherWithTheText` |
| Duplicate or lost message on retry after an edit | Retries are compared with a fingerprint of the text as first sent, which edits never change | `IdempotencyAfterLifecycleTests`, `Retry_AfterAnEdit_...` |
| Cross-conversation reply reference | Target must be in the same conversation and not deleted | `Reply_ToAnotherConversationsMessage_IsRefused` |
| Metadata injection and leakage | Flat, bounded, validated; never included in events | `SogaMetadataTests` |
| Typing and presence privacy | Members only; no text; last-seen off by default; can be disabled | `Typing_...`, `Presence_...` tests |
| Group-size and fan-out abuse | Configurable size limit; typing throttled; conversation and page limits | `ConcurrentGroupAdds_RespectTheSizeLimit` |
| Cross-tenant access | Tenant in every key, index, cursor, and group name | `Tenants_AreIsolated...`, `Hub_ScopesGroupsByTenant` |
| **User identity confusion (`Alice` vs `alice`) on SQL Server** | Case-sensitive collation and an ordinal key comparer on every identity column; the store refuses to start on SQL Server without them | `EfIdentityTestsBase`, `SqlServerSafetyTests` |
| Duplicate-send replay | Unique idempotency key; changed content is a conflict | `Send_WhenSameKeyIsReused...` |
| Cursor tampering or replay | Data Protection, bound to kind, tenant, and scope (including list filters) | `SogaCursorProtectorTests`, `List_CursorsCannotBeReplayed...` |
| Oversized content (DoS) | Text limit, bounded body reader, page and batch limits | `Send_WhenBodyIsOversized...` |
| Broken or malicious host extension | A throwing policy denies; a throwing audit sink never undoes work; validators can only reject | `Policy_ThatThrowsDeniesTheOperation` |
| Sensitive data in logs | No content or tokens logged; audit entries hold IDs and codes; outbox failures store only the exception type | code review |
| Information leaks in errors | Generic `soga.internal_error`; no stack traces | `UnexpectedFailure_ReturnsGenericProblem...` |

## Known limits

- **No end-to-end encryption**, moderation, blocking, or retention controls. Message text is plain text at rest: use database encryption and access controls.
- **Contact rules are the host's.** Soga does not decide who may start a conversation; use `ISogaAuthorizationPolicy` (see the marketplace sample). Without a policy, any authenticated user can open a conversation with any user ID, and Soga cannot check the ID exists.
- **Cursors depend on Data Protection keys.** Persist and share the key ring in multi-instance deployments.
- **A custom `ISogaUserContext` does not apply to SignalR**, which reads the connection principal with the configured claim type.
- **Presence and typing are per process.** With several instances each sees only its own connections until a distributed provider exists (V3).
- **SQL Server needs the identity collation** (see above). PostgreSQL needs nothing extra. Both were verified with the store and concurrency suites (SQL Server 2025, PostgreSQL 17); run them against your own engine version before relying on it.
- **Group creation is not idempotent**: a retried create can produce a second group.

## Host responsibilities

- Serve over HTTPS only. Send SignalR access tokens the way your scheme supports (for browsers, the `access_token` query string over WebSockets), and make sure your proxy and logs do not record query strings.
- Configure CORS with explicit allowed origins for browser clients.
- Apply `UseRateLimiter()` and set `Soga:Security:RateLimitPolicyName`; add `.UseSogaRejectionResponse()` for stable error bodies.
- Send audit entries (`ISogaAuditSink`) to a tamper-resistant store.
- Do not log request bodies.
