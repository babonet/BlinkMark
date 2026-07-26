# Phase 0 Research: BlinkMark Core

**Feature**: `001-blinkmark-core` | **Date**: 2026-07-26 | **Constitution**: v1.1.0

The constitution already fixes the stack (React, .NET Core, Container Apps, Blob, Cosmos
serverless, Table Storage, Graph, Key Vault, App Insights), so this phase does not re-litigate
technology selection. It resolves the design unknowns that the fixed stack does not answer, and
records two places where reality diverges from what the constitution assumed.

---

## R1. Presence backplane

**Decision**: Azure Cache for Redis (Basic C0, 250 MB) holds presence state; the browser
receives updates over Server-Sent Events from the API container. Redis pub/sub fans changes out
across API replicas.

**Rationale**:

- The constitution forbids in-process state that "breaks under multi-instance deployment", so
  presence held in a replica's memory is already prohibited — different replicas would report
  different viewer lists. A shared store is mandatory, not optional.
- Redis `SETEX`-style keys give the staleness window (FR-062, SC-018) for free: a heartbeat
  refreshes a per-viewer key with a 60-second TTL, and a viewer who dies simply stops refreshing.
  No tombstones, no cleanup job, no disconnect detection.
- Entra ID authentication with managed identity is supported on the Basic tier, so the cheapest
  tier still satisfies the Safe Secrets Standard (R14). Access key authentication is disabled
  outright.
- The same Redis instance serves rate limiting (FR-054, FR-085) and quota counters (FR-083),
  which also need cross-replica shared state. One dependency, three jobs.
- Basic C0 is roughly $16/month, single-node with no SLA. That is consistent with the answer to
  clarification Q3 — best-effort, no DR, loss acceptable — and with FR-069, which requires
  preview and commenting to keep working when presence is unavailable. A Redis restart degrades
  presence and nothing else.
- SSE is one-directional server-to-client, which is all presence needs. It rides ordinary HTTP,
  needs no extra service, and is supported by Container Apps ingress.

**Alternatives considered**:

- *Azure SignalR Service / Web PubSub, Standard S1*: the "correct" managed answer, and both
  handle scale-out cleanly. Rejected on cost — roughly $49/month per unit, which is more than the
  entire rest of the runtime budget, to solve a problem Redis already solves as a side effect of
  work we must do anyway for rate limiting. Revisit if presence grows into collaborative editing.
- *Client polling every 3–5 seconds*: meets SC-017's 5-second window and needs no new service.
  Rejected because 500 concurrent viewers polling produces roughly 6,000 requests/minute against
  the API tier, which directly threatens SC-019 (presence must not regress preview and comment
  latency) and inflates Container Apps consumption billing.
- *Cosmos DB change feed as the presence store*: rejected. Presence writes a heartbeat per viewer
  per interval; at serverless RU pricing this is the most expensive possible home for the most
  worthless possible data, and FR-067 says presence must never be persisted anyway.

---

## R2. Comment anchoring model

**Decision**: Anchors follow the W3C Web Annotation Data Model. Every comment stores a
`TextQuoteSelector` (exact text plus prefix/suffix context) *and* a `TextPositionSelector`
(character offsets), both computed against a **normalized plain-text projection of the sanitized
render**. Region comments additionally store a `RangeSelector` naming the nearest stable
containing element plus normalized fractional offsets within it. Resolution happens client-side,
quote-first, position as a hint, using approximate matching.

**Rationale**:

- Principle III forbids anchoring to "a volatile DOM path or pixel coordinate alone". Text quote
  selectors are content-derived by construction and survive re-rendering.
- Storing the quote *and* its surrounding context is what makes FR-022 implementable: when the
  quote cannot be found, the stored context is exactly what the orphaned comment displays.
- The subtle part: the spec assumes files are immutable, which appears to make orphaning
  unreachable. It is not. Anchors resolve against the **sanitized** render, not the raw upload, so
  a change to sanitizer configuration or renderer version silently changes the text the anchor
  was computed against. Therefore the sanitizer and renderer version are pinned per file and
  stored with it, so a given file always renders identically for the life of that file. Orphaning
  remains implemented for the re-upload path and as the honest failure mode.
- Duplicate passages (a listed edge case) are why prefix/suffix context and the position hint are
  both stored: the position selector disambiguates identical quotes.

**Alternatives considered**:

- *XPath or CSS-path anchors*: rejected outright by Principle III, and brittle across sanitization.
- *Character offsets alone*: cheapest, but a single inserted character upstream shifts every
  anchor, and it cannot produce a meaningful orphan message.
- *Server-side anchor resolution*: rejected. It would require the server to hold a DOM per
  request, adding latency against SC-003's 300 ms budget; the client already has the rendered DOM.

**Library direction**: the Hypothesis-lineage approach (`dom-anchor-text-quote` /
`dom-anchor-text-position`, backed by approximate string matching) is the reference
implementation of exactly this model and is the intended starting point for the frontend.

---

## R3. Preview isolation — and a correction to the constitution

**Decision**: Sanitized preview content is served by a **dedicated backend service on its own
hostname** (`preview.<domain>`), separate from both the SPA host and the API host. The SPA embeds
it in an `<iframe sandbox>` without `allow-scripts` or `allow-same-origin`, and the response
carries a restrictive `Content-Security-Policy` including `sandbox`.

**Rationale**: Principle IV requires preview content to be served "from an origin that cannot
access the application's session, tokens, or cookies". A distinct registrable hostname plus a
sandboxed iframe with no same-origin grant achieves that: the framed document lands in an opaque
origin with no ambient credentials.

**⚠ Constitution correction required**: Constitution v1.1.0 states the Static Web Apps *Standard*
tier is required "because Principle IV's isolated preview origin is served as a second custom
domain on the same instance." That justification is factually wrong. Preview content is
per-file and dynamic; a Static Web Apps custom domain serves the same static bundle and cannot
serve it. The isolated origin must come from the backend tier.

This does not change the *outcome* — Standard remains a reasonable tier for SLA and staging
environments — but the stated reason is invalid and should not survive into implementation
folklore. **Recommendation: a PATCH amendment to constitution v1.1.1** correcting the
justification. Tracked in the plan's Complexity Tracking table.

**Alternatives considered**:

- *Same origin, sanitization only*: rejected. Principle IV requires isolation *and* sanitization;
  a sanitizer bypass with no origin boundary is a tenant-wide credential theft path.
- *`data:` or `blob:` URL iframes*: gives an opaque origin without a second hostname, but breaks
  the ability to stream large content and complicates CSP reporting. Rejected as less auditable.

---

## R4. Sanitization pipeline

**Decision**: Server-side, single pipeline for both formats. Markdown is rendered to HTML with
Markdig (safe configuration, raw HTML disabled), and *all* HTML — uploaded or generated — then
passes through an allowlist sanitizer (Ganss.Xss / HtmlSanitizer) before storage of the render
artifact. Sanitization happens once at upload, not per view.

**Rationale**:

- FR-013, FR-014, FR-016 and SC-008 demand that script never executes and that external
  references cannot leak viewer identity. An allowlist — not a denylist — is the only defensible
  posture; anything not explicitly permitted is dropped.
- Sanitizing once at upload and caching the render artifact keeps SC-002's 1-second preview
  budget achievable and makes the render deterministic, which R2's anchoring model depends on.
- External resource references (`img`, `link`, `iframe`, `url()`) are rewritten or stripped
  rather than proxied. Proxying would make BlinkMark a request forwarder for arbitrary uploaded
  content — an SSRF surface for no user benefit.

**Alternatives considered**:

- *Client-side sanitization (DOMPurify)*: rejected as the sole control — a client-side-only
  guarantee is not a server-side authorization boundary. May be retained as defence in depth.
- *Per-view sanitization*: rejected; wastes CPU on every view and makes renders non-deterministic
  across sanitizer upgrades, breaking anchors.

---

## R5. AI agent interface

**Decision**: Two coordinated surfaces over one set of application services — a REST API
described by OpenAPI 3.1, and a **Model Context Protocol server over Streamable HTTP** mounted in
the same API container. Both authenticate with Microsoft Entra ID; agent calls use the
On-Behalf-Of flow so the downstream identity is the user, not the agent.

**Rationale**:

- FR-052 requires a machine-readable, self-describing capability list. MCP's `tools/list` is
  exactly that primitive and needs no bespoke discovery document; OpenAPI covers conventional
  clients.
- FR-046/FR-047 require the agent's permissions to be *exactly* the user's. On-Behalf-Of achieves
  this at the token layer rather than by application-level impersonation logic — the API sees a
  user token and applies its ordinary authorization path, so there is no second authorization
  code path to get wrong.
- FR-055 (delegated only, no standing identity, no acting for a signed-out user) falls out
  naturally: OBO requires an inbound user assertion. There is no app-only credential to issue.
- The API authenticates to Entra for the OBO exchange using a federated identity credential backed
  by its managed identity, not a client secret — see R14.
- FR-051's structured content requirement is served by returning the normalized text projection
  plus anchor descriptors from R2 — the same representation the anchoring model already produces.

**Alternatives considered**:

- *REST + OpenAPI only*: adequate for FR-052 but leaves every agent integrator writing a bespoke
  adapter. MCP is the emerging standard for this exact use case and costs little on top.
- *MCP only*: rejected; the SPA needs a conventional API regardless, and MCP-only would strand
  non-agent automation.
- *App-only agent identity with a permissions filter*: explicitly forbidden by FR-055, and would
  require a constitution amendment.

---

## R6. File storage, expiry, and account topology

**Decision**: **Two storage accounts.** Account A has hierarchical namespace enabled and holds
file blobs; expiry is set per blob at write time via the Set Blob Expiry API (absolute mode).
Account B is a standard StorageV2 account holding the audit Table and the notification Queue.

**Rationale**:

- Set Blob Expiry — the mechanism the constitution mandates so the platform performs deletion —
  is only available on accounts with hierarchical namespace enabled.
- **Accounts with hierarchical namespace enabled do not support the Table and Queue services.**
  The constitution names Blob (HNS), Table Storage, and Storage Queues together without noting
  they cannot coexist in one account. Two accounts is therefore forced, not chosen.
- This is a happy accident: it puts the immutable audit trail in a different account from user
  content, with a different access policy and a separate resource lock, which strengthens FR-043.

**Retention extension**: extending a file rewrites the blob's expiry and the Cosmos document TTL.
Both are bounded by a stored `maxExpiresAt` computed once at upload as `uploadedAt + 30 days`,
so the 30-day ceiling (FR-028) is enforced by data rather than by validation logic that could be
bypassed on a new code path.

---

## R7. Cosmos DB partitioning and TTL cascade

**Decision**:

| Container | Partition key | TTL | Primary access pattern |
|---|---|---|---|
| `files` | `/id` | per-item | Point read by file id (preview hot path) |
| `comments` | `/fileId` | per-item | List all comments for one file (comment hot path) |
| `notifications` | `/recipientId` | per-item, short | Drain pending per recipient |
| `userPrefs` | `/id` (user id) | none | Point read |

**Rationale**:

- The preview path is a point read by file id — the cheapest and lowest-latency Cosmos operation,
  which is what SC-002 needs. Listing a user's own files becomes a cross-partition query, but the
  live document population is small by construction (a 50-file cap × hundreds of users, all
  expiring continuously), so this is affordable in serverless.
- `comments` partitioned by `fileId` makes "load all comments for this file" a single-partition
  query, which is the operation SC-003 puts a 300 ms budget on.
- Cosmos excludes TTL-expired items from query results *before* physically deleting them, which
  satisfies FR-032 (expired-but-not-purged content must read as deleted) without application
  filtering. Application code still checks expiry defensively.

**TTL cascade caveat**: Cosmos has no cascading delete. Comment TTLs are set from the file's
expiry at comment creation. On a retention extension the comments' TTLs must be updated too;
this is done by the reconciliation job rather than synchronously, because a file may have many
comments and FR-027 must not become a slow operation. The read path always validates the parent
file first, so a stale comment TTL can never surface content that should be gone.

---

## R8. Audit trail append-only within Table Storage

**Decision**: Audit entries are written to Table Storage in the dedicated account (R6). Append-only
is enforced by four layers: no update or delete code path exists in the application; the audit
repository interface exposes only `AppendAsync`; the account carries a resource lock; and an
automated test asserts that no delete or merge operation against the audit table exists in the
compiled application.

**Honest limitation**: Azure Storage **immutability (WORM) policies apply to Blob containers, not
to Tables**. There is no platform-enforced write-once guarantee for Table entities, and no
built-in add-only RBAC role for the Table data plane. FR-044 says "no *interface* that permits
application-level modification or deletion", and the decision above satisfies that literally —
but it is application-enforced, not platform-enforced, and that distinction should be understood
rather than glossed.

**Escalation path if platform-enforced immutability is later required**: dual-write each entry as
an append blob in a container with a time-based immutability policy, keeping the Table as the
queryable projection. Deferred — it doubles audit write cost and, per clarification Q1, nothing
in the product reads audit entries in this phase.

**Alternatives considered**:

- *Audit in Cosmos*: rejected by the constitution and on cost — write-once, read-rarely data at RU
  pricing is the worst possible fit.
- *Audit to Log Analytics as system of record*: explicitly forbidden by the constitution, and
  ingestion pricing would dominate the bill.

---

## R9. Notifications

**Decision**: Comment events enqueue a message to a Storage Queue. A KEDA queue-scaled Container
App drains it, coalesces events per recipient per file over a short window, and sends email via
Microsoft Graph using an application permission (`Mail.Send`) constrained by an **Application
Access Policy** to a single dedicated service mailbox. Teams delivery is deferred.

**Rationale**:

- FR-037 requires that notification failure never affects the user action. Enqueue-and-forget is
  the only shape that guarantees this; the enqueue is the only synchronous work.
- FR-038's consolidation requirement needs a delay window, which a queue consumer provides
  naturally and a synchronous path cannot.
- An unconstrained `Mail.Send` application permission can send as *any* mailbox in the tenant.
  The Application Access Policy narrowing it to one service mailbox is what makes that permission
  grant defensible to a security reviewer, and it should be treated as mandatory, not optional.
- The spec says "email **or** Teams", so email alone satisfies FR-034/FR-035. Teams activity-feed
  notifications require a registered Teams app and a separate consent path; that is real scope
  for no additional requirement coverage in this phase.

---

## R10. Quotas and rate limiting

**Decision**: Redis holds rate-limit counters (fixed window, `INCR` + `EXPIRE`) for both agent
(FR-054) and human (FR-085) limits. The live-file quota (FR-083) is checked against Redis for
speed and confirmed against Cosmos before the upload commits, with Cosmos authoritative.

**Rationale**: A rate limiter in replica memory is both wrong under scale-out and prohibited by
the constitution's stateless requirement. Making Cosmos authoritative for the quota avoids the
failure mode where a Redis flush silently grants everyone unlimited uploads. FR-088 — capacity
frees itself as files expire — needs no implementation: the quota is a live count, and expiry
reduces it. FR-087 (agent uploads count against the represented user) is automatic because OBO
means the API only ever sees the user's identity.

---

## R11. Accessible anchoring

**Decision**: Selection-to-comment has a first-class keyboard path that does not depend on
pointer selection: the preview exposes an ordered list of addressable text blocks, and a
keyboard user moves through them, extends a selection with modified arrow keys, and commits with
a documented shortcut. Region selection (FR-018) offers a non-dragging alternative (FR-082) by
selecting a structural element rather than drawing a rectangle. Presence updates are announced
through a `polite` live region that never moves focus (FR-080).

**Rationale**: WCAG 2.1 AA (clarification Q4) plus FR-078 and FR-082 make this a design
constraint on US2 and US6 rather than a later remediation. The chosen approach — anchoring to
structural elements for the keyboard path — reuses the same selector model from R2, so the
keyboard path produces identical anchor data to the pointer path rather than a second format.

---

## R12. Resulting cost model

| Component | Tier | Approx. monthly |
|---|---|---|
| Static Web Apps | Standard | $9 |
| Container Apps (API + preview + notifier + job) | Consumption, min 1 replica on API | $15–25 |
| Azure Cache for Redis | Basic C0 | $16 |
| Cosmos DB | Serverless | <$10 |
| Blob (HNS) + Table + Queue | Hot / standard, LRS | $1–3 |
| Key Vault | Standard | ~$0–1 |
| App Insights | Daily cap set | $0–5 |
| Entra ID, Graph | Existing licensing | $0 |
| **Subtotal** | | **≈ $55–70** |
| Private endpoints (×5) + VNet | Required by `[SFI-NS2.2.1]` | $40–50 |
| **Total with SFI network posture** | | **≈ $95–120** |

Redis is the delta against the ~$35–55 estimate that informed constitution v1.1.0. The private
endpoint and VNet requirement is the delta introduced by constitution v1.2.0. Both are recorded in
the plan's Complexity Tracking table with their justification.

---

## R13. Preview origin authorization

**Decision**: The API mints a **short-lived, user-scoped, single-file preview token** (signed JWT,
15-minute ceiling, audience pinned to the preview host, carrying `sub`, optional `agt`, `fid`,
`renderVersion`, and the correlation ID). The preview host validates that token and nothing else.
It never receives an Entra access token, a session cookie, or a refresh token.

**Rationale**:

- R3 isolates the preview on its own origin to satisfy Principle IV. That isolation removes the
  session — but Principle I still requires *every* request that previews a file to be authorized
  server-side before content is returned. Without an explicit mechanism, the isolated origin would
  either be unauthenticated (violating Principle I and FR-001) or the design would be incomplete.
  This decision closes that gap.
- The document is framed with `sandbox` and **without** `allow-same-origin`, so it renders in an
  opaque origin: it cannot read or set cookies, cannot use storage, and cannot attach an
  `Authorization` header to its own document request. The credential must therefore travel in the
  URL of the initial document fetch. There is no cookie- or header-based alternative that
  preserves the isolation.
- This is deliberately the same shape as the "short-lived, user-scoped, read-only SAS (15 minutes
  maximum)" that the constitution already sanctions for blob access — the same ceiling, the same
  scoping discipline, applied to a rendered artifact instead of a raw blob.
- It also resolves an auditing problem. FR-041 lists `view` and `preview` as separate actions
  because they occur in different services. The preview host can now emit an accurate `preview`
  entry from the token's `sub`, `agt`, and correlation ID; the API could not, because it cannot
  know whether an issued token was ever redeemed.

**Mitigations for a credential in a URL**: 15-minute maximum lifetime; scoped to one file and one
render version; `Referrer-Policy: no-referrer` so it cannot leak onward from within the framed
content; query strings excluded from access logs and request telemetry; the token grants read of a
single rendered artifact and confers no ability to comment, delete, extend retention, or
enumerate. Because the sanitized render contains no external references (FR-016), no subresource
ever carries the token to a third party.

**Why 15 minutes does not constrain reviewers**: the token gates the document *fetch*, not the
review session. Once the preview has rendered, nothing re-validates it, and commenting runs
against the API with the user's ordinary session — so a reviewer may work for hours untouched by
expiry. Tokens are reusable within their lifetime so ordinary navigation works, and the SPA
re-mints transparently if a reload happens after expiry. The lifetime limits how long a *leaked
URL* remains useful; it does not limit the user.

**Alternatives considered**:

- *Cookie scoped to the preview origin*: impossible. A sandboxed frame without `allow-same-origin`
  has an opaque origin and will not send or accept cookies.
- *Adding `allow-same-origin` to the sandbox so a cookie works*: rejected outright. Combined with
  any script capability it collapses the isolation Principle IV exists to create.
- *Serving preview from the API origin*: rejected — defeats Principle IV entirely.
- *Direct blob SAS to the browser*: rejected. It would serve the raw upload rather than the
  sanitized render, bypassing FR-013 and FR-016, and would give no place to emit the audit entry.

Contract: [contracts/preview-origin.md](contracts/preview-origin.md).

---

## R14. SFI Safe Secrets Standard — control mapping

**Decision**: Local authentication is disabled at every resource, all Azure-to-Azure calls use a
user-assigned managed identity, and no Entra application holds a client secret. Constitution
Principle VII (v1.2.0) makes this binding.

Mapped against the S360 SFI KPIs that apply to this stack:

| S360 KPI | Applies to | Control |
|---|---|---|
| `[SFI-ID4.2.1]` Storage Accounts – Safe Secrets Standard | Both storage accounts | `allowSharedKeyAccess: false`, `defaultToOAuthAuthentication: true`, `allowBlobPublicAccess: false`, `minimumTlsVersion: TLS1_2`. Data-plane RBAC: Storage Blob Data Contributor, Storage Table Data Contributor, Storage Queue Data Message Sender/Processor |
| `[SFI-ID4.2.3]` Cosmos DB – Safe Secrets Standard | Cosmos serverless | `disableLocalAuth: true`; Cosmos DB Built-in Data Contributor assigned to the managed identity via `sqlRoleAssignments` |
| `[SFI-ID4.2.7]` Redis Cache – Safe Secrets Standard | Azure Cache for Redis | Entra authentication enabled, `disableAccessKeyAuthentication: true`, Redis Data Owner access policy assigned to the managed identity |
| `C+E FUN Security P0` – TLS 1.2+ for Azure Cache for Redis | Redis | `minimumTlsVersion: 1.2`, non-TLS port disabled |
| `[SFI-ID4.1.1]` Entra ID Apps – Safe Secrets Standard | API and SPA registrations | No client secrets, no certificates. See the OBO note below |
| `[SFI-ID4.1.2]` Service Principal – Safe Secrets Standard | CI/CD | GitHub Actions authenticates by workload identity federation; no service principal secret |
| `[SFI-NS2.2.1]` Secure PaaS Resources / Restrict Key Vault access | All data services | `publicNetworkAccess: Disabled` plus private endpoints from a VNet-integrated Container Apps environment |
| `C+E FUN Comp P0` – Key Vault access policies → Azure RBAC | Key Vault | `enableRbacAuthorization: true`; access policies not used |
| `[PILOT][SFI-vTI5.1.3]` Remove standing human access to Key Vaults | Key Vault | No standing human role assignments; operator access is JIT through PIM |
| `[SFI-PS4.2.3]` / `[SFI-PS4.9]` CSP adoption and violations | Preview origin, SPA | Already satisfied by R3 and `contracts/preview-origin.md`; add CSP violation reporting |
| `[SFI-ES3.1.1]` Live Secret Detection, `[SFI-PS3.2]` CodeQL | Repository | Already in T006 |

**The On-Behalf-Of problem, and its solution.** R5 chose On-Behalf-Of for agent access, and OBO
normally requires the API to present a client credential to Entra — exactly the client secret
`[SFI-ID4.1.1]` forbids. The resolution is a **federated identity credential on the app
registration, backed by the API's user-assigned managed identity**: the API obtains a token for
its own managed identity and presents it as a client assertion. OBO works unchanged, and no
credential exists to leak or rotate. Without this, SFI compliance and agent access would be in
direct conflict.

**What remains in Key Vault.** With local auth disabled everywhere, the only key material left is
the preview-token signing key from R13. Rather than storing it as a secret, it is a **Key Vault
key**, and the API signs through the Key Vault sign operation using its managed identity. The key
material never reaches the application. BlinkMark therefore holds no retrievable secret at all.

**Graph mail sending.** The `Mail.Send` application permission is granted to the **managed
identity's** service principal rather than to a secret-bearing app registration, and remains
constrained by an Application Access Policy to one service mailbox (R9).

**Deployment identity.** `azd` and GitHub Actions authenticate by workload identity federation.
No publish profile, no Static Web Apps deployment token, no service principal secret. The Static
Web App is deployed through its Entra-authenticated deployment path.

**Cost consequence — this is not free.** `[SFI-NS2.2.1]` requires public network access disabled
on the data services, which means private endpoints and a VNet. Roughly five private endpoints at
~$7.30/month each, plus a VNet-integrated Container Apps environment, adds **~$40–50/month**. See
the revised cost model in R12. This is a genuine trade-off: the workload is internal and
short-lived, but the requirement is not risk-based and is not negotiable at plan level.

---

## Unknowns remaining

None blocking. All Technical Context fields resolved; no `NEEDS CLARIFICATION` markers carried
into Phase 1.
