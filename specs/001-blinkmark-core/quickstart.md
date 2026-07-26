# Quickstart: BlinkMark Core

**Feature**: `001-blinkmark-core` | **Date**: 2026-07-26

Getting a developer from a clean clone to a running BlinkMark, and the small number of things
that will bite if you skip them.

---

## Prerequisites

| Tool | Version | Why |
|---|---|---|
| .NET SDK | 9.0+ | API, preview host, jobs |
| Node.js | 22 LTS | React frontend |
| Azure CLI | latest | Only for deploying |
| Azure Developer CLI (`azd`) | latest | Only for deploying |

**Nothing above is needed to run BlinkMark locally except .NET and Node.** No Docker, no Azure
subscription, no Entra app registration, and no network connection.

---

## Local setup

```powershell
git clone <repo> ; cd BlinkMark
dotnet build
npm --prefix frontend ci
```

Then two terminals:

```powershell
dotnet run --project backend/tools/BlinkMark.LocalHost   # API + preview origin
npm --prefix frontend run dev                            # http://localhost:5173
```

Open <http://localhost:5173> and you are signed in. That is everything.

### What the local host actually is

`BlinkMark.LocalHost` is a project that is **never deployed**. It calls `ApiHost.ConfigureServices`
and `ApiHost.ConfigurePipeline` — the same methods `BlinkMark.Api/Program.cs` calls — and then
replaces the Azure adapters with the in-memory ones the backend test suite already runs against.
Every endpoint, every authorization policy, the sanitizer, the renderer, the anchoring algorithm,
the retention rules and the middleware order are the real ones.

Two things are not real, and it is worth knowing exactly which:

- **Storage is in memory.** Everything is lost when the process exits. For a product whose whole
  premise is that content does not stick around, this is arguably the most faithful part.
- **Tokens are signed with a local symmetric key** by `GET /dev/token`, instead of by Entra.

**Authentication itself is not faked.** The local host mints genuine JWTs and the production
authentication handler validates them in full — signature, issuer, audience, lifetime, tenant,
scope, and the app-only refusal in FR-055. A stub handler would have been less code and would
have switched off precisely the behaviour you most want to try by hand.

```powershell
# Be someone else — this is how to watch ownership actually being enforced
curl "http://localhost:5080/dev/token?user=bob"

# An agent acting on a user's behalf, for checking attribution in the audit trail
curl "http://localhost:5080/dev/token?user=alice&agent=blinkmark-local-agent"
```

To use the frontend as a different person, change `VITE_LOCAL_DEV_USER` in
`frontend/.env.development` and reload.

### The preview really is a separate origin

The API serves on `http://localhost:5080` and the preview on `http://127.0.0.1:5081`. Those are
different origins to a browser even though they are the same machine, so Principle IV is enforced
locally by the same-origin policy rather than by convention — no hosts file, no external DNS, and
a sanitizer bypass you find locally lands somewhere with no access to the application's tokens.

### Deployed-only prerequisites

An Azure subscription and permission to register an application in the tenant, needed only to
deploy. **No admin consent is required**: BlinkMark requests only delegated `User.Read`, which
each user consents to for themselves. Notifications are delivered in-app rather than by email
precisely so that no admin-consented permission is needed (research.md R9).

Two registrations, not one:

1. **BlinkMark API** — exposes scopes `Files.ReadWrite` and `Comments.ReadWrite`, and accepts
   On-Behalf-Of exchanges from agent clients.
2. **BlinkMark SPA** — public client, authorization code with PKCE, redirect to
   `http://localhost:5173`.

Set `Sign in audience` to **single tenant** on both. This is the cheapest possible enforcement of
FR-002 — a multi-tenant registration would accept tokens from other organizations and put the
entire burden on application code.

Neither registration may hold a client secret or certificate. The API's ability to perform the
On-Behalf-Of exchange comes from a federated identity credential backed by the managed identity.

---

## Verify the walking skeleton

```powershell
pwsh tools/local-smoke.ps1
```

Eighteen checks against the running host, covering the four below and the rest of the
authorization, retention, commenting, preview-isolation and sanitization rules.

### The four checks that matter

Run these before believing anything works:

1. **Unauthenticated request returns nothing.** `curl` any endpoint with no token. You should get
   401 with no filename, no metadata, no hint the file exists (FR-001).
2. **Script does not execute.** Upload `backend/tests/fixtures/hostile/inline-script.html` and open
   the preview. If an alert fires, stop and fix it before writing another line (FR-013, SC-008).
3. **Expiry actually deletes.** Set a file's expiry to two minutes out, wait, then confirm the
   blob is gone from storage — not merely hidden from the API (FR-031, SC-006).
4. **The ceiling holds.** `PATCH` retention to 31 days out. It must be refused, and the existing
   expiry must be unchanged (FR-028).

---

## Deploy

**Target**: subscription `46a174f6-0602-4df8-9fb0-f8e8248bcb8f` ("Commerce AI Assistant"),
resource group `rg-blinkmark`, region `eastus`, tenant `72f988bf-86f1-41af-91ab-2d7cd011db47`.

```powershell
azd auth login
azd env set AZURE_SUBSCRIPTION_ID 46a174f6-0602-4df8-9fb0-f8e8248bcb8f
azd env set AZURE_RESOURCE_GROUP rg-blinkmark
azd env set AZURE_LOCATION eastus
azd up
```

Provisions two storage accounts, Cosmos serverless, Redis Basic C0, Container Apps in a VNet,
Static Web Apps, Key Vault, App Insights, a user-assigned managed identity, and private endpoints
for every data service.

### Deployment gotchas

- **Two storage accounts is not a mistake.** Accounts with hierarchical namespace enabled do not
  support the Table and Queue services, and hierarchical namespace is required for the Set Blob
  Expiry API. Files live in the HNS account; audit and queues live in the standard account.
- **Set the App Insights daily cap during provisioning.** Uncapped ingestion will be your largest
  bill line, larger than all compute combined.
- **Notifications are in-app, and that is deliberate.** Do not reach for Graph `Mail.Send`: it is
  rated Critical/Restricted and app-only access to it is not supported in the Microsoft tenant.
  Adding email later means mailbox-scoped Resource Specific Consent, not a tenant-wide permission.
- **Container Apps `min-replicas` stays at 1** on the API. Scale-to-zero adds cold start to the
  1-second preview budget (SC-002).
- **Redis Basic C0 has no SLA and restarts without warning.** That is expected and acceptable: it
  holds only presence, rate-limit counters, and a quota cache. Confirm the app degrades quietly
  when you stop it (FR-069).

---

## SFI: there are no secrets, and that is load-bearing

Constitution Principle VII forbids service credentials outright. Local authentication is disabled
**at the resource**, not merely unused, so the usual fallbacks do not exist and are not meant to.

| Service | What is disabled | How the app authenticates |
|---|---|---|
| Storage (both accounts) | `allowSharedKeyAccess: false` | Managed identity + data-plane RBAC |
| Cosmos DB | `disableLocalAuth: true` | Managed identity + Built-in Data Contributor |
| Redis | `disableAccessKeyAuthentication: true` | Managed identity + Entra auth |
| Key Vault | Access policies (RBAC only) | Managed identity + Crypto User |
| App Insights / Log Analytics | `DisableLocalAuth: true` | Managed identity |
| Entra app registrations | Client secrets and certificates | Federated identity credential backed by the managed identity |
| CI/CD | Service principal secrets, SWA deployment tokens | Workload identity federation |

**If you find yourself looking for a connection string, stop.** There isn't one, and adding one
fails the CI SFI gate. Use `DefaultAzureCredential` everywhere.

**On-Behalf-Of works without a client secret.** OBO normally needs one; here the API presents a
token for its own managed identity as a client assertion via a federated identity credential on
the app registration. An `AADSTS7000215` invalid-client-secret error means the federated credential
is misconfigured — do not "temporarily" add a secret to unblock yourself.

**The preview-token signing key never leaves Key Vault.** It is a Key Vault *key*, not a secret,
and signing happens through the Key Vault sign operation. There is no key material to retrieve.

**Local development uses emulators, not cloud resources.** Private endpoints mean your workstation
cannot reach the deployed data services, by design. `docker compose up` gives you Redis, Azurite,
and the Cosmos emulator. No developer needs data-plane access to a deployed environment.

---

## Things that will surprise you

**Anchors are computed against the sanitized render, not your upload.** If you change sanitizer
configuration, previously stored anchors may no longer resolve. That is why `renderVersion` is
pinned per file. Do not "just upgrade the sanitizer" without understanding this.

**There is no admin.** No role can read the audit trail, raise a quota, or restore a file. If
you find yourself designing a screen for an administrator, re-read clarification Q1 — that role
does not exist in this phase.

**Nothing is backed up.** A Cosmos or Blob mishap in development is unrecoverable, exactly as in
production. Do not build local habits that assume a restore exists.

**Presence must never become a feature dependency.** If preview or commenting stops working when
Redis is down, that is a bug against FR-069, not an infrastructure problem.

---

## Test layout

```
backend/tests/
  contract/      # OpenAPI + MCP manifest conformance
  integration/   # authz denial, retention ceiling, anchor orphaning, sanitization  ← Principle VI mandatory four
  unit/
frontend/tests/
  a11y/          # keyboard-only commenting, presence live-region announcements
  unit/
```

The four integration areas are non-negotiable under Principle VI. A pull request that skips or
disables one of them does not merge.
