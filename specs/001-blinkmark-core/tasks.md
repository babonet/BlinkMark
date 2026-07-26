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

- [X] T001 Create directory structure per plan in `backend/src/`, `backend/tests/`, `frontend/`, `infra/`
- [X] T002 Initialize .NET 9 solution `BlinkMark.sln` with projects `BlinkMark.Core`, `BlinkMark.Infrastructure`, `BlinkMark.Api`, `BlinkMark.Preview`, `BlinkMark.Jobs` under `backend/src/`
- [X] T003 [P] Initialize React 18 + TypeScript + Vite application in `frontend/`
- [X] T004 [P] Configure linting and formatting in `.editorconfig`, `frontend/.eslintrc.json`, `frontend/.prettierrc`
- [X] T005 [P] Create `docker-compose.yml` at repository root with Redis, Azurite, and Cosmos DB emulator for local development
- [X] T006 [P] Create CI workflow in `.github/workflows/ci.yml` running build, lint, tests, secret scanning, and dependency vulnerability scanning (constitution Quality Gates)
- [X] T134 [P] Add an SFI compliance gate to `.github/workflows/ci.yml` that fails the build if any Bicep template would enable local authentication (shared key, Cosmos local auth, Redis access keys, Log Analytics local auth, Key Vault access policies) or if any connection string, account key, or client secret appears in source, configuration, or pipeline definitions

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Infrastructure, domain primitives, and cross-cutting concerns that every user story depends on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Infrastructure as code

- [X] T007 Create `infra/main.bicep` composing all resource modules with parameters per environment, targeting subscription `46a174f6-0602-4df8-9fb0-f8e8248bcb8f`, resource group `rg-blinkmark`, region `eastus`
- [X] T129 Create `infra/modules/identity.bicep` provisioning the user-assigned managed identity used by all four Container Apps, and all data-plane role assignments — Storage Blob/Table/Queue Data roles, Cosmos DB Built-in Data Contributor via `sqlRoleAssignments`, Redis Data Owner access policy, Key Vault Crypto User (research.md R14)
- [X] T130 Create `infra/modules/network.bicep` provisioning the VNet, subnets, private DNS zones, and private endpoints for both storage accounts, Cosmos, Redis, and Key Vault, with `publicNetworkAccess: Disabled` on each (`[SFI-NS2.2.1]`)
- [X] T008 [P] Create `infra/modules/storage.bicep` provisioning **two** storage accounts — one with hierarchical namespace for blobs, one standard StorageV2 for Table and Queue (they cannot coexist, see research.md R6) — with `allowSharedKeyAccess: false`, `defaultToOAuthAuthentication: true`, `allowBlobPublicAccess: false`, `minimumTlsVersion: TLS1_2` (`[SFI-ID4.2.1]`)
- [X] T009 [P] Create `infra/modules/cosmos.bicep` provisioning serverless account with `disableLocalAuth: true` and containers `files` (PK `/id`), `comments` (PK `/fileId`), `notifications` (PK `/recipientId`), `userPrefs` (PK `/id`), TTL enabled per data-model.md (`[SFI-ID4.2.3]`)
- [X] T010 [P] Create `infra/modules/redis.bicep` provisioning Azure Cache for Redis Basic C0 with Entra authentication enabled, `disableAccessKeyAuthentication: true`, `minimumTlsVersion: 1.2`, and the non-TLS port disabled (`[SFI-ID4.2.7]`, C+E FUN Security P0)
- [X] T011 [P] Create `infra/modules/container-apps.bicep` with a VNet-integrated environment plus four apps: `api` (min-replicas 1), `preview` (separate ingress hostname), `notifications` (KEDA queue scaler), `reconciliation` (cron job) — all bound to the user-assigned managed identity, with no `secrets` block
- [X] T012 [P] Create `infra/modules/observability.bicep` with Application Insights and Log Analytics, `DisableLocalAuth: true`, Entra-authenticated ingestion, and an explicit daily ingestion cap
- [X] T131 [P] Create `infra/modules/keyvault.bicep` with `enableRbacAuthorization: true`, no access policies, no standing human role assignments, holding the preview-token signing **key** (not a secret) for in-place signing (research.md R13, R14)
- [X] T013 [P] Create `infra/entra/setup-app-registrations.ps1` registering the API and SPA applications as **single-tenant** with **no client secrets and no certificates**, exposing scopes `Files.ReadWrite` and `Comments.ReadWrite`, and adding a **federated identity credential backed by the user-assigned managed identity** so the API can perform the On-Behalf-Of exchange without a credential (`[SFI-ID4.1.1]`, research.md R14)
- [X] T132 [P] Configure GitHub Actions workload identity federation to the target subscription in `.github/workflows/deploy.yml` — no service principal secret, no Static Web Apps deployment token, no publish profile (`[SFI-ID4.1.2]`)
- [X] T121 [P] Enforce and verify encryption at rest on both storage accounts and Cosmos, and TLS 1.2+ minimum with HTTPS-only ingress on every service, in `infra/modules/storage.bicep`, `infra/modules/cosmos.bicep`, and `infra/modules/container-apps.bicep` (FR-010)

### Domain and persistence

- [X] T014 Implement domain models `FileRecord`, `Comment`, `Anchor`, `AuditEntry` and ULID identifier generation in `backend/src/BlinkMark.Core/Models/`
- [X] T015 Implement retention rules in `backend/src/BlinkMark.Core/Retention/RetentionPolicy.cs` computing `maxExpiresAt` at upload and validating every `expiresAt` write against it
- [X] T016 Implement Cosmos repositories in `backend/src/BlinkMark.Infrastructure/Cosmos/` with per-item TTL and point-read access for files
- [X] T017 Implement blob adapter in `backend/src/BlinkMark.Infrastructure/Blob/BlobFileStore.cs` using the Set Blob Expiry API in absolute mode
- [X] T018 Implement audit repository in `backend/src/BlinkMark.Infrastructure/Audit/TableAuditStore.cs` exposing **only** `AppendAsync` — no update or delete method may exist
- [X] T019 Implement Redis adapter in `backend/src/BlinkMark.Infrastructure/Redis/RedisStore.cs` for presence keys, rate-limit counters, and quota cache, authenticating with Microsoft Entra ID via managed identity (no access key)
- [X] T133 Implement preview-token signing through the Key Vault sign operation in `backend/src/BlinkMark.Infrastructure/Crypto/KeyVaultSigner.cs` so the signing key never leaves Key Vault (Principle VII)

### Cross-cutting

- [X] T020 Configure Entra ID JWT validation in `backend/src/BlinkMark.Api/Auth/AuthenticationSetup.cs` pinned to the single owning tenant issuer and audience
- [X] T021 Implement authorization policies in `backend/src/BlinkMark.Api/Auth/AuthorizationPolicies.cs` for tenant member, file viewer, and file owner
- [X] T022 Implement audit service and correlation ID middleware in `backend/src/BlinkMark.Api/Middleware/` propagating the correlation ID through to storage calls
- [X] T023 Implement RFC 9457 problem details handling and log redaction in `backend/src/BlinkMark.Api/Middleware/ErrorHandling.cs`, excluding file content, comment text, and credentials
- [X] T024 Wire managed identity and Key Vault configuration in `backend/src/BlinkMark.Api/Program.cs` and `backend/src/BlinkMark.Infrastructure/Configuration/` using `DefaultAzureCredential` throughout — no connection strings, no account keys, no client secrets in configuration or environment
- [X] T025 Create API host skeleton with health endpoint and OpenAPI document generation in `backend/src/BlinkMark.Api/Program.cs`
- [X] T118 Implement preview token issuance and validation per `contracts/preview-origin.md` in `backend/src/BlinkMark.Core/Preview/PreviewTokenService.cs` — 15-minute ceiling, audience pinned to the preview host, scoped to one `fileId` and one `renderVersion`, signing key from Key Vault (FR-001, FR-003, Principle I). **Blocks T026 and T039**
- [X] T026 Create preview host skeleton in `backend/src/BlinkMark.Preview/Program.cs` validating the preview token as its sole credential, serving on a separate hostname with the response headers required by `contracts/preview-origin.md`, and never accepting a session cookie or Entra token
- [X] T027 Create frontend shell with MSAL authentication, routing, and API client in `frontend/src/services/` and `frontend/src/App.tsx`

### Test infrastructure

- [X] T028 Set up xUnit with Testcontainers in `backend/tests/`, plus Vitest and Playwright with axe-core in `frontend/tests/`
- [X] T029 Create hostile content fixture corpus in `backend/tests/fixtures/hostile/` covering inline script, event handlers, external resource loads, frame-busting, and extension/content mismatch

**Checkpoint**: Foundation ready — user story implementation can begin

---

## Phase 3: User Story 1 - Share a draft and preview it safely (Priority: P1) 🎯 MVP

**Goal**: A user uploads an HTML or Markdown file, gets a link, colleagues in the tenant sign in and see it rendered safely, and it deletes itself after 24 hours.

**Independent Test**: Upload an HTML file and a Markdown file, open the link in a second authenticated session, confirm both render, confirm an unauthenticated session is refused at every step, and confirm the file is inaccessible and physically deleted after expiry.

### Tests (mandatory under Principle VI)

- [X] T030 [P] [US1] Contract test for file endpoints against `contracts/openapi.yaml` in `backend/tests/contract/FileEndpointsContractTests.cs`
- [X] T031 [P] [US1] Integration test for authorization denial paths — unauthenticated, cross-tenant, and expired — in `backend/tests/integration/AuthorizationDenialTests.cs`
- [X] T032 [P] [US1] Integration test for sanitization against the hostile corpus in `backend/tests/integration/SanitizationTests.cs`
- [X] T033 [P] [US1] Integration test for 24-hour default expiry and physical deletion of blob, document, and comments in `backend/tests/integration/RetentionExpiryTests.cs`
- [X] T119 [P] [US1] Contract test for the preview origin against `contracts/preview-origin.md` in `backend/tests/contract/PreviewOriginContractTests.cs` — asserting refusal without a token, with an expired token, with a token minted for a different file, and with a token minted for the API audience; and asserting a token is reusable within its lifetime

### Implementation

- [X] T034 [US1] Implement upload validation in `backend/src/BlinkMark.Core/Upload/UploadValidator.cs` checking declared type, extension, size, and extension-versus-content agreement
- [X] T035 [US1] Implement `POST /api/files` in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs` storing under a system-generated ULID, setting blob expiry, and writing `maxExpiresAt`
- [X] T036 [P] [US1] Implement Markdown rendering with Markdig (raw HTML disabled) in `backend/src/BlinkMark.Core/Rendering/MarkdownRenderer.cs`
- [X] T037 [P] [US1] Implement allowlist HTML sanitization and external reference neutralization with Ganss.Xss in `backend/src/BlinkMark.Core/Rendering/HtmlSanitizerService.cs`
- [X] T038 [US1] Generate and store the sanitized render and normalized text projection with a pinned `renderVersion` in `backend/src/BlinkMark.Core/Rendering/RenderPipeline.cs`
- [X] T039 [US1] Implement preview serving in `backend/src/BlinkMark.Preview/Endpoints/PreviewEndpoints.cs` returning the stored render artifact only after preview-token validation, with the headers mandated by `contracts/preview-origin.md`
- [X] T040 [US1] Implement `GET /api/files/{fileId}` in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs` returning `previewUrl` carrying a freshly minted preview token, plus `accessScopeNotice` and `retentionNotice`
- [X] T041 [US1] Implement `GET /api/files` returning the caller's live files with quota status in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [X] T042 [US1] Implement the expired-reads-as-deleted guard in `backend/src/BlinkMark.Core/Retention/ExpiryGuard.cs` applied to every read path
- [X] T043 [US1] Implement live-file quota and upload rate limiting in `backend/src/BlinkMark.Core/Quotas/QuotaService.cs` with Redis counters confirmed against Cosmos before commit
- [X] T044 [US1] Implement the reconciliation job purging expired blobs, documents, and comments in `backend/src/BlinkMark.Jobs/Reconciliation/RetentionReconciler.cs`
- [X] T045 [US1] Emit audit entries for upload and view in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [X] T120 [US1] Emit the `preview` audit entry from the preview host using the token's subject, acting agent, and correlation ID, in `backend/src/BlinkMark.Preview/Endpoints/PreviewEndpoints.cs` (FR-041, FR-042)
- [X] T046 [P] [US1] Build the upload screen with validation messaging and quota display in `frontend/src/pages/Upload.tsx`
- [X] T047 [P] [US1] Build the file list with remaining-time display in `frontend/src/pages/FileList.tsx`
- [X] T048 [P] [US1] Build the sandboxed iframe preview host component in `frontend/src/components/preview/PreviewFrame.tsx`
- [X] T123 [P] [US1] Implement keyboard focus management for the preview region in `frontend/src/components/preview/PreviewFrame.tsx` — labelled focusable wrapper, a documented key to enter the framed content, `Escape` to return focus, and a skip link past the preview (FR-081)
- [X] T127 [US1] Implement transparent preview-token re-minting in `frontend/src/components/preview/PreviewFrame.tsx` — treat a `401` from the preview origin as re-mint-and-retry rather than an error, and pre-emptively re-mint on any frame reload after ten minutes, so tab restore, back-navigation, and network interruption never surface an authentication failure to an authorized reader
- [X] T128 [P] [US1] End-to-end test in `frontend/tests/e2e/preview-reload.spec.ts` confirming a preview reloaded after token expiry recovers silently and the reader sees no error
- [X] T049 [P] [US1] Add access scope and no-backup notices to the upload flow in `frontend/src/components/upload/ScopeNotice.tsx`
- [X] T050 [US1] Run and fix an accessibility pass over the US1 flows in `frontend/tests/a11y/upload-preview.spec.ts`

**Checkpoint**: MVP complete and independently deliverable

---

## Phase 4: User Story 2 - Comment on a specific passage (Priority: P2)

**Goal**: Reviewers highlight text or select a region, attach comments that stay pinned to that passage, reply to each other, and never silently lose a comment.

**Independent Test**: Add a comment to a text range, reload in a different session and confirm it appears at the same passage; then make the passage unresolvable and confirm the comment surfaces as orphaned with its quoted context rather than disappearing or moving.

### Tests (mandatory under Principle VI)

- [X] T051 [P] [US2] Contract test for comment endpoints against `contracts/openapi.yaml` in `backend/tests/contract/CommentEndpointsContractTests.cs`
- [X] T052 [P] [US2] Integration test for anchor resolution and orphaning, including duplicate-passage disambiguation, in `backend/tests/integration/AnchorResolutionTests.cs`
- [X] T053 [P] [US2] Accessibility test for keyboard-only passage selection and comment creation in `frontend/tests/a11y/keyboard-commenting.spec.ts`

### Implementation

- [X] T054 [US2] Implement W3C selector computation and resolution over the text projection in `backend/src/BlinkMark.Core/Anchoring/AnchorService.cs`
- [X] T055 [US2] Implement comment repository operations with TTL derived from the parent file in `backend/src/BlinkMark.Infrastructure/Cosmos/CommentRepository.cs`
- [X] T056 [US2] Implement `POST /api/files/{fileId}/comments` taking author identity from the token only in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [X] T057 [US2] Implement `GET /api/files/{fileId}/comments` as a single-partition query in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [X] T058 [US2] Implement reply threading with `threadId` and `parentId` in `backend/src/BlinkMark.Core/Comments/ThreadService.cs`
- [X] T059 [US2] Implement edit and delete of own comments with soft delete in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [X] T060 [US2] Implement orphan state computation and persistence in `backend/src/BlinkMark.Core/Anchoring/OrphanDetector.cs`
- [X] T061 [US2] Emit audit entries for comment create, edit, and delete in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [X] T062 [P] [US2] ~~Integrate `dom-anchor-text-quote` and `dom-anchor-text-position` client-side resolution~~ Implement client-side anchoring over the text projection in `frontend/src/services/anchoring.ts` — **deviation:** the planned libraries operate on a live DOM, and the preview iframe is sandboxed cross-origin so its DOM is unreachable; anchoring runs against the text projection instead, mirroring `AnchorService` (see research.md R2 correction). Both packages removed from `package.json`.
- [X] T063 [P] [US2] Build pointer-based text selection to comment — **deviation:** implemented in `frontend/src/components/comments/PassageSelector.tsx` rather than `TextSelection.tsx`
- [X] T064 [P] [US2] Build the keyboard selection path producing identical anchor data — **deviation:** implemented in `frontend/src/components/comments/PassageSelector.tsx` rather than `KeyboardSelection.tsx`. Sharing one component is what enforces "identical anchor data": all three paths call the same `createTextAnchor`/`createRegionAnchor`, so there is no second anchor format for the accessible path to drift into.
- [X] T065 [P] [US2] Build region selection with a non-dragging alternative — **deviation:** implemented in `frontend/src/components/comments/PassageSelector.tsx` rather than `RegionSelection.tsx`
- [X] T066 [P] [US2] Build the comment sidebar with in-place highlights and a separate orphaned section in `frontend/src/components/comments/CommentSidebar.tsx`
- [X] T067 [P] [US2] Render comment bodies as literal text with no markup interpretation in `frontend/src/components/comments/CommentBody.tsx`
- [X] T122 [P] [US2] Convey comment presence, anchored passage, and orphaned state to assistive technology in `frontend/src/components/comments/CommentSidebar.tsx` and ~~`frontend/src/components/comments/AnchorHighlight.tsx`~~ `frontend/src/components/comments/PassageSelector.tsx` — never by visual highlight alone (FR-079)

**Checkpoint**: Review capability complete — BlinkMark is now a review tool, not a viewer

---

## Phase 5: User Story 3 - Keep a file alive longer (Priority: P3)

**Goal**: Owners see remaining time, extend retention up to the 30-day ceiling, delete early, and download their file with all its comments.

**Independent Test**: Confirm the displayed expiry is 24 hours out, extend it and confirm enforcement, attempt to exceed 30 days from upload and confirm refusal, then download and confirm every comment including orphans is present.

### Tests (mandatory under Principle VI)

- [X] T068 [P] [US3] Integration test for the 30-day ceiling and owner-only enforcement across every interface in `backend/tests/integration/RetentionCeilingTests.cs`
- [X] T069 [P] [US3] Integration test for download bundle completeness including orphaned comments in `backend/tests/integration/DownloadBundleTests.cs`

### Implementation

- [X] T070 [US3] Implement `PATCH /api/files/{fileId}/retention` with ceiling validation at the data layer in `backend/src/BlinkMark.Api/Endpoints/RetentionEndpoints.cs`
- [X] T071 [US3] Update blob expiry and Cosmos TTL together on extension in `backend/src/BlinkMark.Core/Retention/RetentionService.cs`
- [X] T072 [US3] Add comment TTL synchronization after extension to the reconciliation job in `backend/src/BlinkMark.Jobs/Reconciliation/CommentTtlSync.cs`
- [X] T073 [US3] Implement `DELETE /api/files/{fileId}` for owners in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [X] T074 [US3] Implement the download bundle builder combining content and all comments with author, time, thread, and anchored passage in `backend/src/BlinkMark.Core/Export/DownloadBundleBuilder.cs`
- [X] T075 [US3] Implement `GET /api/files/{fileId}/download` restricted to the owner in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [X] T076 [P] [US3] Build the retention display and extension control in `frontend/src/components/retention/RetentionControl.tsx`
- [X] T077 [P] [US3] Build the download action with the copy-outlives-expiry notice in `frontend/src/components/retention/DownloadButton.tsx`
- [X] T078 [US3] Emit audit entries for retention change with previous and new expiry, and for download, in `backend/src/BlinkMark.Api/Endpoints/RetentionEndpoints.cs`

**Checkpoint**: Multi-day reviews now possible

---

## Phase 6: User Story 4 - Find out when someone comments (Priority: P4)

**Goal**: File owners and thread participants are notified of new comment activity, asynchronously and consolidated, **in BlinkMark's own notification list**.

**Channel decision (2026-07-26)**: in-app, not email or Teams. Graph `Mail.Send` (Application) is
Critical/Restricted and app-only access to it is not supported in the Microsoft tenant; a delegated
permission cannot substitute because delivery is asynchronous with no signed-in user present.
FR-034 to FR-040 name no channel, so they are unaffected. See research.md R9.
**This removes the admin-consent request, and the SDL/Privacy/RAI gate behind it, from US4
entirely.** The queue, coalescing, and recipient resolution stay channel-independent, so adding
email or Teams later is an addition rather than a redesign.

**Independent Test**: Have a second user comment and confirm the owner sees a notification naming the commenter, the file, and a working link — while confirming the commenter's action completed immediately regardless of delivery.

### Tests

- [ ] T079 [P] [US4] Integration test confirming comment creation succeeds and returns no error when the notification channel fails in `backend/tests/integration/NotificationIsolationTests.cs`
- [ ] T145 [P] [US4] Integration test for consolidation and suppression in `backend/tests/integration/NotificationCoalescingTests.cs` — several comments on one file inside one window produce a single unread notification carrying an event count rather than several rows (FR-038); an author is never notified of their own comment (FR-036); a recipient who has turned notifications off receives none (FR-040)

### Implementation

- [ ] T080 [US4] Enqueue a notification message on comment creation in `backend/src/BlinkMark.Api/Endpoints/CommentEndpoints.cs`
- [ ] T081 [US4] Implement the queue-scaled dispatcher in `backend/src/BlinkMark.Jobs/Notifications/NotificationDispatcher.cs`, writing an in-app notification record rather than sending a message
- [ ] T082 [US4] Implement coalescing by recipient, file, and time window in `backend/src/BlinkMark.Jobs/Notifications/Coalescer.cs` — a second event inside the window increments the existing unread notification through `FindByCoalesceKeyAsync` instead of adding one
- [ ] T083 [US4] Implement `GET /api/me/notifications` and `POST /api/me/notifications/{notificationId}/read` in `backend/src/BlinkMark.Api/Endpoints/NotificationEndpoints.cs`, returning the caller's unread notifications with the actor, the file, and a deep link to the comment (FR-039). Notifications inherit the parent file's TTL, so an expired file takes its notifications with it
- [ ] T084 [US4] Implement self-notification suppression and notification preference checks in `backend/src/BlinkMark.Jobs/Notifications/RecipientResolver.cs`
- [ ] T085 [P] [US4] Implement `GET`/`PATCH /api/me/preferences` in `backend/src/BlinkMark.Api/Endpoints/PreferenceEndpoints.cs`
- [X] T086 [P] [US4] Build the notification preferences screen in `frontend/src/pages/Preferences.tsx`
- [ ] T146 [P] [US4] Build the in-app notification list in `frontend/src/components/notifications/NotificationBell.tsx` and `frontend/src/pages/Notifications.tsx` — unread count in the header, each entry naming the actor and file and linking to the comment, marked read on open. Announce the count through a polite live region rather than conveying it visually alone

**Checkpoint**: Async review loop closed

---

## Phase 7: User Story 5 - Let an AI agent work on the file for me (Priority: P4)

**Goal**: An AI agent reads files and comments and creates comments strictly on behalf of a signed-in user, with visible attribution and audit distinction.

**Independent Test**: Using a delegated agent credential, list files, read content and comments, and create a comment — then verify the agent cannot reach anything the user cannot, that attribution names both, and that every action is marked agent-initiated in the audit trail.

### Tests (mandatory under Principle VI)

- [ ] T087 [P] [US5] Contract test verifying the served MCP manifest matches `contracts/mcp-tools.json` in `backend/tests/contract/McpManifestContractTests.cs`
- [ ] T088 [P] [US5] Integration test confirming an agent cannot exceed the represented user's permissions, cannot act for a signed-out user, and that its actions are distinguishable from direct user actions in the audit trail, in `backend/tests/integration/AgentAuthorizationTests.cs`

### Implementation

- [ ] T089 [US5] Implement On-Behalf-Of token exchange and acting-agent identity extraction in `backend/src/BlinkMark.Api/Auth/OnBehalfOfHandler.cs`
- [ ] T090 [US5] Implement the MCP server endpoint over Streamable HTTP in `backend/src/BlinkMark.Api/Mcp/McpServer.cs`
- [ ] T091 [US5] Implement the `list_my_files` and `read_file_content` tools in `backend/src/BlinkMark.Api/Mcp/Tools/FileTools.cs`
- [ ] T092 [US5] Implement the `list_comments` and `create_comment` tools in `backend/src/BlinkMark.Api/Mcp/Tools/CommentTools.cs`
- [ ] T093 [US5] Implement the `extend_retention` tool in `backend/src/BlinkMark.Api/Mcp/Tools/RetentionTools.cs`
- [ ] T094 [US5] Implement `GET /api/files/{fileId}/content` returning the structured projection in `backend/src/BlinkMark.Api/Endpoints/FileEndpoints.cs`
- [ ] T095 [US5] Persist `actingAgentId` on comments and surface it in responses in `backend/src/BlinkMark.Infrastructure/Cosmos/CommentRepository.cs`
- [ ] T096 [US5] Implement agent rate limiting and ensure agent actions count against the represented user's quota in `backend/src/BlinkMark.Core/Quotas/AgentRateLimiter.cs`
- [ ] T124 [US5] Populate `actingAgentId` on every audit entry written during an agent-initiated request, in `backend/src/BlinkMark.Api/Middleware/AuditMiddleware.cs`, so agent actions are distinguishable from direct user actions (FR-049, SC-015)
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

- [X] T100 [US6] Implement the Redis presence store with per-viewer TTL keys and pub/sub fan-out in `backend/src/BlinkMark.Infrastructure/Redis/PresenceStore.cs`
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
- [ ] T125 [P] Stand up continuous synthetic probing for unauthenticated and out-of-tenant access, and schedule the hostile-corpus suite to run on every deployment, in `.github/workflows/continuous-verification.yml` (SC-007, SC-008)
- [ ] T126 [P] Add an availability probe and monthly availability reporting against the 99.5% target in `infra/modules/observability.bicep` (SC-023)
- [ ] T113 Apply a resource lock to the audit storage account and add a test asserting no delete or merge operation against the audit table exists in `backend/tests/integration/AuditImmutabilityTests.cs`
- [X] T114 [P] Write `README.md` linking the constitution, spec, and quickstart
- [ ] T115 Validate deployment with `azd up` against subscription `46a174f6-0602-4df8-9fb0-f8e8248bcb8f` / `rg-blinkmark` / `eastus`, and run the four verification checks from [quickstart.md](quickstart.md)
- [ ] T135 Verify SFI posture on the deployed environment: confirm `allowSharedKeyAccess`, Cosmos `disableLocalAuth`, Redis `disableAccessKeyAuthentication`, Log Analytics local auth, Key Vault RBAC mode, and `publicNetworkAccess` on every data service, and record the result in `docs/sfi-attestation.md`
- [ ] T116 Submit a PATCH amendment to `.specify/memory/constitution.md` raising it to v1.1.1, correcting the Static Web Apps preview-origin justification identified in [plan.md](plan.md) (superseded in part by the v1.2.0 amendment; the SWA justification still needs correcting)
- [ ] T117 [P] Verify the application degrades quietly with Redis stopped, confirming preview and commenting are unaffected

---

## Phase 10: SFI identity compliance

**Added after checking the standards on eng.ms.** Token validation, application ownership, and
admin consent are governed centrally at Microsoft, and the first of these is a live SFI KPI
rather than a recommendation.

- [X] T136 Harden inbound token validation in `backend/src/BlinkMark.Api/Auth/AuthenticationSetup.cs` — pin signing algorithms, require signed tokens and an audience, restrict `typ`, refuse app-only tokens (FR-055), enforce an authorized-party allow-list, stop persisting the bearer value, and log rejection reasons without the token. Covered by new tests in `backend/tests/integration/AuthorizationDenialTests.cs`. **Interim only — superseded by T137**
- [ ] T137 **Migrate inbound token validation to MISE v2 before the app registration goes live.** SFI KPI ID-2.1.x, minimum version ≥ 1.31.0, and MSec Product Security requires the **latest MISE v2 GA release**. The SFI Wave deadline was **April 2026 and has already passed**, so this is not deferrable work: the KPI is driven by eSTS telemetry per application id, which means an S360 action item opens against BlinkMark as soon as the registration starts validating tokens. Steps: add `Microsoft.Identity.ServiceEssentials.AspNetCore` 2.0.1; add `miseconfig.json` with `MiseVersion: "2.0"` and `Protocols.Bearer.TokenTypes.AccessToken` set to `AppToken: false` / `UserToken: true` (FR-055 expressed as configuration); replace `AddJwtBearer` with `AddAuthentication(MiseAuthenticationDefaults.AuthenticationScheme).AddMiseWithDefaultModules(...)`; order middleware `UseAuthentication` → `UseAuthorization` → `UseMise`; set `CopyToOutputDirectory` on the config file. Confirm every authorization policy keeps `RequireAuthenticatedUser()` — under MISE the authentication handler does not block requests by itself, so a policy that can succeed without an authenticated user bypasses authentication entirely. Allow 2–3 days after deployment for the KPI to close. **Blocked**: the package is on an internal feed and this repository has only nuget.org configured
  - Effort for a service this size is rated **Low** — bump the package, rename config keys, reorder middleware
  - [Adoption guide](https://eng.ms/docs/microsoft-security/identity/entra-developer-application-platform/id4s-identity-for-services/authn-middleware-sdk-microsoft-identity-service-essentials/microsoft-identity-service-essentials/articles/v2/adoption-guide/adopting-mise-v2-aspnetcore) · [campaign](https://eng.ms/docs/microsoft-security/dscgp/msec-trust/msec-deputy-ciso/msec-security-strategy-and-risk-operations/msec-security-strategy-and-risk-operations/campaigns/misev2/misev2) · [exception policy](https://eng.ms/docs/microsoft-security/identity/entra-developer-application-platform/id4s-identity-for-services/authn-middleware-sdk-microsoft-identity-service-essentials/microsoft-identity-service-essentials/articles/tsgs/mise-kpi-exception-policy) · [dashboard](https://aka.ms/mise/dashboard) · [help channel](https://aka.ms/mise/s360-help)
  - If it cannot be done before launch, file an exception rather than letting the item age: tag the ADO item `MSec-Campaigns-MISEv2-Exception` and `ETA.YYYY-MM-DD`
- [ ] T141 Adopt the MISE v2 **token revocation** and **Continuous Access Evaluation** modules once T137 lands. Without them a compromised token cannot be revoked in real time — a threat actor keeps access until natural expiry, and Conditional Access is enforced only at issuance. The campaign is explicit that services without MISE v2 are **not eviction-ready**. This matters more than usual for BlinkMark: the product's premise is that content is short-lived and tenant-only, and a token that cannot be killed undercuts both claims
- [X] T138 Require a Service Tree ID: mandatory GUID-validated `serviceTreeId` parameter in `infra/main.bicep` stamped as a tag on every resource, written to both app registrations' `tags` and `notes` by `infra/entra/setup-app-registrations.ps1`, surfaced to the API as `BlinkMark__Entra__ServiceTreeId`, passed by `.github/workflows/deploy.yml`, and enforced by `tools/sfi-gate.ps1`
- [X] T139 ~~File the admin consent request for Microsoft Graph `Mail.Send`~~ — **no longer required.** Resolved by T144: notifications are delivered in-app, so BlinkMark requests only delegated `User.Read`, which every user consents to for themselves. No SPACE request, no App Admin Consent, no Application Access Policy, and no SDL/Privacy/RAI gate on the notification path. Recorded here rather than deleted because the reasoning matters if email or Teams is ever added — at which point use mailbox-scoped Resource Specific Consent, and for a time-bound event note SPACE's [Temporary Consent](https://spacetool.microsoft.com/create-temporary-consent-request) path (90 days, auto-approved only for low-risk or event-approved permissions; support `dsracreviews@microsoft.com`)
- [X] T144 **Resolved: drop email, notify in-app.** Microsoft's Entra mail guidance rates Graph `Mail.Send` (Application) as Critical/Restricted and states the Microsoft tenant "does not currently support app-only access to Mail.Send due to the extreme risk", so the mechanism [research.md R9](research.md) assumed was never available. FR-034–FR-040 say *notify* rather than naming a channel, so in-app delivery satisfies them unchanged. Applied: R9 rewritten; spec.md US4 narrative, assumption, Notification entity and SC-012 updated; a clarification recorded; plan.md risk table swapped for the residual in-app risk; `Mail.Send` removed from `infra/entra/setup-app-registrations.ps1`; `SenderAddress` removed from configuration; `NotificationState` reshaped from `Pending/Sent/Failed` to `Unread/Read/Suppressed` with `ReadAt` and an `EventCount`; `INotificationRepository` reshaped for unread reads and coalesce-key lookup. **Accepted cost**: someone who is not in BlinkMark does not learn of a comment until they next open it, and SC-012's 5-minute target now measures visibility in-product rather than reaching someone's attention
- [ ] T142 **Decide the registration tenant deliberately, before T139.** The [app registration decision guide](https://eng.ms/docs/microsoft-security/identity/app-plat-and-graph/app-vertical/aad-first-party-apps/identity-platform-and-access-management/microsoft-identity-platform/apps-repo/first-party-app-decision-guide) says the Corp tenant is **not recommended for non-production applications**, and that dev/test work belongs in **TME** or **TestTorus** (looser restrictions, no SAW, ordinary `@microsoft.com` sign-in). For a hackathon there is also a **Temporary Consent** path in Corp (see T139), so the choice is genuinely open:
  - **Corp (`72f988bf-…`)** — real employee sign-in and real mailbox delivery, which is what the spec assumes. For a hackathon, **Temporary Consent** (90 days, T139) avoids the full review chain up front. Beyond 90 days it costs Service Tree inventory, standard AAC, and the SDL, Privacy and RAI reviews behind it.
  - **TME (`70a036f6-8e4d-4615-bad6-149c02e7720d`) / TestTorus (`b1a4f7cb-…`)** — removes SPACE/AAC and its review chain, so T139 leaves the critical path.
  - **What TME does NOT remove — verified, and the opposite of the intuitive answer:**
    - **TME is in SFI scope.** S360 compliance there is deliberately designed to mirror AME/PME, and "all S360 SFI and other KPIs will remain the same". **T137 (MISE v2) therefore still applies** — choosing TME buys nothing on that front. Ditto the credential-free posture the SFI gate enforces.
    - **No customer data in TME**, ever. BlinkMark stores whatever a user uploads, so a demo has to use synthetic drafts and the Data Classification and Handling Standard applies.
    - **TME is not for automated testing with user tokens.** Accounts are Corp-guested and carry the same MFA requirements, so the Playwright suites that need a signed-in user need an *Ephemeral* or *Test Automation Auxiliary* tenant instead.
  - **Service availability**: the documented TME gaps are dSTS/dSMS, SharePoint, Copilot Studio, AI Foundry marketplace models, and SLNM. Nothing in BlinkMark's stack — Container Apps, Cosmos, Redis, Storage, Key Vault — is on that list, but confirm before committing.
  - If the goal is a hackathon demo: TME, synthetic content, and **cut or stub US4** rather than starting an AAC request that cannot finish in time. If the goal is a service colleagues actually use, it is Corp and the reviews start now, because they are calendar time rather than developer time.
  - `infra/entra/setup-app-registrations.ps1` detects which tenant it is pointed at and prints the consequences before doing anything
- [ ] T143 **Get TME access** (only if T142 chooses TME). No SAW required — TME is reachable from an ordinary dev workstation, and Visual Studio / VS Code sign in with the TME account directly.
  1. Go to [aka.ms/umsportal](https://aka.ms/umsportal) → **Identity Hub** → *Eligibilities, Clearance and Approval Team*
  2. **Request Eligibility** → search for **`Corp User Access in TME01`** → submit. Accounts in TME are guested from Corp; "eligibility" is the sync
  3. Clear the multi-stage approval — approvers act at [ums.microsoft.com/v2/UMS/approvals](https://ums.microsoft.com/v2/UMS/approvals). Following the link from the notification e-mail while signed out loses the destination, so go to the approvals page directly
  4. Once approved (plus one automated step) the account syncs, and the portal is [portal.azure.com/70a036f6-8e4d-4615-bad6-149c02e7720d](https://portal.azure.com/70a036f6-8e4d-4615-bad6-149c02e7720d)
  - Service principals in TME come from the [cross-tenant SPN request](https://aka.ms/crosstenantspnrequest); apps cannot be moved from Corp/AME/PME and must be recreated. Corp → TME access is permitted
  - For `New-AzureServiceRollout`, pass `-GuestAccountTenantId '70a036f6-8e4d-4615-bad6-149c02e7720d'`
  - Support: StackOverflow tag [`[tme]`](https://stackoverflow.microsoft.com/search?q=%5Btme%5D) (preferred) or the TME Support Teams channel — best-effort, not fully monitored
- [X] T140 Extend `tools/sfi-gate.ps1` with ownership and token-validation rules — Service Tree ID present and tagged, signing algorithms not relaxed outside the test host, no `Validate*` check disabled, and metadata retrieved over HTTPS. Verified against a deliberately violating tree rather than assumed

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

- **T118 (preview token) blocks T026 and T039** — the preview host has no other credential, so it cannot be built before the token exists
- T038 (render + projection) blocks T054 (anchor computation) — anchors need the projection to exist
- T043 (quota) must precede T035 completion — upload enforces the quota
- T054 (anchor service) blocks T060 (orphan detection) and T062 (client resolution)
- T100 (presence store) blocks T101–T105
- T089 (OBO handler) blocks all MCP tools T090–T093, and blocks T124 (agent-attributed audit)

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

**T118 (preview token) comes before any preview code.** The preview origin has no session and no
other credential; building T026 or T039 first produces an unauthenticated content endpoint, which
violates Principle I on the most sensitive path in the product.

**Verify T033 deletes physically, not just logically.** A file hidden from the API but still in
blob storage fails SC-006 and the entire compliance premise.

---

## Summary

| Metric | Value |
|---|---|
| Total tasks | 146 |
| Setup | 7 |
| Foundational | 31 |
| US1 (P1, MVP) | 26 |
| US2 (P2) | 18 |
| US3 (P3) | 11 |
| US4 (P4) | 10 |
| US5 (P4) | 12 |
| US6 (P4) | 10 |
| Polish | 13 |
| SFI identity compliance | 9 |
| Marked `[P]` | 60 |
| Mandatory test tasks | 13 |

**Tasks T118–T128 were added after the consistency analysis, T129–T135 after the SFI review, and
T136–T146 after checking the identity standards on eng.ms and the Global Hackathon 2026 resources
site.** They are numbered above the original sequence so existing IDs stay stable, and sit within
the phase they belong to rather than at the end.

## Completion state

**101 of 146 complete.** Phases 1–5 are done; the backend of each is verified.

| Phase | State |
|---|---|
| 1 — Setup | Complete |
| 2 — Foundational | Complete |
| 3 — US1 (MVP) | Complete — verified |
| 4 — US2 comments | Complete — backend verified; frontend written but never compiled |
| 5 — US3 retention | Complete — backend verified; frontend written but never compiled |
| 6 — US4 notifications | T086 only; channel now decided (in-app), so the rest is unblocked |
| 7 — US5 agents | Not started |
| 8 — US6 presence | T100 only (the Redis store, built with the adapters) |
| 9 — Polish | T114 only |
| 10 — SFI identity | T136, T138, T139, T140, T144 complete; T137/T141 blocked on feed access; T142/T143 are decisions |

**Verified**: `dotnet build` clean, `dotnet test` 128 passing (18 unit, 83 integration, 27
contract), `az bicep build` exit 0, `tools/sfi-gate.ps1` passing and proven non-vacuous against a
deliberately violating tree.

**Not verified**: everything under `frontend/`. `npm install` has not been run, so no frontend
code has been compiled, linted, or executed. This now includes the whole US2 commenting UI and
the US3 retention and download controls, which together are the largest unverified body of code
in the repository.

**One manual step outstanding**: `dom-anchor-text-quote` and `dom-anchor-text-position` are still
listed in `frontend/package.json` but are no longer used or wanted (see T062). They should be
removed; the edit was blocked here as a dependency change.

### A correction made during Phase 5

Owner-scoped routes (`PATCH /retention`, `DELETE`, `GET /download`) answer **403, not 404**, for
an expired file. The ownership check runs in the authorization handler, which treats "expired",
"never existed", and "not yours" identically — so all three deny the same way and none of them
can be told apart. That is the stronger privacy property, so the tests assert
indistinguishability rather than a particular status code. The cost is that an owner returning to
their own expired file sees "Forbidden" rather than "not found", which is worth revisiting in
Phase 9 as a message problem rather than an authorization one.

**What is left to settle:**

- **T142 — registration tenant.** Corp gives real employee sign-in and, for a hackathon, a 90-day
  Temporary Consent path — though with in-app notifications BlinkMark needs no admin consent at
  all, which makes Corp considerably cheaper than it was. TME/TestTorus has no real employee
  accounts and forbids customer data.
- **T137 — MISE v2.** The April 2026 SFI Wave deadline has passed, and this applies **whichever
  tenant is chosen** — TME is explicitly in SFI scope and carries the same S360 KPIs. Blocked
  only by the internal NuGet feed not being configured here.

The admin-consent blocker is gone: dropping email removed the single permission that needed it.
