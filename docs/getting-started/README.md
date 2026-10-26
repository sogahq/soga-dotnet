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
    modelBuilder.AddSoga();   // optional: AddSoga(schema: "messaging", tablePrefix: "Chat")
}
```

```bash
dotnet ef migrations add AddSoga
dotnet ef database update
```

Soga never migrates your database for you.

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

## 5. Receive in realtime

```js
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/soga/v1/hub", { accessTokenFactory: () => token })
  .withAutomaticReconnect()
  .build();

connection.on("MessageCreated", evt => { /* dedupe by evt.eventId, order by evt.sequence */ });
connection.on("ReadPositionChanged", evt => { });
connection.on("ConversationCreated", evt => { });

await connection.start();
await connection.invoke("NegotiateProtocol", 1);
await connection.invoke("SubscribeToConversation", conversationId);
```

Call `SubscribeToConversation` again after every reconnect. Subscribing re-checks access.

## 6. Recover after a disconnect

Realtime is a notification, not the record. Keep a sync cursor and use it after reconnecting:

```text
1. GET /soga/v1/sync                 -> { events: [], cursor: "A", hasMore: false }   (first start)
2. GET /soga/v1/conversations ...    load your screens over HTTP
3. ... disconnected, messages arrive ...
4. GET /soga/v1/sync?cursor=A        -> { events: [...], cursor: "B", hasMore: false }
5. Apply events, dedupe by eventId, store "B". While hasMore is true, call again with the new cursor.
```

## Try the samples

```bash
dotnet run --project samples/Soga.MinimalSample          # in-memory, header auth
dotnet run --project samples/Soga.MarketplaceSample      # EF Core + SQLite, order-scoped threads

curl -H "X-Soga-User: alice" http://localhost:5272/soga/v1/context
```

The samples trust an `X-Soga-User` header to keep them short. Never do that in production.

## Build and test

```bash
dotnet restore
dotnet build -warnaserror
dotnet test
dotnet pack src/Soga/Soga.csproj -c Release -o artifacts/packages
dotnet pack src/Soga.EntityFrameworkCore/Soga.EntityFrameworkCore.csproj -c Release -o artifacts/packages
```
