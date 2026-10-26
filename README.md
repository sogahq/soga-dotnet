# Soga

<p align="center">
  <img src="assets/soga-package-icon.png" alt="Soga logo" width="180">
</p>

> Build conversations. Own your infrastructure.

Soga is an open-source, self-hosted messaging framework for ASP.NET Core. It provides the application layer developers need above SignalR, including conversations, persistent messages, authorization, message history, read state, realtime events, and reconnection support.

Soga runs inside the developer's own ASP.NET Core application. It is not a hosted SaaS service. The developer keeps control of their users, database, messages, deployment, and infrastructure.

> **Status:** Soga is in preview (`0.1.0-preview.1`). The focused V1 direct-messaging path is implemented and tested, but the public API, protocol, and database schema are not stable and it is not yet recommended for production use.

## Why Soga?

SignalR provides realtime communication, but it does not provide a complete messaging domain. Teams still need to implement message persistence, conversation membership, permissions, duplicate-send protection, history, unread state, and recovery after disconnection.

Soga packages these capabilities into a reusable ASP.NET Core framework.

## What V1 does

```text
Authenticate
→ create or open a direct conversation
→ save a message
→ publish a realtime event
→ disconnect
→ reconnect
→ recover missed messages
→ mark the conversation as read
```

- integration with the host application's authentication and optional tenancy;
- direct conversations, with an optional external reference such as an order ID;
- durable text messages with safe retry (idempotency);
- deterministic per-conversation ordering and cursor-based history;
- realtime delivery through SignalR, published from a transactional outbox;
- reconnection and missed-message synchronization;
- read positions and unread counts;
- EF Core persistence, health checks, logging, tracing, and metrics.

Advanced features such as groups, attachments, typing, presence, reactions, client SDKs, and hosted services will come later.

## Packages

| Package | Purpose |
|---|---|
| `Soga` | The ASP.NET Core messaging framework, protocol, SignalR integration, and in-memory development provider |
| `Soga.EntityFrameworkCore` | Relational database persistence using EF Core |

`Soga.Client` will be developed after the backend protocol is stable.

## Usage

```bash
dotnet add package Soga
dotnet add package Soga.EntityFrameworkCore
```

```csharp
builder.Services
    .AddSoga(options =>
    {
        options.RoutePrefix = "/soga/v1";
        options.Messages.MaximumTextLength = 8_000;
    })
    .UseEntityFrameworkCore<AppDbContext>();

app.MapSoga();
```

```csharp
// In your DbContext:
protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSoga();
```

Calling `MapSoga()` maps Soga's HTTP API and SignalR hub inside the developer's own application. `/soga/v1` is a configurable default prefix; it is not an external Soga service address. Your application owns the database migrations.

See [Getting started](docs/getting-started/README.md) for the full walkthrough, and [Concepts](docs/concepts/README.md) for the HTTP API, events, and error codes.

## Build and test

```bash
dotnet restore
dotnet build -warnaserror
dotnet test
dotnet pack src/Soga/Soga.csproj -c Release -o artifacts/packages
dotnet pack src/Soga.EntityFrameworkCore/Soga.EntityFrameworkCore.csproj -c Release -o artifacts/packages
```

```bash
dotnet run --project samples/Soga.MinimalSample
dotnet run --project samples/Soga.MarketplaceSample
```

## Design principles

- The database is the source of truth.
- Messages are stored before realtime publication.
- SignalR is transport, not message storage.
- The authenticated user is determined by the host application.
- Every protected operation is authorized.
- Message sends are safe to retry.
- Disconnected clients can recover missed durable messages.
- Public contracts are separate from persistence entities.
- Single-server correctness comes before distributed scale-out.

## Documentation

- [Getting started](docs/getting-started/README.md)
- [Concepts, HTTP API, events, and errors](docs/concepts/README.md)
- [Security and threat model](docs/security/README.md)
- [Deployment and operations](docs/deployment/README.md)
- [Architecture decisions](docs/decisions/)
- [Architecture and engineering guide](SOGA_ARCHITECTURE_AND_ENGINEERING_GUIDE.md)

## Contributing

Read `CONTRIBUTING.md`. Major architecture or public API changes should be discussed before implementation and documented through architecture decision records.

## Security

Soga requires authentication by default and does not trust client-supplied sender identities. Report vulnerabilities privately as described in `SECURITY.md`.

Soga V1 uses HTTPS for transport security and does not provide end-to-end encryption.

## Ownership

Soga is created and maintained by Zhuwa Africa Limited.

## License

Soga is licensed under the Apache License 2.0. See `LICENSE` for details.
