---
description: "Task list for BlinkMark Core implementation"
---

# Tasks: BlinkMark Core

**Input**: Design documents from `/specs/001-blinkmark-core/`
**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/)

**Tests**: Test tasks are included and are **mandatory**, not optional. Constitution v1.1.0
Principle VI requires automated tests for authorization denial paths, retention expiry and the
30-day ceiling, anchor resolution and orphaning, and content sanitization. Clarification Q4 adds
WCAG 2.1 AA, so accessibility tests are also required. Other testing is at the team's discretion.

**Organization**: Tasks are grouped by user story so each story can be implemented, tested, and
delivered independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1–US6)
- Exact file paths are included in every task

## Path Conventions

Web application structure per [plan.md](plan.md): `backend/src/`, `backend/tests/`,
`frontend/src/`, `frontend/tests/`, `infra/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Repository skeleton and toolchain

- [ ] T001 Create directory structure per plan in `backend/src/`, `backend/tests/`, `frontend/`, `infra/`
- [ ] T002 Initialize .NET 9 solution `BlinkMark.sln` with projects `BlinkMark.Core`, `BlinkMark.Infrastructure`, `BlinkMark.Api`, `BlinkMark.Preview`, `BlinkMark.Jobs` under `backend/src/`
- [ ] T003 [P] Initialize React 18 + TypeScript + Vite application in `frontend/`
- [ ] T004 [P] Configure linting and formatting in `.editorconfig`, `frontend/.eslintrc.json`, `frontend/.prettierrc`
- [ ] T005 [P] Create `docker-compose.yml` at repository root with Redis, Azurite, and Cosmos DB emulator for local development
- [ ] T006 [P] Create CI workflow in `.github/workflows/ci.yml` running build, lint, tests, secret scanning, and dependency vulnerability scanning (constitution Quality Gates)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Infrastructure, domain primitives, and cross-cutting concerns that every user story depends on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Infrastructure as code

- [ ] T007 Create `infra/main.bicep` composing all resource modules with parameters per environment
- [ ] T008 [P] Create `infra/modules/storage.bicep` provisioning **two** storage accounts — one with hierarchical namespace for blobs, one standard StorageV2 for Table and Queue (they cannot coexist, see research.md R6)
- [ ] T009 [P] Create `infra/modules/cosmos.bicep` provisioning serverless account with containers `files` (PK `/id`), `comments` (PK `/fileId`), `notifications` (PK `/recipientId`), `userPrefs` (PK `/id`), TTL enabled per data-model.md
- [ ] T010 [P] Create `infra/modules/redis.bicep` provisioning Azure Cache for Redis Basic C0
- [ ] T011 [P] Create `infra/modules/container-apps.bicep` with environment plus four apps: `api` (min-replicas 1), `preview` (separate ingress hostname), `notifications` (KEDA queue scaler), `reconciliation` (cron job)
- [ ] T012 [P] Create `infra/modules/observability.bicep` with Application Insights and an explicit daily ingestion cap
- [ ] T013 [P] Create `infra/entra/setup-app-registrations.ps1` registering the API and SPA applications as **single-tenant**, exposing scopes `Files.ReadWrite` and `Comments.ReadWrite`

### Domain and persistence

- [ ] T014 Implement domain models `FileRecord`, `Comment`, `Anchor`, `AuditEntry` and ULID identifier generation in `backend/src/BlinkMark.Core/Models/`
- [ ] T015 Implement retention rules in `backend/src/BlinkMark.Core/Retention/RetentionPolicy.cs` computing `maxExpiresAt` at upload and validating every `expiresAt` write against it
- [ ] T016 Implement Cosmos repositories in `backend/src/BlinkMark.Infrastructure/Cosmos/` with per-item TTL and point-read access for files
- [ ] T017 Implement blob adapter in `backend/src/BlinkMark.Infrastructure/Blob/BlobFileStore.cs` using the Set Blob Expiry API in absolute mode
- [ ] T018 Implement audit repository in `backend/src/BlinkMark.Infrastructure/Audit/TableAuditStore.cs` exposing **only** `AppendAsync` — no update or delete method may exist
- [ ] T019 Implement Redis adapter in `backend/src/BlinkMark.Infrastructure/Redis/RedisStore.cs` for presence keys, rate-limit counters, and quota cache

### Cross-cutting

- [ ] T020 Configure Entra ID JWT validation in `backend/src/BlinkMark.Api/Auth/AuthenticationSetup.cs` pinned to the single owning tenant issuer and audience
- [ ] T021 Implement authorization policies in `backend/src/BlinkMark.Api/Auth/AuthorizationPolicies.cs` for tenant member, file viewer, and file owner
- [ ] T022 Implement audit service and correlation ID middleware in `backend/src/BlinkMark.Api/Middleware/` propagating the correlation ID through to storage calls
- [ ] T023 Implement RFC 9457 problem details handling and log redaction in `backend/src/BlinkMark.Api/Middleware/ErrorHandling.cs`, excluding file content, comment text, and credentials
- [ ] T024 Wire managed identity and Key Vault configuration in `backend/src/BlinkMark.Api/Program.cs` and `backend/src/BlinkMark.Infrastructure/Configuration/`
- [ ] T025 Create API host skeleton with health endpoint and OpenAPI document generation in `backend/src/BlinkMark.Api/Program.cs`
- [ ] T026 Create preview host skeleton in `backend/src/BlinkMark.Preview/Program.cs` serving on a separate hostname with `Content-Security-Policy` including `sandbox` and no session cookie access
- [ ] T027 Create frontend shell with MSAL authentication, routing, and API client in `frontend/src/services/` and `frontend/src/App.tsx`

### Test infrastructure

- [ ] T028 Set up xUnit with Testcontainers in `backend/tests/`, plus Vitest and Playwright with axe-core in `frontend/tests/`
- [ ] T029 Create hostile content fixture corpus in `backend/tests/fixtures/hostile/` covering inline script, event handlers, external resource loads, frame-busting, and extension/content mismatch

**Checkpoint**: Foundation ready — user story implementation can begin

---

## Phase 3: User Story 1 - Share a draft and preview it safely (Priority: P1) 🎯 MVP

**Goal**: A user uploads an HTML or Markdown file, gets a link, colleagues in the tenant sign in and see it rendered safely, and it deletes itself after 24 hours.

**Independent Test**: Upload an HTML file and a Markdown file, open the link in a second authenticated session, confirm both render, confirm an unauthenticated session is refused at every step, and confirm the file is inaccessible and physically deleted after expiry.

### Tests (mandatory under Principle VI)

- [ ] T030 [P] [US1] Contract test for file endpoints against `contracts/openapi.yaml` in `backend/tests/contract/FileEndpointsContractTests.cs`
- [ ] T031 [P] [US1] Integration test for authorization denial paths — unauthenticated, cross-tenant, and expired — in `backend/tests/integration/AuthorizationDenialTests.cs`
- [ ] T032 [P] [US1] Integration test for sanitization against the hostile corpus in `backend/tests/integration/SanitizationTests.cs`
- [ ] T033 [P] [US1] Integration test for 24-hour default expiry and physical deletion of blob, document, and comments in `backend/tests/integration/RetentionExpiryTests.cs`

### Implementation

- [ ] T034 [US1] Implement upload validation in `backend/src/BlinkMark.Core/Upload/UploadValidator.cs` checking declared type, extension, size, and extension-versus-content agreement
- [ ] T035 [US1] Implement `POST /api/files` in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs` storing under a system-generated ULID, setting blob expiry, and writing `maxExpiresAt`
- [ ] T036 [P] [US1] Implement Markdown rendering with Markdig (raw HTML disabled) in `backend/src/BlinkMark.Core/Rendering/MarkdownRenderer.cs`
- [ ] T037 [P] [US1] Implement allowlist HTML sanitization and external reference neutralization with Ganss.Xss in `backend/src/BlinkMark.Core/Rendering/HtmlSanitizerService.cs`
- [ ] T038 [US1] Generate and store the sanitized render and normalized text projection with a pinned `renderVersion` in `backend/src/BlinkMark.Core/Rendering/RenderPipeline.cs`
- [ ] T039 [US1] Implement preview serving in `backend/src/BlinkMark.Preview/Endpoints/PreviewEndpoints.cs` returning the stored render artifact with sandbox headers
- [ ] T040 [US1] Implement `GET /api/files/{fileId}` in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs` returning `previewUrl`, `accessScopeNotice`, and `retentionNotice`
- [ ] T041 [US1] Implement `GET /api/files` returning the caller's live files with quota status in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [ ] T042 [US1] Implement the expired-reads-as-deleted guard in `backend/src/BlinkMark.Core/Retention/ExpiryGuard.cs` applied to every read path
- [ ] T043 [US1] Implement live-file quota and upload rate limiting in `backend/src/BlinkMark.Core/Quotas/QuotaService.cs` with Redis counters confirmed against Cosmos before commit
- [ ] T044 [US1] Implement the reconciliation job purging expired blobs, documents, and comments in `backend/src/BlinkMark.Jobs/Reconciliation/RetentionReconciler.cs`
- [ ] T045 [US1] Emit audit entries for upload, view, and preview in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [ ] T046 [P] [US1] Build the upload screen with validation messaging and quota display in `frontend/src/pages/Upload.tsx`
- [ ] T047 [P] [US1] Build the file list with remaining-time display in `frontend/src/pages/FileList.tsx`
- [ ] T048 [P] [US1] Build the sandboxed iframe preview host component in `frontend/src/components/preview/PreviewFrame.tsx`
- [ ] T049 [P] [US1] Add access scope and no-backup notices to the upload flow in `frontend/src/components/upload/ScopeNotice.tsx`
- [ ] T050 [US1] Run and fix an accessibility pass over the US1 flows in `frontend/tests/a11y/upload-preview.spec.ts`

**Checkpoint**: MVP complete and independently deliverable

---

## Phase 4: User Story 2 - Comment on a specific passage (Priority: P2)

**Goal**: Reviewers highlight text or select a region, attach comments that stay pinned to that passage, reply to each other, and never silently lose a comment.

**Independent Test**: Add a comment to a text range, reload in a different session and confirm it appears at the same passage; then make the passage unresolvable and confirm the comment surfaces as orphaned with its quoted context rather than disappearing or moving.

### Tests (mandatory under Principle VI)

- [ ] T051 [P] [US2] Contract test for comment endpoints against `contracts/openapi.yaml` in `backend/tests/contract/CommentEndpointsContractTests.cs`
- [ ] T052 [P] [US2] Integration test for anchor resolution and orphaning, including duplicate-passage disambiguation, in `backend/tests/integration/AnchorResolutionTests.cs`
- [ ] T053 [P] [US2] Accessibility test for keyboard-only passage selection and comment creation in `frontend/tests/a11y/keyboard-commenting.spec.ts`

### Implementation

- [ ] T054 [US2] Implement W3C selector computation and resolution over the text projection in `backend/src/BlinkMark.Core/Anchoring/AnchorService.cs`
- [ ] T055 [US2] Implement comment repository operations with TTL derived from the parent file in `backend/src/BlinkMark.Infrastructure/Cosmos/CommentRepository.cs`
- [ ] T056 [US2] Implement `POST /api/files/{fileId}/comments` taking author identity from the token only in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [ ] T057 [US2] Implement `GET /api/files/{fileId}/comments` as a single-partition query in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [ ] T058 [US2] Implement reply threading with `threadId` and `parentId` in `backend/src/BlinkMark.Core/Comments/ThreadService.cs`
- [ ] T059 [US2] Implement edit and delete of own comments with soft delete in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [ ] T060 [US2] Implement orphan state computation and persistence in `backend/src/BlinkMark.Core/Anchoring/OrphanDetector.cs`
- [ ] T061 [US2] Emit audit entries for comment create, edit, and delete in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [ ] T062 [P] [US2] Integrate `dom-anchor-text-quote` and `dom-anchor-text-position` client-side resolution in `frontend/src/services/anchoring.ts`
- [ ] T063 [P] [US2] Build pointer-based text selection to comment in `frontend/src/components/comments/TextSelection.tsx`
- [ ] T064 [P] [US2] Build the keyboard selection path producing identical anchor data in `frontend/src/components/comments/KeyboardSelection.tsx`
- [ ] T065 [P] [US2] Build region selection with a non-dragging alternative in `frontend/src/components/comments/RegionSelection.tsx`
- [ ] T066 [P] [US2] Build the comment sidebar with in-place highlights and a separate orphaned section in `frontend/src/components/comments/CommentSidebar.tsx`
- [ ] T067 [P] [US2] Render comment bodies as literal text with no markup interpretation in `frontend/src/components/comments/CommentBody.tsx`

**Checkpoint**: Review capability complete — BlinkMark is now a review tool, not a viewer

---

## Phase 5: User Story 3 - Keep a file alive longer (Priority: P3)

**Goal**: Owners see remaining time, extend retention up to the 30-day ceiling, delete early, and download their file with all its comments.

**Independent Test**: Confirm the displayed expiry is 24 hours out, extend it and confirm enforcement, attempt to exceed 30 days from upload and confirm refusal, then download and confirm every comment including orphans is present.

### Tests (mandatory under Principle VI)

- [ ] T068 [P] [US3] Integration test for the 30-day ceiling and owner-only enforcement across every interface in `backend/tests/integration/RetentionCeilingTests.cs`
- [ ] T069 [P] [US3] Integration test for download bundle completeness including orphaned comments in `backend/tests/integration/DownloadBundleTests.cs`

### Implementation

- [ ] T070 [US3] Implement `PATCH /api/files/{fileId}/retention` with ceiling validation at the data layer in `backend/src/BlinkMark.Api/Endpoints/RetentionEndpoints.cs`
- [ ] T071 [US3] Update blob expiry and Cosmos TTL together on extension in `backend/src/BlinkMark.Core/Retention/RetentionService.cs`
- [ ] T072 [US3] Add comment TTL synchronization after extension to the reconciliation job in `backend/src/BlinkMark.Jobs/Reconciliation/CommentTtlSync.cs`
- [ ] T073 [US3] Implement `DELETE /api/files/{fileId}` for owners in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [ ] T074 [US3] Implement the download bundle builder combining content and all comments with author, time, thread, and anchored passage in `backend/src/BlinkMark.Core/Export/DownloadBundleBuilder.cs`
- [ ] T075 [US3] Implement `GET /api/files/{fileId}/download` restricted to the owner in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [ ] T076 [P] [US3] Build the retention display and extension control in `frontend/src/components/retention/RetentionControl.tsx`
- [ ] T077 [P] [US3] Build the download action with the copy-outlives-expiry notice in `frontend/src/components/retention/DownloadButton.tsx`
- [ ] T078 [US3] Emit audit entries for retention change with previous and new expiry, and for download, in `backend/src/BlinkMark.Api/Endpoints/RetentionEndpoints.cs`

**Checkpoint**: Multi-day reviews now possible

---

## Phase 6: User Story 4 - Find out when someone comments (Priority: P4)

**Goal**: File owners and thread participants are notified of new comment activity, asynchronously and consolidated.

**Independent Test**: Have a second user comment and confirm the owner receives a notification naming the commenter, the file, and a working link — while confirming the commenter's action completed immediately regardless of delivery.

### Tests

- [ ] T079 [P] [US4] Integration test confirming comment creation succeeds and returns no error when the notification channel fails in `backend/tests/integration/NotificationIsolationTests.cs`

### Implementation

- [ ] T080 [US4] Enqueue a notification message on comment creation in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [ ] T081 [US4] Implement the queue-scaled dispatcher in `backend/src/BlinkMark.Jobs/Notifications/NotificationDispatcher.cs`
- [ ] T082 [US4] Implement coalescing by recipient, file, and time window in `backend/src/BlinkMark.Jobs/Notifications/Coalescer.cs`
- [ ] T083 [US4] Implement the Microsoft Graph email sender in `backend/src/BlinkMark.Infrastructure/Graph/GraphMailSender.cs`, documenting the required Application Access Policy scoping
- [ ] T084 [US4] Implement self-notification suppression and notification preference checks in `backend/src/BlinkMark.Jobs/Notifications/RecipientResolver.cs`
- [ ] T085 [P] [US4] Implement `GET`/`PATCH /api/me/preferences` in `backend/src/BlinkMark.Api/Endpoints/PreferenceEndpoints.cs`
- [ ] T086 [P] [US4] Build the notification preferences screen in `frontend/src/pages/Preferences.tsx`

**Checkpoint**: Async review loop closed

---

## Phase 7: User Story 5 - Let an AI agent work on the file for me (Priority: P4)

**Goal**: An AI agent reads files and comments and creates comments strictly on behalf of a signed-in user, with visible attribution and audit distinction.

**Independent Test**: Using a delegated agent credential, list files, read content and comments, and create a comment — then verify the agent cannot reach anything the user cannot, that attribution names both, and that every action is marked agent-initiated in the audit trail.

### Tests (mandatory under Principle VI)

- [ ] T087 [P] [US5] Contract test verifying the served MCP manifest matches `contracts/mcp-tools.json` in `backend/tests/contract/McpManifestContractTests.cs`
- [ ] T088 [P] [US5] Integration test confirming an agent cannot exceed the represented user's permissions and cannot act for a signed-out user in `backend/tests/integration/AgentAuthorizationTests.cs`

### Implementation

- [ ] T089 [US5] Implement On-Behalf-Of token exchange and acting-agent identity extraction in `backend/src/BlinkMark.Api/Auth/OnBehalfOfHandler.cs`
- [ ] T090 [US5] Implement the MCP server endpoint over Streamable HTTP in `backend/src/BlinkMark.Api/Mcp/McpServer.cs`
- [ ] T091 [US5] Implement the `list_my_files` and `read_file_content` tools in `backend/src/BlinkMark.Api/Mcp/Tools/FileTools.cs`
- [ ] T092 [US5] Implement the `list_comments` and `create_comment` tools in `backend/src/BlinkMark.Api/Mcp/Tools/CommentTools.cs`
- [ ] T093 [US5] Implement the `extend_retention` tool in `backend/src/BlinkMark.Api/Mcp/Tools/RetentionTools.cs`
- [ ] T094 [US5] Implement `GET /api/files/{fileId}/content` returning the structured projection in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [ ] T095 [US5] Persist `actingAgentId` on comments and surface it in responses in `backend/src/BlinkMark.Infrastructure/Cosmos/CommentRepository.cs`
- [ ] T096 [US5] Implement agent rate limiting and ensure agent actions count against the represented user's quota in `backend/src/BlinkMark.Core/Quotas/AgentRateLimiter.cs`
- [ ] T097 [P] [US5] Display agent attribution on comments in `frontend/src/components/comments/AgentBadge.tsx`

**Checkpoint**: Agent access complete and provably no broader than the user

---

## Phase 8: User Story 6 - Know who else is in the document right now (Priority: P4)

**Goal**: Viewers see who else has the file open and how many, updating live, degrading silently when unavailable.

**Independent Test**: Open one file in two authenticated sessions and confirm each sees the other appear and disappear, including when the second session is terminated abruptly rather than closed cleanly.

### Tests

- [ ] T098 [P] [US6] Integration test confirming presence is refused to unauthorized users and that stale viewers are removed within the staleness window in `backend/tests/integration/PresenceTests.cs`
- [ ] T099 [P] [US6] Accessibility test confirming presence changes announce politely and never move focus in `frontend/tests/a11y/presence-announcements.spec.ts`

### Implementation

- [ ] T100 [US6] Implement the Redis presence store with per-viewer TTL keys and pub/sub fan-out in `backend/src/BlinkMark.Infrastructure/Redis/PresenceStore.cs`
- [ ] T101 [US6] Implement the SSE endpoint per `contracts/presence-sse.md` in `backend/src/BlinkMark.Api/Presence/PresenceStreamEndpoint.cs`
- [ ] T102 [US6] Implement the heartbeat endpoint in `backend/src/BlinkMark.Api/Presence/HeartbeatEndpoint.cs`
- [ ] T103 [US6] Implement `snapshot`, `join`, `leave`, and `closed` event emission in `backend/src/BlinkMark.Api/Presence/PresenceEventPublisher.cs`
- [ ] T104 [US6] Implement per-user deduplication, overflow counting, and agent-assisted marking in `backend/src/BlinkMark.Core/Presence/PresenceProjection.cs`
- [ ] T105 [US6] Implement graceful degradation so preview and commenting are unaffected when Redis is unavailable in `backend/src/BlinkMark.Api/Presence/PresenceStreamEndpoint.cs`
- [ ] T106 [P] [US6] Build the presence display with avatars, count, and overflow indicator in `frontend/src/components/presence/PresenceBar.tsx`
- [ ] T107 [P] [US6] Build the SSE client with reconnect backoff and a polite ARIA live region in `frontend/src/services/presenceClient.ts`

**Checkpoint**: All six user stories complete

---

## Phase 9: Polish & Cross-Cutting Concerns

- [ ] T108 [P] Build a load test sustaining 500 concurrent users in `backend/tests/load/ConcurrencyLoadTest.cs` verifying preview, comment, and upload targets hold
- [ ] T109 [P] Verify p95 latency targets for preview, comment, and upload in `backend/tests/load/LatencyValidation.cs`
- [ ] T110 [P] Run a full WCAG 2.1 Level AA audit across every user story flow and record results in `frontend/tests/a11y/full-audit.spec.ts`
- [ ] T111 [P] Expand the hostile content corpus and verify CSP violation reporting in `backend/tests/fixtures/hostile/` and `backend/tests/integration/SanitizationTests.cs`
- [ ] T112 [P] Configure Application Insights dashboards and verify the daily ingestion cap is enforced in `infra/modules/observability.bicep`
- [ ] T113 Apply a resource lock to the audit storage account and add a test asserting no delete or merge operation against the audit table exists in `backend/tests/integration/AuditImmutabilityTests.cs`
- [ ] T114 [P] Write `README.md` linking the constitution, spec, and quickstart
- [ ] T115 Validate deployment with `azd up` and run the four verification checks from [quickstart.md](quickstart.md)
- [ ] T116 Submit a PATCH amendment to `.specify/memory/constitution.md` raising it to v1.1.1, correcting the Static Web Apps preview-origin justification identified in [plan.md](plan.md)
- [ ] T117 [P] Verify the application degrades quietly with Redis stopped, confirming preview and commenting are unaffected

---

## Dependencies

### Phase dependencies

```
Setup (T001–T006)
    ↓
Foundational (T007–T029)  ← BLOCKS EVERYTHING
    ↓
US1 (T030–T050)  P1  🎯 MVP
    ↓
US2 (T051–T067)  P2   — needs US1 render pipeline and text projection
    ↓
US3 (T068–T078)  P3   — download needs US2 comments to be worth bundling
    ↓
    ├── US4 (T079–T086)  P4  — needs US2 comments
    ├── US5 (T087–T097)  P4  — needs US1 + US2 capabilities to expose
    └── US6 (T098–T107)  P4  — needs only US1 + Redis
    ↓
Polish (T108–T117)
```

### Story dependencies

| Story | Depends on | Why |
|---|---|---|
| US1 | Foundational only | Fully self-contained MVP |
| US2 | US1 | Anchors are computed against US1's text projection |
| US3 | US1; US2 for a complete download bundle | Retention alone needs only US1 |
| US4 | US2 | Nothing to notify about without comments |
| US5 | US1, US2 | Agent tools expose file and comment capabilities |
| US6 | US1 | Presence attaches to a viewed file, not to comments |

**US4, US5, and US6 are peers at P4 and have no dependency on each other.** Once US2 is done, three teams can take one each.

### Notable within-story ordering

- T038 (render + projection) blocks T054 (anchor computation) — anchors need the projection to exist
- T043 (quota) must precede T035 completion — upload enforces the quota
- T054 (anchor service) blocks T060 (orphan detection) and T062 (client resolution)
- T100 (presence store) blocks T101–T105
- T089 (OBO handler) blocks all MCP tools T090–T093

---

## Parallel Execution Examples

**Foundational infrastructure** — six Bicep modules, six different files:

```
T008 storage.bicep │ T009 cosmos.bicep │ T010 redis.bicep
T011 container-apps.bicep │ T012 observability.bicep │ T013 entra registrations
```

**US1 mandatory tests** — write all four before implementation:

```
T030 contract │ T031 authorization │ T032 sanitization │ T033 retention
```

**US1 frontend** — four independent components:

```
T046 Upload.tsx │ T047 FileList.tsx │ T048 PreviewFrame.tsx │ T049 ScopeNotice.tsx
```

**US2 frontend** — six independent components:

```
T062 anchoring.ts │ T063 TextSelection.tsx │ T064 KeyboardSelection.tsx
T065 RegionSelection.tsx │ T066 CommentSidebar.tsx │ T067 CommentBody.tsx
```

**Post-US2 story parallelism** — three complete stories at once:

```
US4 (T079–T086) │ US5 (T087–T097) │ US6 (T098–T107)
```

---

## Implementation Strategy

### MVP scope

**Phase 1 + Phase 2 + Phase 3 (T001–T050).** That delivers upload, safe preview, tenant-only
access, and automatic 24-hour deletion — a working product that replaces emailing HTML around,
and is independently demonstrable and deployable.

### Incremental delivery

1. **T001–T050** — MVP. Ship it. Get people uploading before building the review layer.
2. **+T051–T067** — commenting. This is the release that makes BlinkMark a review tool.
3. **+T068–T078** — retention control and download. Removes the "my review expired overnight" complaint.
4. **+T079–T107** — the three P4 awareness features, in any order or in parallel.
5. **+T108–T117** — hardening, performance validation, and the constitution amendment.

### Build-order warnings

**Build the keyboard path (T064) alongside the pointer path (T063), not after it.** This is the
single largest schedule risk in the plan. Retrofitting keyboard-accessible passage selection means
rewriting the US2 interaction model.

**Do not defer T032 (sanitization tests).** A stored-XSS discovered after the preview component is
built is a rewrite, not a patch.

**Verify T033 deletes physically, not just logically.** A file hidden from the API but still in
blob storage fails SC-006 and the entire compliance premise.

---

## Summary

| Metric | Value |
|---|---|
| Total tasks | 117 |
| Setup | 6 |
| Foundational | 23 |
| US1 (P1, MVP) | 21 |
| US2 (P2) | 17 |
| US3 (P3) | 11 |
| US4 (P4) | 8 |
| US5 (P4) | 11 |
| US6 (P4) | 10 |
| Polish | 10 |
| Marked `[P]` | 52 |
| Mandatory test tasks | 12 |
