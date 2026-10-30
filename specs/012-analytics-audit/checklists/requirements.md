# Specification Quality Checklist: Analytics audit log for Login and Payment (012)

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-10-31
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

- 2026-10-31: spec reconciled with plan.md deviations SD1–SD13 (Clarifications session 2026-10-31); FR-039 (uniform
  `Enabled`) and FR-040 (reserved outbox kind) added at the end, FR numbering otherwise unchanged. All items above still
  pass.

- No [NEEDS CLARIFICATION]: owner decisions of 2026-10-30 close Q8 (JSONL per instance and event type, one file per UTC
  day) and Q11 (raw `playerId`, 25-month retention, documented); Q9, Q10 and Q12 use the plan defaults (`chargeTime` =
  server commit time with optional store time; resume event at most once per player per day; zone per deployment with
  per-event offset and a UTC copy). Points left open were decided as assumptions: `transactionID` = store transaction id
  (Apple transaction id, Google order id, digest fallback) instead of the claim reference; login event on success only;
  resume dedupe per instance in memory over the deployment's local day; file day by UTC write time; missing zone fails
  in Staging/Production and defaults to UTC in Development/Test; sample flavor files use `Asia/Ho_Chi_Minh`; analytics
  directory separate from logs; no server-side deletion of analytics files by default.
- Names that appear in FRs (`userID`, `transactionID`, `chargeTime`, `activeTime`, `payment`, `login`, `source = resume`,
  `GameServer:Analytics`, `iap.purchase`, `--provision`, `HOSTNAME`) come from the owner's table, the agreed prior
  analysis or existing contracts; other JSON field names and setting keys are left to the plan and PCR.
- Conflicts and traps found while specifying (resolved in the spec):
  - The Google external reference stored by IAP (and used as the claim key) is the full purchase token, a credential-like
    value; shipping it as `transactionID` would break LOG-05 → FR-008 uses the store's transaction id.
  - Both verifiers already refuse test/sandbox purchases in Production, so `isTest` can only be true outside live; the
    "sandbox mixed into revenue" risk is about environments sharing a data lake → `isTest` + `environmentId` (FR-010,
    FR-033). `VerifiedPurchase` has no test flag today; Google computes it but discards it, Apple checks the environment
    but does not return it → FR-010.
  - The inline outbox relay is awaited before the purchase response, so a slow analytics write would delay purchases →
    FR-013 bounds it and falls back to the sweeper.
  - The relay marks a receipt relayed only when every consumer succeeds; a failing analytics sink keeps the IAP receipt
    pending (ledger re-inserted idempotently) and may dead-letter it → edge case; disabled analytics must still
    acknowledge the entries (FR-014).
  - Builds without the payment consumer (rolling deploy, rollback) cannot relay receipts carrying the new outbox kind →
    edge case and deploy note (FR-038).
  - JOB-02 allows in-memory events only when the loss is accepted and recorded in PCR → login best effort recorded (FR-004).
  - Receipt size limit 4 KiB (D2) and the 010 generic anonymization of receipts → FR-012.
  - Rejected commands already roll back staged outbox entries (no receipt for rejections), so FR-011 needs no new
    mechanism for ALREADY_GRANTED / INVALID_RECEIPT.
  - The instance id is resolved in the observability options; analytics must use the same resolution (FR-022), and two
    processes sharing a directory without distinct ids must fail fast (FR-025).
  - `--provision` mode (011) must not require analytics settings or create files (FR-030).
  - Serilog rolls by machine-local time; the file day must come from the server clock in UTC (FR-023).
  - Session reuse (30-day sliding session) means no Login call on most days → resume event (FR-017, FR-018).
- Risks for the plan: losing files when a pod is replaced (persistent volume or shipper, LOG-03 policy, deploy
  checklist); container images without time zone data or in invariant globalization mode (startup fail, FR-021);
  Windows developer machines need IANA zone resolution; tests (WebApplicationFactory) must not write analytics files into
  the repository — use the in-memory sink or a temp directory; PRIV-01 legal exposure of raw ids in shipped files
  accepted by the owner and documented (FR-036).
