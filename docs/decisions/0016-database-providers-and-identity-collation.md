# 0016: Database providers and case-sensitive identity

- Status: Accepted
- Date: 2026-09-19

## Context

V2 must support more than one production database. Soga compares user, tenant, and idempotency identifiers **ordinally**: `Alice` and `alice` are different users (ADR 0002). Databases do not agree on that.

## Decision

`Soga.EntityFrameworkCore` uses only provider-neutral EF Core features (`ExecuteUpdate`, explicit transactions, unique indexes, UTC `DateTime` conversion), so one store serves every relational provider.

**Support matrix**

| Provider | Status |
|---|---|
| SQLite | Development and tests only (single writer) |
| SQL Server | Supported. The full store suite and the concurrency suite were run against SQL Server 2025 |
| PostgreSQL | Supported. The full store suite and the concurrency suite were run against PostgreSQL 17 |

Both production providers must pass the same suites before a stable release.

**Case-sensitive identity.** PostgreSQL and SQLite compare strings case-sensitively. **SQL Server does not by default**, which would silently make `Alice` and `alice` one user: a confidentiality bug. So:

- `modelBuilder.AddSoga(identityCollation: "Latin1_General_100_BIN2")` applies a case-sensitive collation to every identity column (tenant, user, sender, client message ID, external reference, direct key).
- The same call sets an explicit **ordinal key comparer** on those properties. This is required separately: EF Core's SQL Server provider gives string properties a case-insensitive key comparer regardless of column collation, so EF's own change tracker would otherwise merge `Alice` and `alice` in memory (found by running the suite on SQL Server).
- The store **refuses to start** on SQL Server without a collation (checked at application startup and on first store use), with a message that names the fix.

**Concurrency** relies on one mechanism on every provider: a row lock taken by an `UPDATE` (conversation counter, tenant change counter, group lock) held until commit, plus unique constraints as the backstop with a bounded retry. It does not use `SKIP LOCKED`, sequences, or serializable isolation, so behavior is the same everywhere.

## Alternatives considered

A database-specific migration per provider was rejected (the host owns migrations, ADR 0005). Normalizing identifiers to lower case was rejected: it silently merges distinct host users.

## Consequences

SQL Server hosts must add the collation and include it in their migration. Changing collation on an existing V1 SQL Server database needs a maintenance window (see the migration guide).

## Security and migration implications

`EfIdentityTestsBase` runs on every provider and asserts that users, tenants, idempotency keys, external references, and targeted sync changes are case-sensitive.
