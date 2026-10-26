# Documentation

The architecture guide at the repository root is the authoritative engineering direction. This tree documents what is implemented (V1 direct messaging and V2 groups, message lifecycle, host extension points, typing, presence, and multi-provider persistence).

| Page | What it covers |
|---|---|
| [Getting started](getting-started/README.md) | Install, configure, groups, edit and delete, realtime, sync, host rules |
| [Concepts](concepts/README.md) | Guarantees, conversations, roles, messages, sync, events, HTTP API, error codes |
| [Security](security/README.md) | Trust model, threat model with tests, known limits |
| [Deployment](deployment/README.md) | Production checklist, database support, configuration, operations, troubleshooting |
| [V1 to V2 migration](migration/v1-to-v2.md) | Protocol, schema, and SQL Server collation changes |
| [Decisions](decisions/) | Architecture decision records (0001 to 0017) |

Soga is in preview (`0.x`). The public API, protocol, and database schema may change until `1.0`.
