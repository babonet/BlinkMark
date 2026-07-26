# Specification Quality Checklist: BlinkMark Core

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-07-26
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

- Items marked incomplete require spec updates before `/speckit.clarify` or `/speckit.plan`.

### Validation iteration 1 — 2026-07-26

**Outstanding**: one `[NEEDS CLARIFICATION]` marker at FR-055 (who may view and comment on
a file). This is a scope and security decision with two reasonable readings and no safe
default, so it was not guessed.

**Resolved during authoring (no marker raised)**:

- Agent interface shape (protocol/transport) — deliberately left to the plan. FR-051 and
  FR-052 state the *requirement* (structured machine-readable content, discoverable
  capability description) without naming a mechanism, keeping the spec technology-agnostic.
- File size limit, supported extensions, file immutability, audit retention period, and
  viewport targets — reasonable defaults recorded in Assumptions rather than raised as
  clarifications.

### Validation iteration 2 — 2026-07-26 — ALL ITEMS PASS

Both open questions were answered by the user; the spec was updated and re-validated.

| Question | Answer | Spec changes |
|---|---|---|
| Q1 — file access scope | **A** — any authenticated organization member holding the link may view and comment | Former FR-055 marker replaced by FR-056/FR-057/FR-058; new US1 acceptance scenario 4 (forwarded link); new SC-016 (uploader is shown access scope); new edge case (link forwarded beyond intended audience); new Assumption stating the link *is* the access grant and that access lists, invitations, and group scoping are out of scope |
| Q2 — unattended agent operation | **A** — delegated sessions only | New FR-055 (no standing agent identity, no acting for a signed-out user); new US5 acceptance scenario 5; SC-014 extended to probe for signed-out agent activity; Assumption added placing background/scheduled agent work and service-level agent identities out of scope |

Requirement count is now 58 functional requirements and 16 success criteria.

**Residual risk accepted by Q1-A**: link possession equals access within the organization.
FR-057 and SC-016 mitigate by disclosure at upload time rather than by access control. If
confidentiality expectations change, this returns as a new feature, not a bug fix.

**Constitution alignment spot-check** (v1.1.0):

| Principle | Covered by |
|---|---|
| I. Tenant-Only Secure Access | FR-001–FR-005, FR-055–FR-058, SC-007 |
| II. Ephemeral by Default | FR-026–FR-033, SC-006 |
| III. Anchored Comments Must Survive | FR-019, FR-022, SC-003, SC-010 |
| IV. Untrusted Content Is Never Trusted | FR-007, FR-008, FR-013, FR-014, FR-016, FR-024, SC-008 |
| V. Auditable and Observable | FR-041–FR-045, FR-049, SC-009, SC-015 |
| VI. Contract-First, Test-Backed Delivery | Enforced at plan/PR time; FR-052 also requires a published machine-readable contract |

### Validation iteration 3 — 2026-07-26 — ALL ITEMS STILL PASS

Scope addition requested by the user: show who and how many users are currently on a
document. Added as **User Story 6 — live presence (P4)**, with FR-059–FR-069, SC-017–SC-021,
a new *Presence Session* entity, three edge cases, and four assumptions.

No `[NEEDS CLARIFICATION]` markers were raised. Decisions taken as documented assumptions:

| Decision | Choice | Why no clarification needed |
|---|---|---|
| Invisible / lurker mode | Not offered | The display's value depends on it being trustworthy; a partial display is worse than none |
| Multi-tab and multi-device | Deduplicated by person | "3 viewers" must mean three people, not three tabs |
| Staleness window | About one minute | Standard trade-off between display accuracy and tolerance of flaky networks |
| Historical presence | Out of scope | Audit trail (FR-041) already answers "who viewed this"; FR-068 forbids presence substituting for it |

Requirement count is now 69 functional requirements and 21 success criteria across 6 stories.

**Plan-time constraints this creates** (not spec violations — flagged for `/speckit.plan`):

- Presence is inherently shared, live state, while the constitution's Platform and Technology
  Constraints require stateless, horizontally scalable services with "no in-process session or
  file caches that break under multi-instance deployment". The naive in-memory implementation
  is therefore already prohibited; presence needs a shared backplane external to the API
  instances. The plan must name it.
- FR-069 (degrade gracefully without presence) exists so this backplane can never become a
  dependency of the P1 and P2 paths.
- FR-067 and the assumption that presence is never stored keep this out of scope for
  Principle II retention machinery — there is no presence data to expire.
- Adding a real-time transport is the first material addition to the cost profile agreed for
  v1.1.0 of the constitution. The plan should size it and, if it needs a service tier beyond
  what the constitution names, record it in Complexity Tracking.

### Validation iteration 4 — 2026-07-26 — post-`/speckit.clarify`, ALL ITEMS PASS

Five clarification questions asked and answered; all five integrated. A `## Clarifications`
section with a `### Session 2026-07-26` subheading now records them, one bullet per answer.

| # | Question | Answer | Integrated as |
|---|---|---|---|
| 1 | Audit trail access | No in-product access; operators only, operational docs out of scope | FR-070; assumption stating there is no admin or compliance role |
| 2 | File download | Owner-only, comments included | FR-071–FR-075; US3 scenarios 7–9; SC-022; download assumption |
| 3 | Availability/durability | Best-effort, single region, no DR, loss accepted | FR-076; SC-023; durability assumption |
| 4 | Accessibility | WCAG 2.1 Level AA | FR-077–FR-082; SC-024, SC-025; scoping assumption |
| 5 | Human usage limits | Per-user live-file cap + upload rate limit | FR-083–FR-088; SC-026, SC-027; 2 edge cases; limits assumption |

Requirement count is now 88 functional requirements and 27 success criteria across 6 stories.

**Inconsistency found and repaired**: FR-041 listed "download" as an auditable action while no
requirement granted download at all. Q2 resolved it in favour of adding the capability
(owner-only) rather than striking the audit entry.

**Cross-answer interactions worth carrying into the plan**:

- Q1 (no administrator) + Q5 (quotas) forced FR-088: capacity must free itself as files expire,
  because there is nobody to unblock a user who hits the cap.
- Q2 (owner download) + Q3 (no backup) makes download the only way anyone preserves a review.
  FR-075 and FR-076 exist so users understand this rather than discovering it after a loss.
- Q4 (WCAG 2.1 AA) + US2/US6 is the largest design constraint added: FR-078 (keyboard-only
  passage selection) and FR-082 (non-dragging region selection) mean the anchored-commenting
  interaction model cannot be pointer-first, and FR-080 constrains how presence updates
  announce themselves.
- Q5 counts agent uploads against the represented user's quota (FR-087), closing delegation as
  a route around a personal limit.

**Taxonomy coverage after clarification**:

| Category | Status |
|---|---|
| Functional scope & behavior | Clear |
| Domain & data model | Clear |
| Interaction & UX flow | Resolved (accessibility was Missing) |
| Non-functional: performance, scalability | Clear |
| Non-functional: reliability & availability | Resolved (was Missing) |
| Non-functional: observability | Clear |
| Non-functional: security & privacy | Clear |
| Compliance / regulatory | Resolved (audit access was Partial) |
| Integration & external dependencies | Resolved (download/export was Partial) |
| Edge cases & failure handling | Resolved (rate limiting was Partial) |
| Constraints & tradeoffs | Clear (governed by constitution v1.1.0) |
| Terminology & consistency | Clear |
| Completion signals | Clear |
| Misc / placeholders | Clear — no markers remain |

No Outstanding or Deferred categories. Spec is ready for `/speckit.plan`.

### Validation iteration 5 — 2026-07-26 — post-`/speckit.analyze` remediation

Cross-artifact analysis found 16 issues (1 critical, 3 high, 6 medium, 6 low). All were remediated.

| ID | Severity | Issue | Resolution |
|---|---|---|---|
| I1 | CRITICAL | Preview host had no defined authorization mechanism, violating Principle I | research.md R13 + new `contracts/preview-origin.md` define a 15-minute, single-file, single-render preview token; T118 added and made a blocker for T026/T039 |
| C1 | HIGH | Preview endpoint absent from all contracts, violating Principle VI | `contracts/preview-origin.md` created; T119 contract test added |
| C2 | HIGH | FR-049 / SC-015 agent-distinguished audit had no task | T124 added; T088 extended to assert the distinction |
| I2 | HIGH | Immutability assumption contradicted US2 scenario 4, its Independent Test, and its narrative | All four reconciled to research.md R2 — orphaning arises from render drift and ambiguous matching, not user edits |
| I3 | MEDIUM | FR-087 assumed agent upload; MCP manifest omits it | FR-087 reworded as forward-looking |
| C3 | MEDIUM | FR-010 encryption had no task | T121 added |
| C4 | MEDIUM | FR-081 preview keyboard focus had no task | T123 added |
| C5 | MEDIUM | FR-079 comment state to assistive tech had no task | T122 added |
| C6 | MEDIUM | SC-007/SC-008 required continuous verification; only one-time tests existed | T125 added |
| I4 | MEDIUM | FR subsections were out of numeric order | Reordered to strict ascending: 001–069, 070, 071–076, 077–082, 083–088. No IDs changed |
| C7 | LOW | FR-070 operator retrieval | Accepted as inherent; covered by T113 |
| C8 | LOW | SC-023 availability measurement had no task | T126 added |
| A1 | LOW | Presence enumeration cap unspecified | Fixed at 8 named viewers in FR-066 and the presence contract |
| A2 | LOW | Deleted comments in download bundle unspecified | FR-073 now excludes author-deleted comments |
| I5 | LOW | tasks.md claimed 52 `[P]`, actual 50 | Metrics recomputed and verified: 126 tasks, 56 `[P]`, 0 duplicate IDs |
| I6 | LOW | "organization" vs "tenant" drift | Assumptions now state the terms are interchangeable |

**Coverage after remediation**: 88 / 88 functional requirements have at least one task. Zero
critical, high, or medium findings remain.

**Note on the critical finding**: I1 was a genuine design hole, not a documentation gap. Isolating
the preview onto its own origin to satisfy Principle IV removed the session that Principle I
relies on, and no artifact replaced it. The two principles were in tension and nothing reconciled
them. The preview token resolves it using the same shape the constitution already sanctions for
blob access. It also fixed a second problem nobody had noticed: FR-041 requires auditing `preview`
separately from `view`, and only the preview host can know whether an issued token was redeemed.
