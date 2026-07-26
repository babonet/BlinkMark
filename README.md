# BlinkMark

Share a short-lived HTML or Markdown draft with your organization, collect comments anchored to
specific passages, and let the file delete itself.

Files expire after 24 hours by default and can never outlive 30 days. Comments that lose the
passage they were attached to surface as orphaned rather than moving or vanishing. Uploaded
content is sanitized once and served from an isolated origin that has no access to the
application's session. AI agents reach the same capabilities strictly on a signed-in user's
behalf.

## Where things are decided

| Document | What it settles |
|---|---|
| [Constitution](.specify/memory/constitution.md) | The seven principles everything else answers to |
| [Specification](specs/001-blinkmark-core/spec.md) | 88 functional requirements, 27 success criteria, 6 user stories |
| [Research](specs/001-blinkmark-core/research.md) | The 14 design decisions the fixed stack does not answer |
| [Plan](specs/001-blinkmark-core/plan.md) | Architecture, complexity tracking, risks |
| [Data model](specs/001-blinkmark-core/data-model.md) | Entities across Cosmos, Blob, Table, and Redis |
| [Contracts](specs/001-blinkmark-core/contracts/) | REST, MCP, preview origin, presence stream |
| [Quickstart](specs/001-blinkmark-core/quickstart.md) | Developer onboarding and the four verification checks |
| [Tasks](specs/001-blinkmark-core/tasks.md) | The implementation breakdown and its current state |

## Layout

```text
backend/
  src/
    BlinkMark.Core/            Domain: rendering, anchoring, retention, quotas, preview tokens
    BlinkMark.Infrastructure/  Adapters: Cosmos, Blob, Table, Queue, Redis, Key Vault
    BlinkMark.Api/             REST, SSE presence, MCP endpoint
    BlinkMark.Preview/         The isolated preview origin, on its own hostname
    BlinkMark.Jobs/            Retention reconciliation and notification dispatch
  tests/
    contract/                  OpenAPI and preview-origin conformance
    integration/               The four mandatory areas under Principle VI
    unit/                      Domain rules in isolation
    fixtures/hostile/          The sanitization corpus
frontend/                      React 18 + TypeScript + Vite, MSAL authentication
infra/                         Bicep, plus Entra app registration
tools/                         SFI compliance gate and posture verification
```

## Running it locally

```powershell
docker compose up -d          # Redis, Azurite, Cosmos emulator
dotnet build BlinkMark.sln
dotnet test BlinkMark.sln

cd frontend
npm install
npm run dev
```

Local development runs against emulators. No developer ever needs data-plane access to a deployed
resource, which is what makes the private-endpoint posture liveable.

## Two things worth knowing before you change anything

**There are no secrets, and that is enforced.** Every resource is provisioned with local
authentication disabled, and every Azure call uses a user-assigned managed identity.
`tools/sfi-gate.ps1` runs first in CI and fails the build — not warns — if a template would
re-enable local auth or if a connection string, account key, or client secret appears anywhere in
source, configuration, or pipeline definitions. `tools/verify-sfi-posture.ps1` then checks the
same properties on the deployed resources, because a correct template and a correct resource are
different claims.

**The 30-day retention ceiling is data, not validation logic.** `maxExpiresAt` is computed once at
upload and stored on the file. Every later write of `expiresAt` is checked against that stored
value at the data layer, so a new endpoint cannot forget the rule and a ceiling recomputed from
"now" cannot let a file live forever, thirty days at a time.

## Verification

```powershell
./tools/sfi-gate.ps1 -RepositoryRoot .     # credential-free posture
dotnet test BlinkMark.sln                  # unit, integration, contract
az bicep build --file infra/main.bicep     # infrastructure compiles
cd frontend; npm run lint; npm test        # frontend
```
