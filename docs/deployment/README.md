# Deployment

## Production checklist

- [ ] `.UseEntityFrameworkCore<T>()` is configured. The in-memory provider logs a warning and loses data on restart.
- [ ] `modelBuilder.AddSoga()` is called and the migration is reviewed and applied by your normal process.
- [ ] **SQL Server only:** `AddSoga(identityCollation: "Latin1_General_100_BIN2")` is used. The application refuses to start without it.
- [ ] HTTPS only; HSTS enabled.
- [ ] Real authentication is configured, and `Soga:Identity:UserIdClaimType` matches your claim.
- [ ] Data Protection keys are persisted and shared across instances (cursors depend on them).
- [ ] CORS lists explicit origins for browser clients.
- [ ] A rate-limit policy is registered, `UseRateLimiter()` is called, and `Soga:Security:RateLimitPolicyName` is set.
- [ ] Host rules that decide who may start conversations or create groups are registered (`ISogaAuthorizationPolicy`).
- [ ] Audit entries go somewhere durable (`ISogaAuditSink`).
- [ ] Health checks are mapped and monitored; telemetry is subscribed (below).
- [ ] The database is backed up. The messages, edit-history, changes, and outbox tables hold message content.
- [ ] The store suites were run against your production database engine.
- [ ] You run one realtime instance, or have configured a SignalR backplane yourself (see below).

## Database support

| Provider | Status |
|---|---|
| SQL Server | Supported. The store, identity, and concurrency suites pass on SQL Server 2025 |
| PostgreSQL | Supported. The store, identity, and concurrency suites pass on PostgreSQL 17 |
| SQLite | Development and tests only |

Run the suites against your own server (they create and drop a throwaway database):

```bash
export SOGA_TEST_SQLSERVER="Server=localhost;Integrated Security=true;TrustServerCertificate=true"
export SOGA_TEST_POSTGRES="Host=localhost;Username=postgres;Password=..."
dotnet test tests/Soga.EntityFrameworkCore.Tests
```

## Configuration

Everything is under `Soga:` in configuration, or in `AddSoga(options => ...)` (code wins). Invalid values stop the application at startup with a message per problem.

| Key | Default | Notes |
|---|---|---|
| `RoutePrefix` | `/soga/v1` | Must start with `/`, must not end with `/` |
| `DefaultTenantId` | `default` | Used when no tenant resolver is registered |
| `Identity:UserIdClaimType` | `ClaimTypes.NameIdentifier` | |
| `Conversations:DefaultPageSize` / `MaximumPageSize` | 50 / 100 | Max 500 |
| `Conversations:MaximumMetadataBytes` / `MaximumMetadataProperties` | 4096 / 20 | Hard cap 16384 bytes, 100 properties |
| `Groups:Enabled` | true | |
| `Groups:MaximumParticipants` | 256 | 2 to 10000. Measure before raising |
| `Groups:MaximumTitleLength` | 100 | Max 200 |
| `Groups:PostSystemMessages` | false | Post membership changes into the timeline |
| `Messages:MaximumTextLength` | 8000 | 1 to 10000 |
| `Messages:DefaultPageSize` / `MaximumPageSize` | 50 / 100 | Max 500 |
| `Messages:AllowEditing` / `EditWindow` | true / 15 min | `EditWindow` unset means no time limit |
| `Messages:RetainEditHistory` | false | Keep previous texts (erased when the message is deleted) |
| `Messages:AllowDeletion` / `AllowAdminDeletion` | true / true | Admin deletions are audited |
| `Sync:DefaultBatchSize` / `MaximumBatchSize` | 100 / 500 | |
| `Typing:Enabled` | true | |
| `Typing:Timeout` / `MinimumBroadcastInterval` | 6 s / 2 s | Interval must be shorter than the timeout |
| `Presence:Enabled` | true | |
| `Presence:OfflineGracePeriod` | 10 s | Absorbs reconnect flaps |
| `Presence:ExposeLastSeen` | false | Privacy: share when a user was last online |
| `Security:RateLimitPolicyName` | none | A policy you registered with `AddRateLimiter` |
| `Security:MaximumRequestBodyBytes` | 65536 | 1024 to 1048576 |
| `Outbox:DispatcherEnabled` | true | Turn off on instances that must not publish |
| `Outbox:PollInterval` | 1 s | Fallback; commits also wake the dispatcher |
| `Outbox:BatchSize` | 50 | |
| `Outbox:LeaseDuration` | 30 s | Longer than your slowest publish |
| `Outbox:MaximumAttempts` | 10 | Then the event is marked failed; sync still delivers it |
| `Outbox:BaseRetryDelay` / `MaximumRetryDelay` | 2 s / 5 min | Exponential with jitter |
| `Outbox:Retention` | 7 days | Published rows are purged after this |
| `Outbox:UnhealthyBacklogAge` | 5 min | Health is Degraded past this |

## Migrations

You own them (ADR 0005). Add `AddSoga()` to your model, run `dotnet ef migrations add`, review, and apply. During preview the schema can change between releases without an automatic path; read the release notes and the [V1 to V2 guide](../migration/v1-to-v2.md) before upgrading.

## Outbox and event subscribers

Committed events are announced by a background service. It is safe to run on several instances; they lease rows so none is claimed twice, and a crashed instance's rows are retried after the lease expires. Delivery is at least once. Your `ISogaEventSubscriber` implementations run after realtime publication and, if they fail, the event is retried, so they must be idempotent.

Monitor `soga.outbox.failed` and the health check. A terminally failed event is not lost: clients still receive it through `/sync`.

## Multiple instances

SignalR needs a backplane (for example Redis) to fan out across several instances, and typing and presence are counted per process. Both are outside V2: run a single realtime instance, or configure the backplane yourself and accept that typing and presence are only correct within one instance until the V3 provider exists. Sync and the database are shared and work across instances.

## Health

```csharp
app.MapHealthChecks("/health");        // includes the "soga" check
```

`Unhealthy` means the database is unreachable. `Degraded` means the oldest pending outbox event is older than `Outbox:UnhealthyBacklogAge`.

## Telemetry

| Signal | Name |
|---|---|
| Traces | `ActivitySource` `Soga`: `soga.conversation.*`, `soga.group.*`, `soga.message.send`, `.edit`, `.delete`, `.history`, `soga.read.*`, `soga.sync.execute`, `soga.outbox.dispatch`, `soga.realtime.publish` |
| Metrics | `Meter` `Soga`: `soga.commands` (tags `command`, `outcome`), `soga.outbox.published`, `soga.outbox.failed` (tag `terminal`), `soga.outbox.dispatch.duration`, `soga.realtime.connections` |

```csharp
.WithTracing(t => t.AddSource(SogaDiagnostics.SourceName))
.WithMetrics(m => m.AddMeter(SogaDiagnostics.MeterName))
```

Metric tags are bounded (command names and stable error codes). Message text, tokens, user IDs, and conversation IDs are never used as labels or logged by default.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Startup fails: "SQL Server compares strings case-insensitively" | Add `identityCollation` to `AddSoga` (see above) and migrate |
| `InvalidOperationException ... does not include the Soga model` | `modelBuilder.AddSoga()` is missing from `OnModelCreating` |
| `soga.cursor.invalid` after a restart or deploy | Data Protection keys were not persisted; reload state and start `/sync` with no cursor |
| Messages arrive on `/sync` but not live | Client did not call `SubscribeToConversation` or `SubscribeToAll`, the outbox dispatcher is disabled, or the health check is Degraded |
| Events sent right after connecting are missed | Wait for `NegotiateProtocol` before relying on live delivery; sync covers the gap |
| `soga.tenant.unavailable` | Your `ISogaTenantContext` returned empty or over 100 characters |
| `soga.authorization.denied` unexpectedly | A registered `ISogaAuthorizationPolicy` denied it, or threw (a throwing policy denies; check the logs) |
| Presence or typing wrong across servers | They are per process; use one realtime instance |
| `GET /conversations` returns an object, not an array | V2 returns `{ items, nextCursor }` |
| Hub connects then drops | Authentication failed for the hub request; check how your scheme passes the token for WebSockets |
| `429` without `code` | Add `.UseSogaRejectionResponse()` to `AddRateLimiter` |
| Startup fails with `OptionsValidationException` | Read the listed `Soga:` keys; each failure is named |
