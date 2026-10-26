# 0010: Protocol versioning

- Status: Accepted
- Date: 2026-09-18

## Context

The wire protocol must be able to evolve independently of the NuGet package version.

## Decision

The protocol version is a single integer, currently `1` (`SogaProtocol.Version`). It appears in three places: the route prefix (`/soga/v1`), the `protocolVersion` field of every event, and the SignalR method `NegotiateProtocol(int clientVersion)`, which returns the server version or fails with `soga.protocol.mismatch`.

Within a version, changes are additive only: new optional fields, new event types, new endpoints. A client must ignore fields and event types it does not know. Removing or repurposing a field, event name, or error code requires a new version. Error codes and event names are never reused with different meaning.

Canonical JSON fixtures in `ProtocolFixtureTests` pin the exact shapes; changing one is a protocol change and needs review.

## Alternatives considered

Header-based negotiation was rejected as harder to test and to route. Semantic versions for the protocol were rejected as more than V1 needs.

## Consequences

The prefix is configurable, but the version segment is a documented default, not a promise that a host renamed it. Only exact version match is accepted until a second version exists.

## Security and migration implications

None.
