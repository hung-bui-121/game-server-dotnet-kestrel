# Release notes draft — v0.7.0 (slice 013 consumer-readiness)

Input of the `v0.7.0` annotated tag message (FR-090). Each user story appends its part; the final text is copied into the
tag message when the slice closes.

## Fixed (FG ids)

- **FG-010 (HIGH, security)** — Dev-profile (cheat) endpoints and commands were refused only in Production: a host that
  registered `CheatModule` in beta or uat gave every player with a real bearer token a free resource grant. The Dev
  profile is now allowed only where development tools are allowed (Development/Test: flavors `local`, `dev` and framework
  test hosts), decided by one property `GameServerOptions.AllowsDevProfile` (= `AllowsDevelopmentTools`, no setting) and
  enforced by three independent layers:
  1. startup validation: one report line per Dev endpoint and per registered Dev command (also service-only modules,
     `Hosting:MapPlayerApi=false`, commands without route and `--provision`), exit 1;
  2. request pipeline: a Dev route reached anyway (mapped after `UseGameServer`) answers 403 `FORBIDDEN` before any
     authentication scheme runs (no session touch, no login/resume analytics event, no CSRF/body handling), with one
     Warning naming method, route template and environment; an unknown security-profile value is refused the same way;
  3. command runner: Dev commands are refused with `FORBIDDEN` for every actor on `ExecuteAsync` and `TryReplayAsync`,
     before the per-player limiter, the receipt read, the guards and the transaction (a Dev receipt from a development
     store is never replayed); `command_results_total{code=FORBIDDEN}` counts both paths.
  Development and Test behave as in `v0.6.0`.

## Breaking changes and migration

### BC-1 — Dev endpoints and commands fail startup outside Development/Test

Was: only Production. Now: every deployment environment other than Development and Test (Staging = beta, uat;
Production = live; any other value), one line per Dev endpoint and per Dev command in the aggregated startup report.

Migration:

1. Register cheat/Dev modules only inside `if (gameServer.Options.AllowsDevelopmentTools)` (as
   `samples/SampleHost/Program.cs`).
2. Start each non-development flavor once (`--provision` included) to confirm.
3. Composition tests that build Staging/Production hosts with a Dev module: expect the new failure or drop the module.
4. The `FRAMEWORK-GAP FG-010` markers can go; the condition stays as hygiene.

### BC-2 — Dev requests and Dev commands are refused at run time outside Development/Test

A Dev route mapped after `UseGameServer` answers 403 `FORBIDDEN` (error contract) plus a Warning outside
Development/Test; the runner refuses Dev commands there (jobs, GM flows, direct `ICommandRunner` callers).

Migration: same as BC-1; jobs or GM flows that call Dev commands in Staging must use a non-Dev command.

### BC-3 — startup message text for Dev endpoints changed, new line for Dev commands

Old:

```text
[endpoint POST /api/v1/cheat/grant] uses profile Dev in Production. Fix: do not register cheat/dev modules in Production (OPS-09).
```

New (example for beta):

```text
[endpoint POST /api/v1/cheat/grant] uses profile Dev in deployment environment 'Staging' (environment id 'beta'). Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09, SEC-08).
[module economy.cheat] command 'cheat.grant' has profile Dev in deployment environment 'Staging' (environment id 'beta'). Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09).
```

Runner refusal (`GameServerException` message, code `FORBIDDEN`):
`Dev command 'cheat.grant' refused in deployment environment 'Staging' (environment id 'beta').`
Pipeline Warning: `Dev endpoint POST /api/v1/cheat/grant refused in deployment environment Staging (beta).`

Migration: tests matching the old text match the endpoint label (`[endpoint POST /api/v1/cheat/grant]`) or the new text.

## Consumer workarounds now removable

| FG | Workaround today (zh-pvp-server) | Removable after `v0.7.0` | Stays |
| --- | --- | --- | --- |
| FG-010 | `src/ZhPvp.Host/ZhPvpModules.cs` marker on the `AllowsDevelopmentTools` condition; `tests/ZhPvp.Tests/Host/CompositionTests.cs` marker (test as the only protection) | Both markers (framework refuses Dev in beta/uat itself) | The condition and the 5-flavor composition test may stay as game hygiene; CLAUDE.md rule 7 |

## Docs not re-checked

## Manual checks
