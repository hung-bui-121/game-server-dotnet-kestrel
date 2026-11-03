# Contract — local run scripts with host parameters (013, FR-040..FR-046)

Supersedes `specs/011-local-env/contracts/operations-cli.md` §4 (that section gets a "superseded by" note). Sections 1–3,
5 and 6 of the 011 contract are unchanged. Documented for developers in `docs/guides/environments-and-local-run.md` and
`docs/guides/consumer-guide.md`.

## 1. Invocation

```text
tools/local/run-local.ps1 [-HostProject <path>] [-LaunchProfile <name>] [-Name <text>] [-Port <int>] [-Reset] [-Lan]
tools/local/run-local.sh  [--host-project <path>] [--launch-profile <name>] [--name <text>] [--port <int>] [--reset] [--lan]
```

From a game repository: `framework/tools/local/run-local.ps1 -HostProject src/ZhPvp.Host -LaunchProfile local -Name "ZhPvp"`.

| Option | Default | Effect |
| --- | --- | --- |
| host project | framework `samples/SampleHost/SampleHost.csproj` | project file, or a folder containing exactly one `*.csproj`; relative paths resolve against the current directory; the resolved path is printed |
| launch profile | `local` | profile of the host's `Properties/launchSettings.json`; gives the flavor (`ASPNETCORE_ENVIRONMENT`) and the default port |
| name | `Local game server` (sample host), else the project name | banner title |
| port | first `http://` URL port of the profile; none → 5080 with a banner note | `--urls http://localhost:<port>` (`0.0.0.0` with LAN) |
| reset | off | host gets `--FileStore:ResetOnStart=true` (the host empties only its configured store folder after locking it) |
| lan | off | listen on all interfaces; red warning: the development tools of this host (test authentication, cheats, staff seed when configured) are reachable from the network; LAN IPv4 URLs |

Without host project, launch profile and name, both scripts behave exactly as the slice 011 contract (SampleHost,
profile `local`, port 5080, same banner, same exit codes).

## 2. What the scripts read (never write) from the host

Resolved by `tools/GameServer.LocalRun describe` (same code for both scripts, data-model §7):

- `Properties/launchSettings.json` of the host project (comments allowed): profile → `applicationUrl` (port),
  `environmentVariables.ASPNETCORE_ENVIRONMENT` (flavor, required).
- `appsettings.json` + `appsettings.{flavor}.json` of the host project + environment variables (the host's own layering):
  `FileStore:Folder` (data folder: banner, `-Reset` message), `Observability:Logging:Directory` (log folder, default
  `logs`: failure tail), both resolved against the host project folder (content root of `dotnet run`).
- `sdk.version` of the nearest `global.json` above the host project: the version named in SDK messages (never hardcoded).

The scripts never create, edit or delete a file of the consumer repository; the resolver's build output lives in the
framework's `tools/GameServer.LocalRun/bin|obj` (git-ignored).

## 3. Sequence

1. Arguments → exit 2 on unknown/invalid ones.
2. SDK: `dotnet` on PATH, else `~/.dotnet10`, accepted when `dotnet --version` succeeds in the host project folder
   (honours the governing `global.json`) → exit 3 naming the version and `global.json` path.
3. Describe the host → exit 2 with the resolver's message (missing project, no launch settings, unknown profile listing the
   defined ones, profile without `ASPNETCORE_ENVIRONMENT`, unreadable JSON, no `global.json`); resolver build failure →
   exit 5.
4. Port probe (loopback IPv4/IPv6, plus any with LAN) → exit 4 naming the port and `-Port`/`--port`.
5. `dotnet build <project> -c Debug` (output visible) → exit 5.
6. `dotnet run --no-build --launch-profile <profile> -- --urls … [--FileStore:ResetOnStart=true]` from the project folder;
   wait for `GET /health/ready` = 200 (≤ 60 s) → exit 6 if the host exits first (its message above), exit 7 on timeout
   (last 20 lines of the newest file in the log folder, host stopped).
7. Banner, then stream host output until Ctrl+C (graceful stop; kill after 35 s). Exit code 0 after a graceful stop,
   otherwise the host's exit code.

## 4. Banner

Sample host (unchanged text):

```text
Local game server is ready (flavor Local, data ../_database/dbbase)
  Player API   http://localhost:5080/          (routes /api/v1/…, /v1/time)
  Health       http://localhost:5080/health/ready
  GM UI        http://localhost:5080/gm/v1/ui  (staff admin / dev-only-change-me)
  GM OpenAPI   http://localhost:5080/gm/v1/openapi/gm.json
Press Ctrl+C to stop.
```

Any other host (host-independent lines only):

```text
ZhPvp is ready (flavor Local, data E:/game/.local/data)
  Project      E:/game/src/ZhPvp.Host/ZhPvp.Host.csproj (profile local)
  Base URL     http://localhost:5180/
  Health       http://localhost:5180/health/ready
Press Ctrl+C to stop.
```

Plus the port note when the profile has no http URL, the reset note with `-Reset`, and the LAN block with `-Lan`. With the
request log and console sink on (local flavor), one line per request follows on the same console.

## 5. Exit codes

| Code | Meaning (message names the cause and the fix) |
| --- | --- |
| 0 | stopped by Ctrl+C after a graceful host shutdown |
| 2 | invalid script argument, or invalid host input (project, launch profile, settings, `global.json`) |
| 3 | .NET SDK of the governing `global.json` not found |
| 4 | port in use |
| 5 | build failed (host or resolver) |
| 6 | host exited before ready |
| 7 | readiness timeout (60 s) |
| other | host exit code after it was ready |

No new code is added; a consumer wrapper may use 8+ for its own checks (the first consumer uses 8 = submodule missing).
No path leaves a host running after the script exits.

## 6. Repeatable check (FR-041, SC-005)

| Check | Where | When |
| --- | --- | --- |
| exit 2: unknown argument, missing host project, unknown profile, host without launch settings | `RunLocalScriptTests` (ps1 on Windows; sh when bash is found) | every test run |
| exit 4: busy port | same | every test run |
| resolver rules (port, flavor, layering, env vars, comments, errors) | `LocalRunDescribeTests` | every test run |
| default invocation: ready, banner, graceful stop (exit 0) | `RunLocalScriptTests` | `GAMESERVER_SCRIPT_TESTS=1` |
| fixture host `tests/ConsumerFixture/src/Fixture.Host`: ready on 5180, banner name, `-Reset` empties its data folder, failing configuration → 6 + log tail | same | `GAMESERVER_SCRIPT_TESTS=1` |
| `run-local.sh` on macOS/Linux | manual, recorded in tasks.md | before release if a machine is available |
