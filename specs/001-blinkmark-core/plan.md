# Implementation Plan: BlinkMark Core

**Branch**: `001-blinkmark-core` | **Date**: 2026-07-26 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-blinkmark-core/spec.md`

## Summary

BlinkMark is a tenant-internal service for sharing short-lived HTML and Markdown drafts and
collecting anchored review comments on them. Files self-destruct after 24 hours by default and
can never outlive 30 days. Reviewers comment on specific passages; comments that lose their
passage surface as orphaned rather than vanishing. AI agents reach the same capabilities strictly
on a user's behalf, and everyone can see who else is reading the document right now.

The technical approach is settled by constitution v1.1.0 (React, .NET Core on Container Apps,
Cosmos serverless, Blob with hierarchical namespace, Table Storage audit). Phase 0 resolved the
design questions that stack does not answer: **Redis plus Server-Sent Events for presence**
(chosen over a $49/month managed real-time service because Redis is needed for rate limiting
anyway), **W3C Web Annotation selectors computed against a versioned sanitized render** for
anchoring, and **MCP over Streamable HTTP with On-Behalf-Of** for agent access, which makes
"agent permissions equal user permissions" a token-layer property rather than application logic.

## Technical Context

**Language/Version**: C# / .NET 9 (backend); TypeScript 5.x with React 18 and Vite (frontend)
**Primary Dependencies**: ASP.NET Core Minimal API, Markdig, Ganss.Xss (HtmlSanitizer), Azure SDK
(Blob, Cosmos, Tables, Queues), StackExchange.Redis, Microsoft.Identity.Web, MSAL React,
`dom-anchor-text-quote` / `dom-anchor-text-position`
**Storage**: Cosmos DB serverless (working set); Blob Storage with hierarchical namespace (content
and render artifacts); Table Storage (audit); Redis Basic C0 (transient coordination)
**Testing**: xUnit with Testcontainers (backend); Vitest and Playwright with axe-core (frontend)
**Target Platform**: Azure Container Apps (Consumption, VNet-integrated) and Azure Static Web Apps
(Standard), single region, locally redundant. **Deployment target**: subscription
`46a174f6-0602-4df8-9fb0-f8e8248bcb8f` ("Commerce AI Assistant"), resource group `rg-blinkmark`,
region `eastus`, tenant `72f988bf-86f1-41af-91ab-2d7cd011db47`
**Project Type**: Web application — React frontend plus .NET backend
**Performance Goals**: p95 preview start <1 s; p95 comment create and list <300 ms; p95 upload
acknowledgement <2 s at 10 MB (the accepted limit is 20 MB; 10 MB remains the measurement point,
so raising the limit did not silently tighten this target); presence propagation <5 s
**Constraints**: 500 concurrent users; stateless horizontally scalable services; content encrypted
at rest and in transit; no unauthenticated path to any content or metadata; 30-day absolute
retention ceiling; WCAG 2.1 Level AA; best-effort availability with no DR and no backup; **no
local authentication on any resource and no service credentials anywhere (SFI Safe Secrets
Standard, Principle VII)**
**Scale/Scope**: 6 user stories, 88 functional requirements, 27 success criteria; ~50 live files
per user; up to 50 simultaneous viewers per file; target runtime cost ≈ $95–120/month

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Initial evaluation (pre-Phase 0)

| Principle | Gate | Status |
|---|---|---|
| I. Tenant-Only Secure Access | Every content path authenticated and authorized server-side; no anonymous access; no long-lived public blob URLs | PASS — single-tenant registration, server-side authorization on every endpoint, preview authorized by a 15-minute single-file token (research.md R13), no public blob URL |
| II. Ephemeral by Default | 24 h default, 30-day hard ceiling, platform-enforced deletion, expired reads as deleted | PASS — Set Blob Expiry plus Cosmos TTL; ceiling stored as `maxExpiresAt` data |
| III. Anchored Comments Must Survive | Content-derived anchors, graceful orphaning, p95 <300 ms | PASS — W3C selectors, explicit `orphaned` state |
| IV. Untrusted Content Is Never Trusted | Sandboxed isolated origin, server-side sanitization, system-generated names | PASS — dedicated preview hostname, allowlist sanitizer, ULID blob names |
| V. Auditable and Observable | Structured append-only audit outliving files, correlation IDs, no content in logs | PASS with a caveat — see R8 on Table Storage immutability |
| VI. Contract-First, Test-Backed Delivery | Contracts before code; mandatory tests for authz, retention, anchoring, sanitization | PASS — contracts in `contracts/`, four mandatory test areas fixed |
| VII. Credential-Free by Default | Local auth disabled at every resource; managed identity everywhere; no Entra app secrets; federated deployment identity | PASS — control mapping in research.md R14, verified in CI |
| Platform constraints | Stack fixed by constitution | **2 deviations** — Redis is not a named service; private endpoints and a VNet are added. Both justified in Complexity Tracking. |

### Post-design re-evaluation (after Phase 1)

Re-checked against the completed design. No new violations. Two findings:

1. **Redis remains the single deviation.** It is not named in the constitution's Platform and
   Technology Constraints. Justified below; it adds roughly $16/month.
2. **The constitution contains a factual error** that the design surfaced. v1.1.0 justifies the
   Static Web Apps Standard tier on the grounds that "Principle IV's isolated preview origin is
   served as a second custom domain on the same instance." Preview content is per-file and
   dynamic; a Static Web Apps custom domain serves a static bundle and cannot serve it. The
   isolated origin must come from the backend tier. The tier choice is unaffected; the stated
   reason is wrong and should be corrected by a **PATCH amendment to v1.1.1** rather than silently
   contradicted by the implementation.

Everything else holds. Notably, the constitution's stateless requirement did useful work here —
it eliminated in-memory presence before it could be proposed.

## Project Structure

### Documentation (this feature)

```text
specs/001-blinkmark-core/
├── plan.md                      # This file
├── spec.md                      # Feature specification
├── research.md                  # Phase 0 — 12 decisions with rationale
├── data-model.md                # Phase 1 — entities across four stores
├── quickstart.md                # Phase 1 — developer onboarding
├── contracts/
│   ├── openapi.yaml             # REST contract
│   ├── mcp-tools.json           # Agent capability manifest (FR-052)
│   ├── preview-origin.md        # Isolated preview host + preview token (Principles I and IV)
│   └── presence-sse.md         # Presence event stream contract
├── checklists/
│   └── requirements.md          # Spec quality validation
└── tasks.md                     # Phase 2 — created by /speckit.tasks, not by this command
```

### Source Code (repository root)

```text
backend/
├── src/
│   ├── BlinkMark.Core/                 # Domain: anchors, retention rules, quotas, authorization
│   │   ├── Anchoring/
│   │   ├── Retention/
│   │   └── Quotas/
│   ├── BlinkMark.Infrastructure/       # Adapters: Cosmos, Blob, Tables, Queues, Redis, Key Vault
│   ├── BlinkMark.Api/                  # REST + SSE + MCP endpoint (Container App, min 1 replica)
│   │   ├── Endpoints/
│   │   ├── Mcp/
│   │   └── Presence/
│   ├── BlinkMark.Preview/              # Isolated preview origin (separate Container App + hostname)
│   └── BlinkMark.Jobs/
│       ├── Reconciliation/             # Cron job: retention backstop, comment TTL sync
│       └── Notifications/              # KEDA queue-scaled notification dispatcher
└── tests/
    ├── contract/                       # OpenAPI + MCP manifest conformance
    ├── integration/                    # The four mandatory areas under Principle VI
    └── unit/

frontend/
├── src/
│   ├── components/
│   │   ├── preview/                    # Sandboxed iframe host
│   │   ├── comments/                   # Anchoring UI, keyboard selection path
│   │   └── presence/                   # Avatars, count, polite live region
│   ├── pages/
│   └── services/                       # API client, MSAL, SSE client
└── tests/
    ├── a11y/                           # Keyboard-only commenting, live-region announcements
    └── unit/

infra/                                  # Bicep: two storage accounts, Cosmos, Redis, ACA, SWA, KV
```

**Structure Decision**: Web application layout — a React frontend and a .NET backend, as the
constitution fixes both. The backend is split into four deployables rather than one because they
have genuinely different constraints: `BlinkMark.Api` must stay warm for latency;
`BlinkMark.Preview` must run on a separate hostname to satisfy Principle IV and must never see a
session token; `Notifications` scales from zero on queue depth; `Reconciliation` runs on a cron
schedule. Collapsing them would compromise the isolation Principle IV requires.

The preview service is authorized by a short-lived, single-file preview token rather than by the
user's session — see [contracts/preview-origin.md](contracts/preview-origin.md) and research.md
R13. It never receives an Entra access token, a session cookie, or a refresh token.

## Phase 0 — Research (complete)

12 decisions recorded in [research.md](research.md): presence backplane, anchoring model, preview
isolation, sanitization pipeline, agent interface, storage topology and expiry, Cosmos
partitioning and TTL cascade, audit append-only, notifications, quotas and rate limiting,
accessible anchoring, and the resulting cost model. No `NEEDS CLARIFICATION` items remain.

Three findings that changed the design rather than merely confirming it:

- **Hierarchical-namespace accounts do not support the Table and Queue services**, so the single
  storage account implied by the constitution is impossible. Two accounts are forced — which
  incidentally puts the audit trail behind a separate access policy and resource lock.
- **Anchors resolve against the sanitized render, not the upload.** This makes orphaning reachable
  even though files are immutable, and forces `renderVersion` to be pinned per file.
- **Table Storage has no platform-enforced immutability.** WORM policies apply to blobs, not
  tables. FR-044 is satisfied at the application layer; the distinction is documented rather than
  glossed, with an escalation path if platform enforcement is later required.

## Phase 1 — Design & Contracts (complete)

- [data-model.md](data-model.md) — entities across Cosmos, Blob, Table, and Redis, with validation
  rules, state transitions, and five cross-cutting invariants.
- [contracts/openapi.yaml](contracts/openapi.yaml) — REST contract; every operation annotated with
  the requirements it satisfies.
- [contracts/mcp-tools.json](contracts/mcp-tools.json) — agent capability manifest satisfying
  FR-052, including an explicit `notProvided` list explaining what agents deliberately cannot do.
- [contracts/preview-origin.md](contracts/preview-origin.md) — the isolated preview host, its
  preview-token authorization, required response headers, and where the `preview` audit entry is
  written.
- [contracts/presence-sse.md](contracts/presence-sse.md) — presence event stream, its degradation
  contract, and its accessibility obligations.
- [quickstart.md](quickstart.md) — developer onboarding with the four verification checks that
  must pass before trusting the skeleton.

### Design decisions worth carrying into implementation

**The 30-day ceiling is data, not validation logic.** `maxExpiresAt` is computed once at upload
and stored. Any code path that writes `expiresAt` is checked against it at the data layer, so a
future endpoint cannot forget the rule.

**Departure is the absence of heartbeats.** Presence expiry is a Redis TTL, so a clean close, a
dropped VPN, and a force-quit browser are handled identically. Nothing has to work correctly at
the moment of failure — which is why SC-018 is achievable.

**AgentSession is never persisted.** Under On-Behalf-Of, the agent's authority lives entirely in
the token. There is no row to forget to revoke, so FR-050 and FR-055 hold by construction.

**Agents read the same projection anchors resolve against.** If agent content and anchor content
diverged, an agent could comment on text no human ever saw.

**The preview origin is authorized by a scoped token, not by the session.** Isolating the preview
onto its own origin removes the session, but Principle I still demands server-side authorization
before content is returned. A 15-minute, single-file, single-render token closes that gap — the
same shape as the SAS the constitution already sanctions — and gives the `preview` audit entry a
place to be written accurately.

**BlinkMark holds no retrievable secret.** Local auth is disabled at every resource and every
Azure-to-Azure call uses a user-assigned managed identity (research.md R14). Two consequences are
easy to miss: the On-Behalf-Of exchange uses a **federated identity credential backed by the
managed identity** rather than a client secret, without which SFI and agent access would be in
direct conflict; and the preview-token signing key is a **Key Vault key signed in place**, not a
secret the application retrieves. There is nothing left to rotate or leak.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| Azure Cache for Redis (Basic C0, ~$16/mo) — not named in constitution v1.1.0 | Presence (FR-059–FR-069), distributed rate limiting (FR-054, FR-085), and quota counters (FR-083) all require state shared across API replicas. The constitution's own stateless requirement forbids holding it in-process. | *In-memory state*: prohibited by the constitution and simply wrong under scale-out — replicas would report different viewer lists. *Azure SignalR / Web PubSub Standard*: ~$49/mo, roughly triple, to solve only presence while rate limiting still needs shared state. *Client polling*: meets the 5 s latency target but ~6,000 req/min at 500 users threatens SC-019 and inflates consumption billing. *Cosmos as presence store*: RU pricing for per-viewer heartbeats is the most expensive home for the least valuable data, and FR-067 forbids persisting it. |
| Four backend deployables rather than one | `Preview` must be a separate origin under Principle IV and must never receive a session token; `Api` must stay warm for SC-002/SC-003; `Notifications` scales from zero on queue depth; `Reconciliation` is cron-scheduled. | A single service would either share the preview origin with the API — defeating Principle IV outright — or force the whole app to stay warm at the cost of the notification and job tiers' scale-to-zero savings. |
| Two storage accounts | Hierarchical namespace is required for Set Blob Expiry, and HNS accounts do not support the Table and Queue services. | Not a choice. The single-account arrangement implied by the constitution cannot be provisioned. |
| Private endpoints (×5) and a VNet-integrated Container Apps environment, ~$40–50/mo | `[SFI-NS2.2.1]` Secure PaaS Resources requires public network access disabled on Blob, Table/Queue, Cosmos, Redis, and Key Vault. Constitution v1.2.0 makes this binding. | *Service firewalls with IP allow-lists*: Container Apps Consumption egress IPs are not stable, so this is unreliable and still leaves the data plane internet-reachable. *Service endpoints*: do not remove public reachability, only restrict it. Neither satisfies the KPI. Cost is the price of a non-risk-based corporate control, not an engineering preference. |

### Constitution amendment recommended

**PATCH to v1.1.1** — correct the Static Web Apps Standard justification. The current text claims
the isolated preview origin is "served as a second custom domain on the same instance", which is
not achievable for dynamic per-file content. The tier remains appropriate; only the reason needs
fixing. This is a clarification with no change in obligation, so PATCH is the correct bump.

Separately, if Redis is expected to persist beyond this feature, consider a **MINOR** amendment
adding it to the Platform and Technology Constraints rather than carrying it as a standing
Complexity Tracking entry.

## Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Keyboard-only passage selection (FR-078, FR-082) is genuinely hard and shapes US2's interaction model | Late discovery forces a US2 rewrite | Build the keyboard path first, not last; both paths emit identical anchor data |
| Sanitizer upgrade silently invalidates stored anchors | Comments orphan en masse | `renderVersion` pinned per file; upgrades apply to new uploads only |
| Table Storage immutability is application-enforced, not platform-enforced | Weaker compliance posture than the wording implies | Documented in R8 with a dual-write escalation path; audit account carries a resource lock |
| Redis Basic C0 has no SLA and restarts without warning | Presence drops | Expected and acceptable — FR-069 requires the product to work without it; verify by stopping Redis |
| In-app notifications only reach someone who opens BlinkMark (R9, revised) | US4 no longer pulls a reviewer back to a review they stopped watching, which was its point | Accepted for now. `Mail.Send` app-only access is not available in the tenant, so email was not an option to trade against. The queue, coalescing, and recipient resolution are channel-independent, so adding mailbox-scoped RSC email or a Teams activity feed later is an addition rather than a redesign |
| App Insights ingestion becomes the largest bill line | Cost overrun | Daily cap set at provisioning; audit deliberately not routed there |
| A future change re-enables local auth on a resource and silently regresses SFI | Compliance regression invisible in review | CI asserts local auth is disabled on every provisioned resource and fails the build, rather than reporting a warning (T129) |
| Private endpoints break local development and emulator workflows | Developer friction, temptation to re-enable public access | Local development uses emulators over `docker compose`; no developer ever needs data-plane access to a deployed resource |

## Next Step

`/speckit.tasks` — generate a dependency-ordered task list. Suggested build order follows the
story priorities: US1 (P1) delivers the MVP, US2 (P2) adds the differentiating capability, US3
(P3) makes multi-day reviews possible, and US4/US5/US6 (all P4) are independent peers that can
proceed in parallel or be dropped without affecting the rest.
