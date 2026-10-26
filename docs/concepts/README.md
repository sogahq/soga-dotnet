# Concepts

## The guarantees

1. Only authorized users can access a conversation. Access is checked for every operation, over HTTP and SignalR alike, and roles are enforced in one place.
2. An accepted message or change is durable. It is stored, together with its sync change and outbox event, in one transaction before anything is announced.
3. A retry does not create a duplicate.
4. Messages in a conversation have a deterministic order that editing and deleting never change.
5. A disconnected client can recover everything it missed that it is still allowed to see.
6. Read state stays correct.
7. A removed participant loses access immediately, on every device.

Realtime delivery means an event was *offered* to an active connection. It does not mean the recipient read it, and it is never exactly once: clients deduplicate by `eventId`. Typing and presence are ephemeral and advisory: never stored, never recovered by sync, never used for authorization.

## Conversations

**Direct.** One conversation per unordered pair of users and optional `externalReference`. `alice→bob` and `bob→alice` are the same conversation. `order:1` and `order:2` between the same users are separate. Opening one twice returns the same conversation (`201` then `200`). See ADR 0007.

**Group.** Created with a title; the creator is the owner. Up to `Groups:MaximumParticipants` active participants (default 256). Creating a group is not idempotent: do not blindly retry it. See ADR 0012.

**Archived** conversations stay readable but reject new messages (`soga.conversation.archived`). Owners and admins can archive and restore groups.

**Metadata** is a small flat JSON object (strings, numbers, booleans; at most 20 properties and 4096 bytes by default). It is returned when you fetch a conversation and is never sent inside events (ADR 0013).

A user who is not a participant, a conversation that does not exist, and a conversation in another tenant all answer with the identical `404 soga.conversation.not_found`, so private conversations cannot be enumerated.

## Roles

| Action | owner | admin | member |
|---|---|---|---|
| Send, read, edit and delete own messages, leave | yes | yes | yes |
| Add a member | yes | yes | no |
| Grant `admin` or `owner` | yes | no | no |
| Remove a member | yes | yes | no |
| Remove an admin or owner | yes | no | no |
| Change roles | yes | no | no |
| Rename, edit metadata, archive, restore | yes | yes | no |
| Delete others' messages (audited) | yes | yes | no |

A group always keeps at least one owner (`soga.group.last_owner`). To hand over ownership, promote someone to `owner` first. Repeating add, remove, role, update, and archive commands is safe: they change nothing the second time. Host policies can restrict any of this further but never grant more (ADR 0014).

A former participant has no access to the conversation or its history, including changes before they were removed. Through sync they receive exactly one thing: the notice that they were removed. New members can read the full history but start with nothing unread.

## Messages

`clientMessageId` is required. Sending the same key with the same text returns the original message (`200`). The same key with different text returns `409 soga.message.idempotency_conflict`. Keys are scoped per sender and conversation (ADR 0011).

Content is plain text, at most `Messages:MaximumTextLength` UTF-16 units (default 8000, else `422 soga.message.too_long`). It is untrusted: encode it when rendering.

**Reply.** Add `replyToMessageId`. The target must exist in the same conversation and not be deleted (`422 soga.reply.invalid`). The response carries `replyTo { messageId, senderId, snippet, deleted }`, a bounded summary, not the whole message.

**Edit.** `PATCH .../messages/{id}` with `{ "text": ... }`. Only the sender, only text messages, only within `Messages:EditWindow` (default 15 minutes). The message keeps its ID, sender, sequence, and `createdAt`; `editedAt` is set and a `MessageUpdated` event is published. Editing to the same text does nothing.

**Delete.** `DELETE .../messages/{id}`. The sender may delete their own; in a group an owner or admin may delete others' (audited). The text is erased and the message stays as an empty tombstone with `deleted: true`, so sequences stay contiguous. Deleting twice succeeds. Deleted messages are not counted as unread.

**System messages** (`kind: "System"`) are posted by trusted server code (`ISogaSystemMessages`) or, if enabled, automatically for membership changes as machine-readable JSON such as `{"code":"participant_added","actor":"alice","target":"carol"}`. Participants cannot create, edit, or delete them.

## Ordering and history

Every message has a per-conversation `sequence` (1, 2, 3, ..., no gaps). Order by it, never by timestamp or ID.

`GET /conversations/{id}/messages?limit=&cursor=` returns newest first, including tombstones. `nextCursor` is opaque and points to a position, not a page number, so new messages between pages never shift them. It is `null` when history is exhausted. `limit` above the maximum is clamped; below 1 is an error.

## Conversation list

`GET /conversations?limit=&cursor=&kind=&state=&externalReference=` returns `{ items, nextCursor }`, most recent activity first. `kind` is `direct` or `group`; `state` is `active` (default), `archived`, or `all`. Each item has `unreadCount`, your `myRole`, `participantCount`, and a bounded `lastMessage` preview. For groups the list omits the participant list (fetch the conversation for it). A cursor is bound to your user and to the filters you used, and paging is stable even when many conversations share the same activity time.

## Read state and unread counts

Each participant has one `lastReadSequence`. `POST /conversations/{id}/read { "messageId": ... }` advances it through that message. It never moves backward. Unread = messages from *others* after `lastReadSequence`, excluding deleted ones. `GET /unread` sums it across conversations.

## Synchronization

See the recovery steps in [Getting started](../getting-started/README.md). Points to remember:

- A cursor is a position, integrity-protected, and bound to the user and tenant. Invalid or replayed cursors return `soga.cursor.invalid`; then start over with no cursor and reload state.
- The returned cursor is the **page boundary**. Keep calling while `hasMore` is true.
- Sync re-checks access each time. Edits, deletions, and membership changes are all durable events.
- Sync and realtime deliver the same `eventId` for the same change. Typing and presence are never in sync.
- Realtime is an optimization. If it fails, sync still has the change (ADR 0008).

## Events

Every event has `eventId`, `protocolVersion`, `type`, `occurredAt`, `conversationId`, `sequence` (when relevant), and `data`. Clients must ignore event types and fields they do not know.

| Type | Durable | `data` |
|---|---|---|
| `MessageCreated` | yes | `{ message }` |
| `MessageUpdated` | yes | `{ message }` (same sequence) |
| `MessageDeleted` | yes | `{ messageId, deletedBy, deletedAt }` |
| `ReadPositionChanged` | yes | `{ userId, lastReadSequence }` |
| `ConversationCreated` | yes | `{ participantUserIds, externalReference, createdAt, kind, title }` |
| `ConversationUpdated` | yes | `{ title, updatedBy, metadataChanged }` |
| `ConversationArchived` | yes | `{ archived, actorUserId }` |
| `ParticipantChanged` | yes | `{ userId, change, role, actorUserId }`; `change` is `added`, `removed`, `left`, or `role_changed` |
| `TypingChanged` | no | `{ userId, typing }` |
| `PresenceChanged` | no | `{ userId, online, lastSeenAt }` |

Who receives what: message, read, update, archive, typing, and presence events go to subscribers of the conversation. `ConversationCreated` goes to each participant. `ParticipantChanged` goes to the conversation and to the affected user, once per connection.

## HTTP API

All routes are under the route prefix (default `/soga/v1`) and require authentication.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/conversations/direct` | Create or return a direct conversation |
| `POST` | `/conversations/groups` | Create a group |
| `GET` | `/conversations` | List with filters and cursor paging |
| `GET` | `/conversations/{id}` | Get one conversation with participants and roles |
| `PATCH` | `/conversations/{id}` | Change title and/or metadata (owner, admin) |
| `POST` | `/conversations/{id}/archive` and `/unarchive` | Archive or restore (owner, admin) |
| `POST` | `/conversations/{id}/participants` | Add a participant |
| `DELETE` | `/conversations/{id}/participants/{userId}` | Remove, or leave when it is you (`204` if you lost access) |
| `PUT` | `/conversations/{id}/participants/{userId}/role` | Change a role (owner) |
| `GET` | `/conversations/{id}/messages` | Cursor-paginated history |
| `POST` | `/conversations/{id}/messages` | Send an idempotent message, optionally a reply |
| `PATCH` | `/conversations/{id}/messages/{messageId}` | Edit your message |
| `DELETE` | `/conversations/{id}/messages/{messageId}` | Delete a message |
| `POST` | `/conversations/{id}/read` | Advance the read position |
| `GET` | `/unread` | Total unread count |
| `GET` | `/sync` | Durable changes after a cursor |
| `GET` | `/context` | The trusted user and tenant Soga sees |
| `*` | `/hub` | SignalR hub |

User IDs in routes must be URL-encoded.

**Hub methods:** `NegotiateProtocol(int)`, `SubscribeToConversation(Guid)`, `SubscribeToAll()` (returns the IDs it restored), `UnsubscribeFromConversation(Guid)`, `SetTyping(Guid, bool)`, `GetPresence(Guid)`. Call `NegotiateProtocol` first: the server answers it only after the connection is fully set up, so nothing sent to you can be missed.

## Errors

Errors are `application/problem+json` with a stable `code`. Branch on `code`, never on the English text. Clients must ignore codes they do not know.

| Code | Status | Meaning |
|---|---|---|
| `soga.auth.unauthorized` | 401 | No authenticated user |
| `soga.tenant.unavailable` | 403 | Tenant could not be resolved |
| `soga.authorization.denied` | 403 | Your role or a host policy does not allow it |
| `soga.conversation.not_found` | 404 | Missing, inaccessible, or wrong tenant |
| `soga.conversation.not_group` | 409 | The operation needs a group |
| `soga.conversation.archived` | 409 | Archived conversations take no new messages |
| `soga.group.disabled` | 403 | Groups are turned off |
| `soga.group.too_large` | 422 | The group is at its size limit |
| `soga.group.last_owner` | 409 | A group must keep an owner |
| `soga.participant.not_found` | 404 | That user was never a participant |
| `soga.message.not_found` | 404 | Message is not in the conversation |
| `soga.message.too_long` | 422 | Text over the limit |
| `soga.message.idempotency_conflict` | 409 | Key reused for different text |
| `soga.message.not_editable` | 409 | System or deleted message, or editing is off |
| `soga.message.edit_window_expired` | 409 | Past the edit window |
| `soga.message.not_deletable` | 409 | System message |
| `soga.message.rejected` | 422 | A host validator refused it; `detail` is its reason code |
| `soga.reply.invalid` | 422 | Reply target missing, deleted, or elsewhere |
| `soga.metadata.invalid` | 422 | Metadata breaks a limit or shape rule |
| `soga.cursor.invalid` | 400 | Bad, tampered, or replayed cursor |
| `soga.request.invalid` | 400 / 413 | Malformed or invalid request; oversized body |
| `soga.rate_limited` | 429 | Rate limit exceeded (needs `UseSogaRejectionResponse`) |
| `soga.protocol.mismatch` | (hub) | Unsupported protocol version |
| `soga.internal_error` | 500 | Unexpected failure; details are only in server logs |
| `soga.conversation.not_participant` | (reserved) | Not returned |

A non-JSON content type is answered by ASP.NET Core itself with a plain `415`.
