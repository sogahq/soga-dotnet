# 0002: Tenancy and identity

- Status: Accepted
- Date: 2026-09-18

## Context

Soga runs inside a host that already owns authentication and users. It must not create accounts, and it must never trust a client-supplied sender or tenant.

## Decision

**Identity.** The acting user comes from `ISogaUserContext`. The default reads the claim named by `Soga:Identity:UserIdClaimType` (default `ClaimTypes.NameIdentifier`) from the authenticated principal. User IDs are opaque strings: trimmed, at most 200 characters, compared **ordinally** (case-sensitive). Soga never queries the host user table and stores no foreign key to it.

**Tenancy.** The active tenant comes from `ISogaTenantContext`. The default returns the configured `DefaultTenantId` (`default`), so a single-tenant host has a documented, safe boundary. Tenant IDs are trimmed and at most 100 characters. The tenant is part of every primary key, unique index, cursor, and SignalR group name, and every store method takes it explicitly so a query cannot omit it.

**SignalR.** The user comes from the connection principal. The tenant resolver runs once per connection in `OnConnectedAsync`, with the connection's own HTTP request flowed into `IHttpContextAccessor` so header-, subdomain-, and route-based resolvers written for HTTP keep working. The result is pinned to the connection.

## Alternatives considered

Accepting the tenant from a header or body was rejected as a cross-tenant access risk. Case-insensitive user IDs were rejected: collation differs per database, and normalizing would silently merge distinct host users.

## Consequences

A host whose user IDs are case-insensitive must normalize them in its claim. A custom `ISogaUserContext` applies to HTTP; SignalR always reads the principal with the configured claim type.

## Security and migration implications

Sender is always derived, never a request field. Cross-tenant access is covered by adversarial integration tests over HTTP and SignalR.
