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
| Azure CLI | latest | Provisioning, Entra app registration |
| Azure Developer CLI (`azd`) | latest | One-command environment |
| Docker | latest | Local Redis, Cosmos and Azurite emulators |

An Azure subscription and permission to register an application in the tenant. Registration
requires an admin to consent to the Graph `Mail.Send` application permission — start that request
early, it is the usual long pole.

---

## Local setup

```powershell
git clone <repo> ; cd BlinkMark
docker compose up -d          # Redis, Azurite, Cosmos emulator
dotnet restore
cd frontend ; npm install ; cd ..
cp .env.example .env          # then fill in the Entra values below
```

### Entra app registration

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

### Run

```powershell
dotnet run --project backend/src/BlinkMark.Api        # https://localhost:7001
dotnet run --project backend/src/BlinkMark.Preview    # https://localhost:7002  ← separate host
cd frontend ; npm run dev                             # http://localhost:5173
```

**The preview host must run on a different hostname, not just a different port.** Same-host
different-port still shares an origin for some purposes and will let you build something locally
that violates Principle IV in production. Add to your hosts file:

```
127.0.0.1  blinkmark.localtest.me
127.0.0.1  preview.blinkmark.localtest.me
```

---

## Verify the walking skeleton

```powershell
# 1. Upload
curl -H "Authorization: Bearer $env:TOKEN" -F "file=@sample.md" https://blinkmark.localtest.me:7001/api/files

# 2. Preview — note the previewUrl is on the preview host, never the API host
curl -H "Authorization: Bearer $env:TOKEN" https://blinkmark.localtest.me:7001/api/files/{id}

# 3. Comment
curl -H "Authorization: Bearer $env:TOKEN" -H "Content-Type: application/json" `
  -d '{"body":"tighten this","anchor":{"kind":"text","exact":"lorem ipsum","renderVersion":"1"}}' `
  https://blinkmark.localtest.me:7001/api/files/{id}/comments

# 4. Presence
curl -N -H "Authorization: Bearer $env:TOKEN" https://blinkmark.localtest.me:7001/api/files/{id}/presence
```

### The four checks that matter

Run these before believing anything works:

1. **Unauthenticated request returns nothing.** `curl` any endpoint with no token. You should get
   401 with no filename, no metadata, no hint the file exists (FR-001).
2. **Script does not execute.** Upload `tests/fixtures/hostile/inline-script.html` and open the
   preview. If an alert fires, stop and fix it before writing another line (FR-013, SC-008).
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
- **Constrain `Mail.Send` with an Application Access Policy.** Ungranted, that permission can send
  as any mailbox in the tenant. Scope it to the single service mailbox before first send.
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
