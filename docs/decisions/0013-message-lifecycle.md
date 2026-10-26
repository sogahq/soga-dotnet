# 0013: Message lifecycle: edit, delete, replies, system messages, metadata

- Status: Accepted
- Date: 2026-09-19

## Context

V2 lets messages be edited and deleted, replied to, and posted by the system, and lets conversations carry host metadata, without weakening ordering, durability, or privacy.

## Decision

**Identity never changes.** Editing and deleting keep the message ID, sender, conversation, and sequence. Both publish durable events (`MessageUpdated`, `MessageDeleted`) with the message's existing sequence, so sync and clients reconcile by ID.

**Edit.** Only the sender, only text messages, only while not deleted, and only within `Messages:EditWindow` (default 15 minutes, `null` for no limit). `CreatedAt` never changes; `EditedAt` is set. Editing to the same text is a no-op that publishes nothing. With `Messages:RetainEditHistory`, the previous text is kept in `SogaMessageEdits`.

**Delete (soft).** The sender may delete their own text message. In a group an owner or admin may delete others' text messages when `Messages:AllowAdminDeletion` is on (default); every such deletion is audited (`message.deleted_by_admin`). The **text is erased** from the row and from any retained edit history, and the row stays as an empty tombstone (`deleted: true`), so sequences stay contiguous and replies can show "deleted". Deleted messages do not count as unread. Deleting again succeeds. Permanent erasure and legal retention remain host responsibilities.

**Replies.** A message may reference a message in the same conversation that exists and is not deleted. Replies carry a bounded summary (sender and the first 100 characters), computed on read, never a recursive embed. If the original is deleted later the summary shows `deleted: true` with no text. There are no threads.

**System messages.** Kind `System`, sent by the reserved sender `$soga:system`. They are created only by trusted server code: `ISogaSystemMessages` and, when `Groups:PostSystemMessages` is on, membership changes (as machine-readable JSON such as `{"code":"participant_added","actor":"a","target":"b"}`, which clients localize). A participant can never send as the system: the sender always comes from the authenticated principal, and edit and delete refuse system messages. They bypass the participant check (the system is not a participant) and may be posted to an archived conversation.

**Archived conversations** stay readable but reject new participant messages with `soga.conversation.archived`.

**Metadata.** A flat JSON object: string (up to 500 characters), number, or boolean values; keys of 1 to 50 characters (`A-Z a-z 0-9 . _ -`); at most 20 properties and 4096 bytes by default (hard cap 16 KB). No nesting and no null. Stored canonically (sorted keys) so equal documents compare equal. **Metadata is never included in events**; `ConversationUpdated` only says that it changed, so it cannot leak to a connection through realtime. Direct conversations take metadata only at creation.

## Alternatives considered

Hard delete was rejected: it breaks sequences and replies and hides deletion from sync. Keeping deleted text "for audit" by default was rejected as unnecessary exposure. Nested metadata was rejected: unbounded structure is hard to limit and compare.

## Consequences

Clients must render `deleted` and `editedAt` and must treat unknown message fields and kinds as ignorable.

## Security and migration implications

Edit and delete ownership bypass, system-message impersonation, and cross-conversation replies are each covered by negative tests through HTTP.
