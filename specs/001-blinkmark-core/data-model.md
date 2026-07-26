# Phase 1 Data Model: BlinkMark Core

**Feature**: `001-blinkmark-core` | **Date**: 2026-07-26 | **Depends on**: [research.md](research.md)

Storage is split deliberately. Cosmos DB holds the mutable working set. Blob storage holds file
bytes and render artifacts. Table storage holds the immutable audit trail. Redis holds transient
coordination state that must never be persisted.

| Store | Holds | Lifetime |
|---|---|---|
| Cosmos DB (serverless) | File, Comment, Notification, UserPreferences | Until TTL, ≤30 days |
| Blob Storage (HNS) | Original upload, sanitized render, text projection | Until blob expiry, ≤30 days |
| Table Storage | AuditEntry | ≥1 year, outlives everything else |
| Redis | PresenceSession, rate/quota counters | Seconds to minutes, never persisted |

---

## Cosmos: `files` container

**Partition key**: `/id` — point read on the preview hot path (SC-002).

| Field | Type | Notes |
|---|---|---|
| `id` | string (ULID) | System-generated. Also the blob name. Never derived from the upload filename (FR-008). |
| `displayName` | string | Original filename, display only (FR-009). Sanitized for rendering; never used as a path. |
| `contentType` | enum | `html` \| `markdown` |
| `sizeBytes` | number | ≤ 10 MB (FR-007) |
| `ownerId` | string | Entra object ID. Indexed for the owner's file list (FR-011). |
| `ownerDisplayName` | string | Denormalized to avoid a directory lookup per list row. |
| `uploadedAt` | string (ISO 8601 UTC) | Immutable. |
| `expiresAt` | string (ISO 8601 UTC) | Default `uploadedAt + 24h` (FR-026). Mutable within the ceiling. |
| `maxExpiresAt` | string (ISO 8601 UTC) | `uploadedAt + 30d`, written once at upload. **The 30-day ceiling is data, not validation logic** (FR-028). |
| `renderVersion` | string | Sanitizer + renderer version pinned at upload. Guarantees a file always renders identically, which anchors depend on (R2). |
| `blobPath` | string | Pointer into the files container. |
| `ttl` | number (seconds) | Kept in sync with `expiresAt`. Cosmos hides expired items from queries before physically deleting them (FR-032). |

**Validation rules**

- `expiresAt` MUST be > now and ≤ `maxExpiresAt` on every write. Rejecting at the data layer means
  a new code path cannot accidentally bypass FR-028.
- `contentType` MUST match validated content, not the claimed extension (FR-007, and the
  "extension does not match content" edge case).
- Only `ownerId` may change `expiresAt` or delete (FR-029, FR-058).

**State transitions**

```
Uploading ──validated──> Live ──owner extends (≤ maxExpiresAt)──> Live
                          │
                          ├── owner deletes ─────────> Purging ──> Gone
                          └── expiresAt reached ─────> Expired ──> Purging ──> Gone
```

`Expired` is not a stored status. It is derived by comparing `expiresAt` to now, so a file reads
as gone the instant it expires regardless of when physical deletion runs (FR-032). Reconciliation
sweeps `Purging` to `Gone` across blob, Cosmos, and comments.

---

## Cosmos: `comments` container

**Partition key**: `/fileId` — single-partition list for the comment hot path (SC-003).

| Field | Type | Notes |
|---|---|---|
| `id` | string (ULID) | Sortable by creation time. |
| `fileId` | string | Partition key. |
| `threadId` | string | Equals `id` for a root comment; replies inherit the root's (FR-021). |
| `parentId` | string \| null | Direct parent for reply nesting. |
| `body` | string | Stored and returned as literal text. Never rendered as markup (FR-024). |
| `authorId` | string | From the authenticated principal only. A client-supplied value is ignored (FR-005, FR-023). |
| `authorDisplayName` | string | Denormalized. |
| `actingAgentId` | string \| null | Set when an agent acted. Drives visible attribution (FR-048). |
| `createdAt` / `editedAt` | string (ISO 8601 UTC) | |
| `anchor` | Anchor | See below. |
| `anchorState` | enum | `anchored` \| `orphaned` (FR-022). |
| `deletedAt` | string \| null | Soft delete so replies survive; audit records the change (FR-025). |
| `ttl` | number | Derived from the parent file's expiry, refreshed by reconciliation on extension (R7). |

---

## Anchor (embedded value object)

Not a container. Embedded in each comment, modelled on the W3C Web Annotation selectors (R2).

| Field | Type | Notes |
|---|---|---|
| `kind` | enum | `text` \| `region` |
| `exact` | string | The quoted passage. This is what an orphaned comment displays (FR-022). |
| `prefix` / `suffix` | string | ~32 chars of surrounding context. Disambiguates repeated passages. |
| `start` / `end` | number | Character offsets into the normalized text projection — a hint, not the source of truth. |
| `containerPath` | string \| null | Region only: nearest stable structural element. |
| `fractionalRect` | object \| null | Region only: normalized offsets within the container, never pixels. |
| `renderVersion` | string | The render the anchor was computed against. A mismatch means resolution must be treated as approximate. |

**Resolution order**: quote match → position hint to disambiguate → approximate match → orphan.
An anchor that cannot be resolved MUST NOT bind to a different location and MUST NOT be hidden
(FR-022); it becomes `orphaned` and displays `exact` with its context.

---

## Cosmos: `notifications` and `userPrefs`

**`notifications`** — partition `/recipientId`, short TTL. Fields: `id`, `recipientId`, `fileId`,
`threadId`, `triggerKind`, `coalesceKey`, `state` (`pending` \| `sent` \| `suppressed` \|
`failed`), `createdAt`, `sentAt`. `coalesceKey` is `recipientId + fileId + window` and is what
makes FR-038's consolidation a grouping operation rather than bespoke logic.

**`userPrefs`** — partition `/id` (Entra object ID), no TTL. Fields: `notificationsEnabled`
(FR-040), `updatedAt`.

---

## Table Storage: `AuditEntry`

Separate storage account (R6). Append-only by application contract (R8).

| Field | Notes |
|---|---|
| `PartitionKey` | `yyyyMMdd` — bounds partition growth, matches operator time-range access. |
| `RowKey` | Reverse-tick + ULID, newest first within a day. |
| `actorId`, `actorDisplayName` | Entra object ID (FR-042). |
| `actingAgentId` | Null for direct user actions. **The field that makes FR-049 and SC-015 satisfiable.** |
| `action` | `upload` \| `view` \| `preview` \| `download` \| `comment.create` \| `comment.edit` \| `comment.delete` \| `retention.extend` \| `file.delete` \| `file.expire` (FR-041). |
| `targetType` / `targetId` | File or comment. |
| `outcome` | `success` \| `denied` \| `error`. Denials are audited too — that is what makes the trail useful in an investigation. |
| `occurredAt` | ISO 8601 UTC (FR-042). |
| `correlationId` | Propagated frontend → API → storage (Principle V). |
| `previousExpiresAt` / `newExpiresAt` | Retention changes only (FR-030, US3 scenario 5). |

Never contains file content, comment text, or credentials (FR-045).

---

## Redis: transient keys

Never persisted; no schema migration concerns; loss degrades presence only (FR-069).

| Key | Value | TTL | Serves |
|---|---|---|---|
| `presence:{fileId}:{userId}` | display name, `actingAgentId`, `joinedAt` | 60 s, refreshed by heartbeat | FR-059–FR-066. Expiry *is* the departure mechanism (FR-062, SC-018). |
| `presence:{fileId}` (pub/sub channel) | change events | — | Fan-out across API replicas |
| `ratelimit:user:{userId}:{window}` | counter | window length | FR-085 |
| `ratelimit:agent:{agentId}:{userId}:{window}` | counter | window length | FR-054 |
| `quota:user:{userId}` | cached live-file count | short | FR-083, confirmed against Cosmos before commit |

One person is keyed once per file regardless of tab count (FR-063), because the key is
`{fileId}:{userId}` — deduplication is structural, not computed.

---

## Blob Storage (HNS account)

| Container | Object | Expiry |
|---|---|---|
| `originals` | `{fileId}` — bytes as uploaded | Set Blob Expiry, absolute, `= expiresAt` |
| `renders` | `{fileId}/{renderVersion}.html` — sanitized render | Same |
| `projections` | `{fileId}/{renderVersion}.txt` — normalized text for anchoring and agent reads | Same |

The projection exists so anchor resolution (R2) and agent content retrieval (FR-051) read the
same representation. If they diverged, an agent could comment against text no human ever saw.

---

## Relationships

```
User ──owns──> File ──has many──> Comment ──roots──> Thread
                 │                   │
                 │                   └──embeds──> Anchor
                 ├──has many──> PresenceSession   (Redis, transient)
                 └──generates──> AuditEntry       (Table, outlives the File)

AgentSession ──acts for──> User        (token-scoped, never stored)
Comment ──triggers──> Notification ──targets──> User
```

**AgentSession is intentionally not persisted.** Under On-Behalf-Of (R5), the agent's authority
lives entirely in the token. There is no row to forget to revoke, which is how FR-050 and FR-055
are satisfied by construction rather than by cleanup logic.

---

## Cross-cutting invariants

1. `expiresAt ≤ maxExpiresAt` always. Enforced at the data layer (FR-028).
2. Deleting a File deletes its blobs, its Cosmos document, and all its comments — and never its
   audit entries (FR-031, FR-043).
3. Author identity always comes from the token, never the payload (FR-005, FR-023).
4. Every mutation writes an AuditEntry before returning success (FR-041).
5. Presence data is never written to durable storage (FR-067) and is never the record of access
   (FR-068) — that is what the audit trail is for.
