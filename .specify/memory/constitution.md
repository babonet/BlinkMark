<!--
SYNC IMPACT REPORT
==================
Version change: 1.0.0 → 1.1.0
Bump rationale: MINOR — no principle removed or redefined. The Platform and Technology
Constraints section is materially expanded: the three deferred stack choices are now
resolved and binding, and the cost-driven service selections behind them are recorded.

Modified principles: none (Principles I–VI unchanged in wording and obligation)

Modified sections:
  - Platform and Technology Constraints — frontend, backend, and metadata store
    decided; compute, purge, audit, notification, secrets, and observability hosting
    choices added with cost rationale.

Added sections: none
Removed sections: none

Templates requiring updates:
  - ✅ .specify/templates/plan-template.md — no edit needed; Technical Context now has
    concrete values to copy from this section.
  - ✅ .specify/templates/spec-template.md — no edit needed.
  - ✅ .specify/templates/tasks-template.md — no edit needed.
  - ✅ .specify/templates/checklist-template.md — no edit needed.
  - ⚠ README.md / docs/quickstart.md — do not exist yet; link this constitution when
    developer-facing docs are added.

Deferred items:
  - RESOLVED TODO(FRONTEND_FRAMEWORK) → React.
  - RESOLVED TODO(BACKEND_RUNTIME) → .NET Core.
  - RESOLVED TODO(DATABASE_ENGINE) → Azure Cosmos DB (serverless).
  - None outstanding.

Prior history:
  1.0.0 (2026-07-26) — initial ratification from template; six principles established.
-->

# BlinkMark Constitution

BlinkMark is an internal, tenant-scoped service for sharing short-lived HTML and
Markdown files and collecting anchored review comments on them.

## Core Principles

### I. Tenant-Only Secure Access (NON-NEGOTIABLE)

Every request that reads, writes, previews, comments on, or extends the life of a
file MUST be authenticated through Microsoft Entra ID (Azure AD) against the owning
tenant, and MUST be authorized server-side before any content is returned.

- Anonymous access MUST NOT exist for any file, comment, or metadata endpoint. There
  are no unauthenticated "share links".
- Authorization MUST be enforced in the backend on every request. Client-side hiding
  of UI affordances is never an authorization control.
- Blob content MUST NOT be served by a long-lived public URL. Access is via the API
  or a short-lived, user-scoped, read-only SAS (15 minutes maximum).
- Files and comments MUST be encrypted at rest and in transit (TLS 1.2+).
- Secrets, connection strings, and keys MUST come from Azure Key Vault or managed
  identity. No secrets in source, config files, or environment defaults.

Rationale: BlinkMark holds unpublished internal drafts. A single unauthenticated path
leaks the entire value of the product.

### II. Ephemeral by Default

Files are temporary artifacts, not a document store.

- Default retention is 24 hours from upload. This default MUST NOT be raised at the
  system level as a way to bypass the policy.
- A user MAY extend retention on a file they own, up to a hard maximum of 30 days
  from original upload. The 30-day ceiling is absolute; no code path may exceed it.
- Deletion MUST remove the blob, the file metadata, and its comments. Expiry MUST be
  enforced by a server-side scheduled process, not by client behavior and not by
  merely hiding expired files from listings.
- Expired-but-not-yet-purged content MUST be treated as deleted by all read paths.
- Every retention extension MUST be recorded in the audit log with actor, file, old
  expiry, and new expiry.

Rationale: The retention promise is the compliance story. If expiry is best-effort,
the product cannot be used for the drafts it exists to serve.

### III. Anchored Comments Must Survive

A comment's value is its anchor. Comments MUST bind to a durable, content-derived
anchor (text range or region selector) persisted alongside the comment, not to a
volatile DOM path or pixel coordinate alone.

- Anchor resolution MUST degrade gracefully: an anchor that cannot be located MUST
  surface the comment as "orphaned" with its original quoted context. It MUST NOT
  disappear silently and MUST NOT attach to a different location.
- Comment create and list operations MUST hold a p95 under 300 ms server-side so
  review feels conversational.
- Author identity MUST be taken from the authenticated principal and MUST NOT be
  supplied by the client.

Rationale: Silently moved or dropped comments destroy trust in the review loop far
faster than a visible "we lost this anchor" state.

### IV. Untrusted Content Is Never Trusted

Uploaded HTML and Markdown are hostile input.

- HTML and rendered Markdown MUST be rendered in a sandboxed context (sandboxed
  iframe with an explicit Content-Security-Policy) and/or sanitized server-side.
  Script execution from uploaded content MUST be blocked.
- Preview content MUST be served from an origin that cannot access the application's
  session, tokens, or cookies.
- Uploads MUST be validated for declared type, extension, and size limit before
  storage, and stored under a system-generated name — never the user-supplied path.
- User-supplied comment text MUST be escaped on render; it is never HTML.

Rationale: Stored XSS in a preview pane inside an authenticated tenant app is a
tenant-wide credential-theft vector.

### V. Auditable and Observable

Every state-changing action MUST produce a structured audit record containing actor
object ID, action, target file or comment ID, UTC ISO 8601 timestamp, and outcome.

- Auditable actions include at minimum: upload, view, preview, download, comment
  create/edit/delete, retention extension, and deletion (user-initiated or automatic).
- Audit records MUST be append-only and retained independently of the file they
  describe — deleting a file MUST NOT delete its audit trail.
- Logs and telemetry MUST NOT contain file contents, comment bodies, tokens, or
  personal data beyond the actor identifier required for audit.
- Failures MUST be traceable end-to-end via a correlation ID propagated from the
  frontend request through the API to storage and database operations.

Rationale: Compliance and incident response both require answering "who saw what,
when" after the underlying file is gone.

### VI. Contract-First, Test-Backed Delivery

API contracts are defined before implementation, and behavior this constitution
declares non-negotiable MUST be covered by automated tests.

- Each feature MUST publish its API contract (OpenAPI or equivalent) before the
  implementing code merges.
- Automated tests are REQUIRED for: authorization denial paths, retention expiry and
  the 30-day ceiling, anchor resolution and orphaning, and content sanitization. Other
  testing is at the team's discretion.
- Ambiguous technology choices MUST be resolved and recorded in the plan's Technical
  Context before implementation tasks are generated — no "React or Angular" may reach
  a task list.
- Breaking API changes require a version bump and a documented migration path.

Rationale: These four areas are exactly where a regression is invisible in manual
testing and catastrophic in production.

## Platform and Technology Constraints

BlinkMark is an Azure-native, Microsoft-tenant-internal web application. The stack
below is decided and binding. It is optimized for low steady-state cost on an
ephemeral, internal-only workload; substitutions require an amendment, not a plan-time
choice.

- **Identity**: Microsoft Entra ID (Azure AD) is the sole identity provider, consumed
  via MSAL. No local accounts, no external federation, no premium-tier dependency.
- **Frontend**: **React** (TypeScript, Vite) hosted on **Azure Static Web Apps
  (Standard)**. The UI MUST be responsive across tablet and desktop viewports. The
  Standard tier is required because Principle IV's isolated preview origin is served
  as a second custom domain on the same instance.
- **Backend**: **.NET Core (ASP.NET Core Minimal API)** hosted on **Azure Container
  Apps (Consumption)** with `min-replicas` of at least 1. Scaling MUST be driven by
  concurrent-request rules, never by manual instance counts.
- **File storage**: **Azure Blob Storage (Hot tier) with hierarchical namespace
  enabled**. Expiry MUST be set on the blob at write time via the Set Blob Expiry API
  so the platform performs deletion. Encryption at rest MUST be enabled.
- **Metadata and comments**: **Azure Cosmos DB (serverless)**. Document TTL MUST be
  the primary retention mechanism for file metadata and comments, kept consistent with
  the blob expiry timestamp. Provisioned or autoscale throughput MUST NOT be adopted
  until sustained demand exceeds serverless limits, and only via amendment.
- **Audit store**: **Azure Table Storage**, in a container with no TTL and no delete
  path from application code. Audit records MUST NOT be written to Cosmos DB or to
  Log Analytics as the system of record.
- **Retention reconciliation**: an **Azure Container Apps Job** on a cron schedule acts
  as the server-side enforcement backstop required by Principle II — reconciling blob,
  metadata, and comment state and purging anything the platform TTLs missed.
- **Notifications**: new-comment and activity notifications are delivered via the
  **Microsoft Graph API** (email and Teams). Delivery MUST be queued through Azure
  Storage Queues so it is asynchronous and cannot block or fail the originating user
  action. Per-message third-party or metered email services MUST NOT be introduced.
- **Secrets and access**: **managed identity** for all Azure-to-Azure calls; **Azure
  Key Vault (Standard)** for the residual secrets. Connection strings and keys MUST NOT
  be used where a managed identity is available.
- **Observability**: **Application Insights / Log Analytics** with an explicit daily
  ingestion cap configured. Telemetry MAY be sampled; audit records (Principle V) MUST
  NOT be sampled, which is why they live in Table Storage.
- **Performance targets**: p95 upload acknowledgement under 2 s for files up to the
  configured size limit; p95 file retrieval and preview start under 1 s; p95 comment
  create and list under 300 ms.
- **Scale target**: 500 concurrent authenticated users while holding the above
  targets. Services MUST be stateless and horizontally scalable; no in-process session
  or file caches that break under multi-instance deployment.

Any deviation from these constraints requires an entry in the plan's Complexity
Tracking table naming the rejected simpler alternative.

## Quality Gates and Development Workflow

- **Constitution Check**: Every `/speckit.plan` MUST evaluate the feature against
  Principles I–VI before Phase 0 research and again after Phase 1 design. Violations
  are either removed or justified in Complexity Tracking.
- **Pull requests**: Every PR MUST state which principles it touches. A PR that adds
  an endpoint MUST show its authorization check and its audit record. A PR that
  renders or stores user content MUST show its sanitization boundary.
- **CI**: Build, lint, and the required tests from Principle VI MUST pass before
  merge. Failing or skipped security and retention tests MUST NOT be merged.
- **Scanning**: Secret scanning and dependency vulnerability scanning MUST run on
  every PR. High-severity findings block merge.
- **Definition of done** for a user-facing feature: contract published, authorization
  enforced and tested, audit events emitted, retention behavior correct, and the UI
  path usable without documentation.

## Governance

This constitution supersedes all other development practices, conventions, and
preferences for the BlinkMark repository. Where a style guide, template, or habit
conflicts with a principle here, the principle wins.

- **Amendments** MUST be proposed as a pull request that edits this file, states the
  motivation, and includes the resulting version bump and Sync Impact Report.
  Amendments touching Principles I, II, or IV additionally require explicit sign-off
  from the project owner.
- **Versioning policy** follows semantic versioning:
  - **MAJOR** — a principle is removed, or redefined such that previously compliant
    code becomes non-compliant.
  - **MINOR** — a principle or governing section is added, or existing guidance is
    materially expanded.
  - **PATCH** — clarifications, wording, and typo fixes with no change in obligation.
- **Compliance review** occurs at every plan gate and every code review. Any merged
  exception MUST be recorded in the owning feature's Complexity Tracking table and
  revisited at the next amendment.
- **Runtime development guidance** lives in the repository's agent guidance files
  under `.github/` and in `.specify/templates/`. Those files MUST NOT contradict this
  constitution; when they drift, they are corrected, not this file.

**Version**: 1.1.0 | **Ratified**: 2026-07-26 | **Last Amended**: 2026-07-26
