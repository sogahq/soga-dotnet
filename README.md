# Soga

<p align="center">
  <img src="assets/soga-package-icon.png" alt="Soga logo" width="180">
</p>

> Build conversations. Own your infrastructure.

Soga is an open-source, self-hosted messaging framework for ASP.NET Core. It provides the application layer developers need above SignalR, including conversations, persistent messages, authorization, message history, read state, realtime events, and reconnection support.

Soga runs inside the developer's own ASP.NET Core application. It is not a hosted SaaS service. The developer keeps control of their users, database, messages, deployment, and infrastructure.

> **Status:** Soga is currently in planning and early development. Its public API is not stable, and it is not ready for production use.

## Why Soga?

SignalR provides realtime communication, but it does not provide a complete messaging domain. Teams still need to implement message persistence, conversation membership, permissions, duplicate-send protection, history, unread state, and recovery after disconnection.

Soga packages these capabilities into a reusable ASP.NET Core framework.

## Initial focus

The first release will focus on a reliable direct-messaging path:

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

The initial scope includes:

- integration with the host application's authentication;
- direct conversations;
- durable text messages;
- safe retry and duplicate-send protection;
- cursor-based message history;
- realtime delivery through SignalR;
- reconnection and missed-message synchronization;
- read positions and unread counts;
- EF Core persistence;
- security, testing, and diagnostics.

Advanced features such as groups, attachments, typing, presence, reactions, client SDKs, and hosted services will come later.

## Packages

Soga will initially provide:

| Package | Purpose |
|---|---|
| `Soga` | The ASP.NET Core messaging framework, protocol, SignalR integration, and development provider |
| `Soga.EntityFrameworkCore` | Optional relational database persistence using EF Core |

`Soga.Client` will be developed after the backend protocol is stable.

## Planned usage

```bash
dotnet add package Soga
dotnet add package Soga.EntityFrameworkCore
```

```csharp
builder.Services
    .AddSoga(options =>
    {
        options.RoutePrefix = "/soga/v1";
    })
    .UseEntityFrameworkCore<AppDbContext>();

app.MapSoga();
```

Calling `MapSoga()` maps Soga's HTTP API and SignalR integration inside the developer's own application. `/soga/v1` is a proposed configurable default prefix; it is not an external Soga service address.

The exact API may change during development.

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

Project documentation will include getting started, authentication, EF Core setup, conversations, synchronization, security, deployment, API reference, troubleshooting, and upgrades.

Detailed architecture and endpoint specifications are maintained outside this README.

## Contributing

Contribution instructions will be published in `CONTRIBUTING.md`. Major architecture or public API changes should be discussed before implementation and documented through architecture decision records.

## Security

Soga will require authentication by default and will not trust client-supplied sender identities. A private vulnerability-reporting process will be documented in `SECURITY.md` before public release.

Soga V1 will use HTTPS for transport security and will not provide end-to-end encryption.

## Ownership

Soga is created and maintained by Zhuwa Africa Limited.

## License

Soga is licensed under the Apache License 2.0. See `LICENSE` for details.
