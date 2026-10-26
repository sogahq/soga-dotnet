# 0005: Migration ownership

- Status: Accepted
- Date: 2026-09-18

## Context

Soga tables live in the host's database (ADR 0004). Silent schema changes to a production database are unacceptable.

## Decision

The host owns migrations. `modelBuilder.AddSoga(schema, tablePrefix)` adds the mappings; the host runs `dotnet ef migrations add` and applies the result through its normal deployment process. Soga never calls `Migrate` or `EnsureCreated`. (The samples call `EnsureCreated` only because they are samples.)

`schema` and `tablePrefix` (default `Soga`) make table naming configurable. Soga has no foreign key to any host table.

If the context does not include the Soga model, the store fails on first use with a message telling the host to call `AddSoga`.

## Alternatives considered

A Soga-owned migration history was rejected because it cannot share a transaction or a connection with host changes. Automatic migration at startup was rejected as unsafe for production.

## Consequences

Every Soga release that changes the schema must say so in its release notes and provide the upgrade steps. **During preview (`0.x`) the schema may change without a migration path.** A stable schema is promised only from `1.0`.

## Security and migration implications

Tenant IDs participate in every key. Hosts must review generated migrations before applying them.
