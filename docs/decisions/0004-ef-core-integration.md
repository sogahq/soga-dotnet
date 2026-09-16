# 0004: EF Core integration

- Status: Accepted
- Date: 2026-09-16

## Context

Soga must integrate with existing ASP.NET Core applications, and Soga changes may need to share transactions with host-domain changes.

## Decision

`Soga.EntityFrameworkCore` exposes an `AddSoga` model-builder extension. A host calls it from its selected `DbContext.OnModelCreating` method and owns migrations.

## Alternatives considered

A mandatory dedicated context was rejected because cross-context transactions complicate integration. Runtime model discovery was rejected because it obscures registration.

## Consequences

Hosts explicitly register mappings and include Soga changes in their normal migration workflow. Schema changes must be documented with upgrades.

## Security and migration implications

Tenant identifiers participate in keys and constraints. Hosts must review and apply migrations; Soga never silently migrates a production database.
