# Presence Event Contract (Server-Sent Events)

**Feature**: `001-blinkmark-core` | **Endpoint**: `GET /api/files/{fileId}/presence`
**Media type**: `text/event-stream` | **Covers**: FR-059 – FR-069, SC-017 – SC-021

This is the only streaming interface in BlinkMark. It is one-directional; the client reports its
own liveness with ordinary `POST /api/files/{fileId}/presence` heartbeats rather than over the
stream.

## Connection

1. Client opens the stream with its Entra access token.
2. Server authorizes the caller for the file. **If the caller cannot view the file, the stream is
   refused with 403 and nothing about viewers is disclosed** — not the count, not the names
   (FR-064, SC-020).
3. Server registers `presence:{fileId}:{userId}` in Redis with a 60-second TTL and subscribes the
   connection to the `presence:{fileId}` pub/sub channel.
4. Server immediately emits a `snapshot` event so the client renders correct state on arrival.

## Heartbeats and departure

The client sends a heartbeat every 20 seconds. The Redis key TTL is 60 seconds, giving three
missed heartbeats of tolerance before a viewer is considered gone.

**Departure is not an event the client sends — it is the absence of heartbeats.** A clean close, a
closed laptop lid, a dropped VPN, and a force-quit browser are therefore all handled identically
and require no disconnect detection (FR-062). This is what makes SC-018 achievable: nothing has to
work correctly at the moment of failure.

A clean navigation away may additionally send `DELETE`-style departure via the heartbeat endpoint
to make removal immediate rather than waiting out the TTL. That is an optimization, never a
correctness requirement.

## Events

### `snapshot`

Full current state. Sent on connect and after any reconnect.

```
event: snapshot
data: {"fileId":"01J...","viewers":[{"userId":"a1b2","displayName":"Dana Reyes","actingAgentId":null,"joinedAt":"2026-07-26T10:04:11Z"},{"userId":"c3d4","displayName":"Sam Okafor","actingAgentId":"contoso-review-agent","joinedAt":"2026-07-26T10:06:02Z"}],"total":2,"displayed":2}
```

- `total` is the true count; `displayed` is how many are enumerated in `viewers`. **The
  enumeration cap is 8.** Beyond that the client shows the 8 most recently joined viewers by name
  plus an accurate remainder — "Dana, Sam, and 6 others" (FR-066). `total` remains exact up to the
  50-viewer accuracy target in SC-021.
- `actingAgentId` non-null means this person is present by way of an agent and must be rendered
  as agent-assisted, distinguishable from a directly present human (FR-065).
- A person appears **once** regardless of open tabs or devices, because the Redis key is
  `{fileId}:{userId}` — deduplication is structural, not computed (FR-063).

### `join`

```
event: join
data: {"userId":"e5f6","displayName":"Priya Nair","actingAgentId":null,"total":3}
```

### `leave`

```
event: leave
data: {"userId":"c3d4","total":2}
```

### `closed`

Emitted when the file is deleted or reaches expiry while viewers are connected — a listed edge
case. Presence for the file is cleared with it (FR-067, FR-068, US6 scenario 8).

```
event: closed
data: {"fileId":"01J...","reason":"expired"}
```

The client stops the stream and shows the expired state. It must not silently retry.

### `: keep-alive`

A comment line every 15 seconds so intermediaries do not close an idle connection.

## Client obligations

- **Degrade silently.** If the stream fails to open, drops, or the server returns 5xx, the client
  hides the presence display and continues previewing and commenting as normal. Presence failure
  MUST NOT surface an error that implies the document is broken (FR-069).
- **Reconnect with backoff**, and treat the `snapshot` on reconnect as authoritative rather than
  replaying missed deltas.
- **Announce politely.** Join and leave events are announced through an ARIA `polite` live region
  and MUST NOT move focus or interrupt the user (FR-080). Announcements are coalesced so a burst
  of arrivals does not produce a burst of speech.

## Non-goals

- No cursor positions, no text selections, no typing indicators. Presence answers "who is here",
  nothing more.
- No history. Presence is never persisted and is never the record of who accessed a file — that is
  the audit trail's job (FR-067, FR-068).
- No guaranteed delivery. Lost events are corrected by the next `snapshot`.
