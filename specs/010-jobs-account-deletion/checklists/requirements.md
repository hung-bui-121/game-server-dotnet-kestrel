# Specification Quality Checklist: Lease jobs and account deletion (010)

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-10-30
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

- No [NEEDS CLARIFICATION]: TBD-04c is closed (30-day grace, anonymized ledger). Points the requirements leave open
  were decided as assumptions: GM-only cancel (no self-cancel by login), "hide identity" = reserved but refused until
  purge, cancel window ends at `purgeAfter`, random anonymous id with the link discarded after purge, purge record kept
  without personal data, deletion treatment declared per collection (startup fail otherwise).
- Phase boundary: JOB-01 is P1 in the phase table but PRIV-01 (P0.5) needs a lease job, so only the minimal lease part
  (lease document, CAS, fencing token, safe re-run) is in scope; generic durable progress documents, JOB-02 job
  consumer, ACC-15 and GM job pages stay P1. PRIV-02 export stays P1.
- Names that appear in FRs (`profile.deleted`, `sessionEpoch`, `ACCOUNT_DELETED`, `player.ban`, `player.read`,
  `leases::{jobName}`, `IPlayerAggregateWriter`) are fixed by the requirements document or the public contract (PCR),
  not design choices of this spec.
- Gap found, not fixed here: the receipt retention purge after 180 days (CMD-08) is not implemented yet; it should reuse
  this slice's job runner.
