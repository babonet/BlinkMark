# Preview Origin Contract

**Feature**: `001-blinkmark-core` | **Host**: `preview.<domain>` (distinct from the API and SPA hosts)
**Covers**: FR-001, FR-003, FR-012, FR-013, FR-014, FR-016, FR-041, FR-081 · Constitution Principles I and IV

The preview origin is the only service that returns rendered file content to a browser. It exists
on its own hostname so that a sanitizer bypass lands in an origin with no access to the
application's session, tokens, or cookies (Principle IV).

That isolation creates the problem this contract solves: **the preview host cannot use the user's
session, but Principle I still requires every preview request to be authorized server-side before
content is returned.** It is authorized by a short-lived, user-scoped, single-file preview token.

---

## Preview token

Issued by the API when a caller who is authorized to view a file requests its metadata. Presented
to the preview host as the sole authorization credential.

| Claim | Value |
|---|---|
| `iss` | The API host |
| `aud` | The preview host — a token minted for the API is not accepted here, and vice versa |
| `sub` | Entra object ID of the user the preview is for |
| `agt` | Acting agent identifier, or absent (FR-048, FR-049) |
| `fid` | The single file this token grants. One token never covers two files |
| `rv` | `renderVersion`, pinning which render artifact may be served |
| `exp` | Issue time + 15 minutes maximum |
| `cid` | Correlation ID, propagated from the originating API request (Principle V) |

Signed with a key held in Key Vault. The preview host validates signature, audience, expiry, and
that `fid` matches the requested path — and does nothing else to establish identity.

**Why a token in the URL rather than a cookie or header.** The preview document is framed with
`sandbox` and *without* `allow-same-origin`, so it renders in an opaque origin: it cannot read or
set cookies, cannot use storage, and cannot attach an `Authorization` header to its own document
request. The credential therefore has to travel in the URL of the initial document fetch. This is
the same shape as the short-lived, user-scoped, read-only SAS the constitution already sanctions
for blob access, with the same 15-minute ceiling.

**Mitigations for a credential in a URL:**

- 15-minute maximum lifetime, and scoped to exactly one file and one render version.
- `Referrer-Policy: no-referrer` on the preview response, so the token cannot leak onward through
  navigation from within the framed content.
- Query strings excluded from access logs and from Application Insights request telemetry.
- The token grants *read of one rendered artifact*. It confers no ability to comment, delete,
  extend retention, or enumerate anything.
- The sanitized render contains no external references (FR-016), so no subresource ever carries
  the token to a third party.

---

## Endpoint

```
GET https://preview.<domain>/p/{fileId}?t={previewToken}
```

**Responses**

| Status | Meaning |
|---|---|
| `200` | Sanitized render artifact, `text/html` |
| `401` | Token missing, malformed, expired, or wrong audience. No content, no filename, no confirmation the file exists (FR-001) |
| `403` | Token valid but `fid` does not match the requested path |
| `404` | File not found, or expired. Expired content reads as absent (FR-032), deliberately indistinguishable from not-found |

**Required response headers**

```
Content-Security-Policy: sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; form-action 'none'; frame-ancestors https://app.<domain>
Referrer-Policy: no-referrer
X-Content-Type-Options: nosniff
Cache-Control: private, no-store
```

`frame-ancestors` restricts framing to the SPA. `default-src 'none'` means the render cannot reach
the network at all, which enforces FR-016 at the browser in addition to at sanitization time.

**Embedding**

```html
<iframe src="https://preview.example.com/p/01J...?t=eyJ..."
        sandbox
        referrerpolicy="no-referrer"
        title="Preview of {displayName}"></iframe>
```

`sandbox` with no tokens: no scripts, no same-origin, no forms, no top-level navigation. Any
`allow-scripts` combined with `allow-same-origin` would defeat the isolation entirely and must
never be added.

---

## Token lifetime and long review sessions

**The token authorizes the document fetch, not the review session.** Once the preview has loaded
into the iframe, nothing re-validates it. A reviewer may read for hours; an expired token has no
effect on already-rendered content.

**Commenting never uses this token.** Comments are created against the API with the user's ordinary
Entra session. Selecting passages, writing, replying, and resolving are entirely unaffected by
preview token expiry. Fifteen minutes therefore constrains nothing a reviewer actually does.

**Tokens are reusable within their lifetime, not single-use.** A reload one minute after load must
succeed with the same token; requiring a fresh mint per request would break ordinary back and
forward navigation.

**Reload after expiry must recover silently.** The SPA MUST treat a `401` from the preview origin
as "re-mint and retry", not as an error: request the file metadata again, obtain a fresh
`previewUrl`, and reload the frame. The user must never see an authentication failure for content
they are still authorized to read. This matters most in the cases users hit accidentally — tab
restore after a laptop wake, back-navigation, and network interruption.

**The SPA SHOULD pre-emptively re-mint** when the frame is reloaded for any reason after roughly
ten minutes, rather than waiting for the `401`.

A fresh token is available on demand for as long as the user remains authorized. Expiry limits how
long a *leaked URL* stays useful; it does not limit the user.

## Auditing, using `sub`, `agt`, and `cid` from the
token. This is the only place that entry can be written accurately, because the API cannot know
whether an issued token was ever redeemed. FR-041 lists `view` and `preview` as separate actions
precisely because they happen in different services: `view` is the metadata read at the API,
`preview` is the content fetch here.

Because tokens are reusable and re-minted on reload, one reading session may produce several
`preview` entries. That is correct — each entry records an actual content fetch.

---

## Keyboard and assistive technology (FR-081)

The framed document is a focus trap by default. The SPA must provide:

- A labelled, focusable wrapper so a keyboard user knows the preview region exists.
- A documented key to move focus into the framed content, and `Escape` to return focus to the
  surrounding application.
- A skip link that bypasses the preview entirely for users navigating to the comment list.

---

## Non-goals

- The preview host never issues tokens, never reads Cosmos, and never writes anything except
  audit entries.
- It never sees an Entra access token, a session cookie, or a refresh token.
- It serves stored render artifacts only. Sanitization happens once at upload, not here.
