# 0003: HTTP endpoint style

- Status: Accepted
- Date: 2026-09-18

## Context

Soga maps its own routes into the host with `MapSoga()`. Errors must be stable and machine-readable, and behavior must not depend on the presentation style.

## Decision

Use Minimal APIs. Handlers are thin: they resolve the trusted caller, call one application workflow, and map the result to Problem Details.

Request bodies are read by Soga, not by model binding, and are bounded by `Soga:Security:MaximumRequestBodyBytes`. Malformed JSON, a missing body, and an oversized body therefore return Problem Details with the stable code `soga.request.invalid` (413 for oversized) instead of a framework error without a code. Query values such as `limit` are parsed by hand for the same reason.

Every error is `application/problem+json` with `type` `urn:soga:error:<code>`, a `code` extension, and a `traceId` extension.

## Alternatives considered

MVC controllers were rejected as heavier and no better at stable errors. Model binding was rejected because binding failures bypass endpoint filters.

## Consequences

The endpoints declare `Accepts("application/json")`, so ASP.NET Core itself answers a non-JSON content type with a plain 415 that carries no Soga code. That is standard HTTP and is documented.

## Security and migration implications

Unexpected exceptions are logged with correlation data and returned as a generic `soga.internal_error` with no details.
