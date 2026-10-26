# Deployment

## Production checklist

- [ ] `.UseEntityFrameworkCore<T>()` is configured. The in-memory provider logs a warning and loses data on restart.
- [ ] `modelBuilder.AddSoga()` is called and the migration is reviewed and applied by your normal process.
- [ ] HTTPS only; HSTS enabled.
- [ ] Real authentication is configured, and `Soga:Identity:UserIdClaimType` matches your claim.
- [ ] Data Protection keys are persisted and shared across instances (cursors depend on them).
- [ ] CORS lists explicit origins for browser clients.
- [ ] A rate-limit policy is registered, `UseRateLimiter()` is called, and `Soga:Security:RateLimitPolicyName` is set.
- [ ] Health checks are mapped and monitored.
- [ ] Telemetry is subscribed (below).
- [ ] The database is backed up. The messages and outbox tables hold message content.
- [ ] The store tests were run against your production database engine.

## Configuration

Everything is under `Soga:` in configuration, or in `AddSoga(options => ...)` (code wins). Invalid values stop the application at startup with a message per problem.

| Key | Default | Notes |
|---|---|---|
| `RoutePrefix` | `/soga/v1` | Must start with `/`, must not end with `/` |
| `DefaultTenantId` | `default` | Used when no tenant resolver is registered |
| `Identity:UserIdClaimType` | `ClaimTypes.NameIdentifier` | |
| `Messages:MaximumTextLength` | 8000 | 1 to 10000 |
| `Messages:DefaultPageSize` / `MaximumPageSize` | 50 / 100 | Max 500 |
| `Conversations:DefaultPageSize` / `MaximumPageSize` | 50 / 100 | |
| `Sync:DefaultBatchSize` / `MaximumBatchSize` | 100 / 500 | |
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

You own them (ADR 0005). Add `AddSoga()` to your model, run `dotnet ef migrations add`, review, and apply. During preview the schema can change between releases without a migration path; read the release notes before upgrading.

## Outbox

Committed events are announced by a background service. It is safe to run on several instances; they lease rows so none is claimed twice, and a crashed instance's rows are retried after the lease expires. Delivery is at least once.

Monitor `soga.outbox.failed` and the health check. A terminally failed event is not lost: clients still receive it through `/sync`.

SignalR itself needs a backplane (for example Redis) to fan out across several instances. That is outside Soga V1, so run a single realtime instance or configure the backplane yourself.

## Health

```csharp
app.MapHealthChecks("/health");        // includes the "soga" check
```

`Unhealthy` means the database is unreachable. `Degraded` means the oldest pending outbox event is older than `Outbox:UnhealthyBacklogAge`.

## Telemetry

| Signal | Name |
|---|---|
| Traces | `ActivitySource` `Soga`: `soga.conversation.create`, `.get`, `.list`, `soga.message.send`, `soga.message.history`, `soga.read.advance`, `soga.read.unread`, `soga.sync.execute`, `soga.outbox.dispatch`, `soga.realtime.publish` |
| Metrics | `Meter` `Soga`: `soga.commands` (tags `command`, `outcome`), `soga.outbox.published`, `soga.outbox.failed` (tag `terminal`), `soga.outbox.dispatch.duration`, `soga.realtime.connections` |

```csharp
.WithTracing(t => t.AddSource(SogaDiagnostics.SourceName))
.WithMetrics(m => m.AddMeter(SogaDiagnostics.MeterName))
```

Metric tags are bounded (command names and stable error codes). Message text, tokens, user IDs, and conversation IDs are never used as labels or logged by default.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `InvalidOperationException ... does not include the Soga model` | `modelBuilder.AddSoga()` is missing from `OnModelCreating` |
| `soga.cursor.invalid` after a restart or deploy | Data Protection keys were not persisted; reload state and start `/sync` with no cursor |
| Messages arrive on `/sync` but not live | Client did not call `SubscribeToConversation`, the outbox dispatcher is disabled, or the health check is Degraded |
| `soga.tenant.unavailable` | Your `ISogaTenantContext` returned empty or over 100 characters |
| Hub connects then drops | Authentication failed for the hub request; check how your scheme passes the token for WebSockets |
| `429` without `code` | Add `.UseSogaRejectionResponse()` to `AddRateLimiter` |
| Startup fails with `OptionsValidationException` | Read the listed `Soga:` keys; each failure is named |
