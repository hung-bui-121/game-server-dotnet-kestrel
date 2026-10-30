# Specification Quality Checklist: Environments per flavor and easy local run (011)

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

- No [NEEDS CLARIFICATION]: owner decisions of 2026-10-30 close Q1 (drop `dbcache`), Q2/Q3 (local/dev → Development,
  beta/uat → Staging, live → Production) and task 4 (out of scope); Q4–Q7 use the plan defaults (overlay per deployment
  environment, one JSON file per document without hot reload and only framework queries, Production provisioning
  command in this slice, `-Lan` for local only with a warning). Points left open by the owner were decided as
  assumptions: flavor selected by host environment name, `EnvironmentId` = flavor name with enforcement only for the
  five standard names, fallback to host environment name kept only for valid deployment-environment names, Production
  requires TLS to Couchbase without override, provisioning as a switch of the host binary, port 5080.
- Names that appear in FRs (`GameServer:DeploymentEnvironment`, `EnvironmentId`, `GameServer:SecretKeys`, `g_<gameId>`,
  `txn_meta`, `/health/ready`, `/gm/v1/ui`, `tools/local/start-hosts.ps1`, port 5080) are fixed by the requirements
  document, the public contract (PCR) or existing tooling; the reference keys (`dbcache`, `moxiPort`, `bucketType`,
  `operationTimeOut`, `compressThreshold`, `localFolder`) come from the owner's input. New setting key names are left to
  the plan.
- Conflicts and traps found while specifying (resolved in the spec):
  - The base `samples/SampleHost/appsettings.json` carries development-only values (`DeploymentEnvironment=Development`,
    `Store=InMemory`, `EnvironmentId=dev`, GM staff seed with password). Any flavor that forgets to override them would
    inherit Development; configuration arrays merge by index, so a flavor file cannot remove the staff seed → FR-005.
  - `GameServerBuilder` falls back to the host environment name when `DeploymentEnvironment` is unset, and no value is
    validated: a `Local` or `Dev` host silently runs as "neither Development nor Production" → FR-003.
  - `tools/local/start-hosts.ps1` sets no host environment name (host framework default: Production) and relies on the
    base file's Development → after FR-005 it must select the dev flavor (FR-033).
  - OPS-09 allows cheats whenever environment ≠ Production, while the code allows development tools only in
    Development/Test; with beta/uat = Staging this means no cheats, test auth or GM seed on beta/uat (stricter than the
    requirement, consistent with SEC-03) → FR-010, story 3 scenario 6.
  - SEC-01 requires database TLS but `CouchbaseOptions`/`CouchbaseConnection` have no TLS setting → FR-011, FR-014.
  - DAT-07 asks for an ops command in Production, but the only path today is `ProvisionOnStartup=true`, which needs
    management rights on every API instance (against least privilege) → FR-029..FR-032.
  - The runtime secret-source check is skipped in Development, so the dev flavor file (shared Couchbase) could carry a
    password unnoticed → FR-006 repository test.
  - CFG-01 overlay per deployment environment makes beta and uat share `config/env/Staging` (owner Q4 default) → FR-008,
    edge case.
  - The file store is not a substitute for Couchbase integration tests (TST-01, constitution VI) → assumption.
  - The SDK default query timeout (75 s) exceeds the GM request timeout (30 s) → query timeout default inside it.
- Risks for the plan: the `WebApplicationFactory<Program>` smoke test of the sample host runs as `Development` and reads
  the base file; after FR-005 it must supply its own settings. The file store touches `InMemoryDocumentStore`, which
  010 just changed; the conformance suite must stay green on all stores.
