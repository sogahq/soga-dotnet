# Migrating from V1 to V2

V1 (`0.1.0-preview.1`) was a preview and shipped without migrations, so you own the schema (ADR 0005). This guide lists exactly what changed. **The upgrade path has not been exercised by an automated test against a real V1 database.** Read it, try it on a copy of your data first, and keep a backup.

## 1. Protocol changes clients must handle

- **`GET /conversations` now returns `{ "items": [...], "nextCursor": "..." | null }`** instead of a bare array. This is the only breaking change (ADR 0017). Update every client that lists conversations.
- New optional fields appear on conversations, messages, and `ConversationCreated`. Clients must ignore fields they do not know.
- New event types and hub methods exist. Clients must ignore event types they do not know.
- Deleted messages appear as tombstones (`deleted: true`, empty `content`) in history. Render them or hide them, but do not treat them as errors.
- `ConversationResponse.participantUserIds` is empty for groups in list results; use `participantCount` or fetch the conversation.
- The error code `soga.authentication.required` was already replaced by `soga.auth.unauthorized` in V1 preview 1.

## 2. Code changes

- `AddSoga(...)` still returns a `SogaBuilder`. Nothing to change.
- `modelBuilder.AddSoga(...)` gained an optional `identityCollation` (required on SQL Server, see below).
- New options groups: `Groups`, `Typing`, `Presence`, plus new `Messages` and `Conversations` options. Defaults match V1 behavior for direct messaging.
- Existing V1 direct-message endpoints behave as before.

## 3. Schema changes

Generate the migration with `dotnet ef migrations add SogaV2` and review it. It will contain:

| Table | Change |
|---|---|
| `SogaConversations` | new `Kind` (not null), `Title`, `MetadataJson`, `ArchivedAt`; new index on `(TenantId, ExternalReference)` |
| `SogaParticipants` | new `Role` (not null, stored as text) |
| `SogaMessages` | new `EditedAt`, `DeletedAt`, `DeletedBy`, `ReplyToMessageId`, `ContentFingerprint` (all nullable; the fingerprint is at most 64 characters) |
| `SogaChanges` | new `TargetUserId` (nullable) and an index on `(TenantId, TargetUserId, Sequence)` |
| `SogaMessageEdits` | **new table** (edit history, only written when `Messages:RetainEditHistory` is on) |

## 4. Backfill existing rows

EF adds the new not-null text columns with an empty default. Give every V1 row its correct value:

```sql
UPDATE SogaConversations SET Kind = 'Direct' WHERE Kind = '';
UPDATE SogaParticipants  SET Role = 'Member' WHERE Role = '';
```

Put these in the migration's `Up` method after the columns are added, so the change is applied as one step. Every V1 conversation is a direct conversation and every V1 participant is an ordinary member. `ContentFingerprint` needs no backfill: V1 messages keep it null and their retries are compared with their current text, exactly as in V1. Nothing else needs data changes: `DirectKey`, sequences, read positions, messages, changes, and outbox rows are compatible.

## 5. SQL Server only: the identity collation

V1 on SQL Server used the database default (case-insensitive) collation for user and tenant IDs, so `Alice` and `alice` were already indistinguishable to it. V2 refuses to start on SQL Server until identity columns have a case-sensitive collation (ADR 0016):

```csharp
modelBuilder.AddSoga(identityCollation: "Latin1_General_100_BIN2");
```

Before you switch:

1. **Look for collisions.** If two rows differ only by case in `UserId` or `TenantId` (for example `Alice` and `alice`), they were treated as one user by V1. Decide which is right and fix the data first:
   ```sql
   SELECT LOWER(UserId), COUNT(DISTINCT UserId) FROM SogaParticipants GROUP BY LOWER(UserId) HAVING COUNT(DISTINCT UserId) > 1;
   ```
2. **Plan a maintenance window.** Changing a column's collation means dropping and recreating the keys and indexes that contain it. The generated migration may need editing to do this in the right order. Test on a copy.
3. Make sure your identity provider already emits the exact case you store, since matching is now exact.

## 5a. All providers: reserved user IDs

User IDs starting with `$soga:` are now reserved. If your identity provider can issue such IDs, those users are refused (`401`) after the upgrade. This is very unlikely, but check before you deploy.

PostgreSQL and SQLite hosts need no collation change.

## 6. Rollout order

1. Back up the database.
2. Deploy the migration (schema and backfill) with the V1 application still running, if you edited it to be additive; otherwise use a short maintenance window.
3. Deploy the V2 application.
4. Update clients for the new conversation-list shape.
5. Run the store suites against a copy of production if you can.

Rollback: restore the backup. V1 does not understand the new columns' meaning but ignores them, so an application rollback alone is safe only until someone creates a group; after that, restore data as well.
