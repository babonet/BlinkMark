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
