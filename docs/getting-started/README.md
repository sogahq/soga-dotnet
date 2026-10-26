# Getting started

You need an ASP.NET Core application on .NET 10 that already authenticates users. Soga adds messaging to it and never creates accounts.

## 1. Install

```bash
dotnet add package Soga
dotnet add package Soga.EntityFrameworkCore
```

## 2. Register

```csharp
builder.Services
    .AddSoga(options =>
    {
        options.RoutePrefix = "/soga/v1";
        options.Identity.UserIdClaimType = ClaimTypes.NameIdentifier;
        options.Messages.MaximumTextLength = 8_000;
        options.Groups.MaximumParticipants = 256;
    })
    .UseEntityFrameworkCore<AppDbContext>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();               // HTTP API and SignalR hub, all under the route prefix
app.MapHealthChecks("/health");
```

`MapSoga()` runs inside your application. `/soga/v1` is a configurable prefix, not an external service.

Without `.UseEntityFrameworkCore`, Soga uses an in-memory provider that is only for samples and development. It logs a warning at startup and loses everything on restart.

## 3. Add the tables

Your `DbContext` owns the schema. Add Soga's mappings and create a migration:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.AddSoga();   // PostgreSQL, SQLite

    // SQL Server is case-insensitive by default, which would treat users "Alice" and "alice" as the same person.
    // Soga refuses to start without a case-sensitive collation:
    // modelBuilder.AddSoga(identityCollation: "Latin1_General_100_BIN2");
}
```

```bash
dotnet ef migrations add AddSoga
dotnet ef database update
```

Soga never migrates your database for you. Upgrading from V1? See the [migration guide](../migration/v1-to-v2.md).

## 4. Send a message

```bash
# Open (or fetch) the conversation between alice and bob.
POST /soga/v1/conversations/direct
{ "otherUserId": "bob", "externalReference": "order:1042" }     # reference is optional

# Send. clientMessageId is your idempotency key: retrying it is always safe.
POST /soga/v1/conversations/{id}/messages
{ "clientMessageId": "7f3c...", "text": "Is this still available?" }
```

The sender is the authenticated user. The request cannot choose it.

## 5. Groups

```bash
# The caller becomes the owner.
POST /soga/v1/conversations/groups
{ "title": "Launch team", "participantUserIds": ["bob", "carol"] }

POST   /soga/v1/conversations/{id}/participants            { "userId": "dave", "role": "admin" }
PUT    /soga/v1/conversations/{id}/participants/bob/role   { "role": "admin" }
DELETE /soga/v1/conversations/{id}/participants/carol      # remove; DELETE your own ID to leave
PATCH  /soga/v1/conversations/{id}                         { "title": "Launch crew" }
POST   /soga/v1/conversations/{id}/archive
```

Owners and admins manage the group; only owners change roles. See [Concepts](../concepts/README.md) for the full role table.

## 6. Edit, delete, and reply

```bash
PATCH  /soga/v1/conversations/{id}/messages/{messageId}   { "text": "corrected text" }
DELETE /soga/v1/conversations/{id}/messages/{messageId}
POST   /soga/v1/conversations/{id}/messages               { "clientMessageId": "...", "text": "agreed", "replyToMessageId": "..." }
```

## 7. Receive in realtime

```js
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/soga/v1/hub", { accessTokenFactory: () => token })
  .withAutomaticReconnect()
  .build();

for (const type of ["MessageCreated", "MessageUpdated", "MessageDeleted", "ParticipantChanged",
                    "ConversationCreated", "ConversationUpdated", "ConversationArchived",
                    "ReadPositionChanged", "TypingChanged", "PresenceChanged"]) {
  connection.on(type, evt => { /* dedupe by evt.eventId, order by evt.sequence */ });
}

await connection.start();
await connection.invoke("NegotiateProtocol", 1);     // wait for it: the connection is now fully set up
await connection.invoke("SubscribeToAll");           // or SubscribeToConversation(id) per conversation

// Typing: call while the user types (repeat every ~2 seconds). It expires on its own.
await connection.invoke("SetTyping", conversationId, true);
const online = await connection.invoke("GetPresence", conversationId);
```

Call `SubscribeToAll` (or `SubscribeToConversation`) again after every reconnect. It re-checks access and restores only what you are still allowed to see. If you are removed from a conversation you receive a `ParticipantChanged` event with `change: "removed"` and stop receiving its events.

## 8. Recover after a disconnect

Realtime is a notification, not the record. Keep a sync cursor and use it after reconnecting:

```text
1. GET /soga/v1/sync                 -> { events: [], cursor: "A", hasMore: false }   (first start)
2. GET /soga/v1/conversations ...    load your screens over HTTP
3. ... disconnected, things happen ...
4. GET /soga/v1/sync?cursor=A        -> { events: [...], cursor: "B", hasMore: false }
5. Apply events, dedupe by eventId, store "B". While hasMore is true, call again with the new cursor.
```

Edits, deletions, and membership changes are all in sync. Typing and presence are not.

## 9. Add your own rules

```csharp
// "Only the buyer and seller of an order may talk about it." A policy can only deny.
builder.Services.AddScoped<ISogaAuthorizationPolicy, OrderConversationPolicy>();
builder.Services.AddSingleton<ISogaMessageValidator, NoLinksValidator>();   // reject a message with a reason code
builder.Services.AddSingleton<ISogaAuditSink, MyAuditSink>();               // privileged operations, never message text
```

The samples show each one working.

## Try the samples

```bash
dotnet run --project samples/Soga.MinimalSample          # in-memory, header auth
dotnet run --project samples/Soga.TeamChatSample         # groups, roles, audit, system messages, host rules
dotnet run --project samples/Soga.MarketplaceSample      # EF Core + SQLite, one thread per order, order policy

curl -H "X-Soga-User: alice" http://localhost:5272/soga/v1/context
```

The samples trust an `X-Soga-User` header to keep them short. Never do that in production.

## Build and test

```bash
dotnet restore
dotnet build -warnaserror
dotnet test
```

The SQL Server and PostgreSQL suites are skipped unless you point them at a server that may create databases:

```bash
export SOGA_TEST_SQLSERVER="Server=localhost;Integrated Security=true;TrustServerCertificate=true"
export SOGA_TEST_POSTGRES="Host=localhost;Username=postgres;Password=..."
dotnet test tests/Soga.EntityFrameworkCore.Tests
```

```bash
dotnet pack src/Soga/Soga.csproj -c Release -o artifacts/packages
dotnet pack src/Soga.EntityFrameworkCore/Soga.EntityFrameworkCore.csproj -c Release -o artifacts/packages
```
