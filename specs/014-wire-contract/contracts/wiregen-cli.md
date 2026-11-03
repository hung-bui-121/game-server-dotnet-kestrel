# Contract — host export switch and the WireGen tool

Consumer-facing contract of the generation step (FR-020..FR-028, FR-030). Changing a switch, an exit code, the config
format or the export format is a breaking change (PCR-01). Config fields: [data-model §4](../data-model.md#4-generator-configuration-wiregenjson-format-gameserverwiregen1);
export: [data-model §3](../data-model.md#3-catalog-export---export-wire-contract-format-gameserverwire-contract1);
file formats: [generated-files.md](generated-files.md).

## 1. Host switch `--export-wire-contract <path>`

```text
dotnet run --project <host.csproj> --no-launch-profile [--no-build] [-c <cfg>] -- --environment Local --export-wire-contract <path>
```

| Item | Behaviour |
| --- | --- |
| Parsing | `GameServerCommandLine.Parse`: the switch (any letter case) and the next argument are removed from `HostArgs`; `Mode = ExportWireContract`, `ExportPath = <path>`; the path reaches `RunGameServerAsync(mode)` as one appended internal argument `--GameServerCommandLine:ExportWireContract=<path>` (the host passes only the mode) |
| Refused (exit 1) | no path after the switch (none, or the next argument starts with `-`); `--export-wire-contract=…`, `-export-wire-contract`, `/export-wire-contract`; given twice; combined with `--provision` |
| Run | `RunGameServerAsync`: resolve `WireCatalog` → registry rules (`ModuleRegistry.Validate(options)`, no game config, as `--provision`) + `WireCatalog.Validate()` → write `ToExportJson()` to `<path>` (folder must exist; UTF-8 without BOM, LF) → dispose → exit 0 |
| Never | `UseGameServer`, hosted services, store open, game config load, secret read, port bind, traffic |
| Errors (exit 1) | registry and declaration faults (data-model §2.1) on stderr, one line each; output folder missing; file not writable |
| Requirement on the host | `Program` uses `GameServerCommandLine.Parse(args)` + `app.RunGameServerAsync(mode)` (as `samples/SampleHost/Program.cs`); composition decisions that depend on the flavor run with the flavor passed by `--environment` |

## 2. WireGen command line

```text
dotnet run --project <framework>/tools/GameServer.WireGen -c Release -- <verb> --config <wiregen.json> [options]

verbs:
  generate   write the generated files that differ from what is on disk
  verify     write nothing; exit 2 when any output file differs or is missing

options:
  --config <file>          required; wiregen.json (format gameserver.wiregen/1)
  --output-root <dir>      overrides output.root (e.g. the Unity client checkout)
  --catalog <file>         use this export instead of config.catalog (no host run)
  --configuration <cfg>    configuration used to build/run the host (default Release)
  --no-build               pass --no-build to the host run (CI after a solution build)
  --dotnet <path>          dotnet executable for the host run and the version evaluation (default: DOTNET_HOST_PATH,
                           the dotnet running the tool, DOTNET_ROOT/dotnet, dotnet on PATH; V15)
```

`--help` prints the usage (exit 0). The host run uses the host project's folder as working directory.

## 3. Exit codes

| Code | Meaning |
| --- | --- |
| 0 | `generate`: done (files written or already up to date). `verify`: every output equals what would be generated |
| 1 | input, configuration or contract error; nothing written. One line per problem: what, where, fix |
| 2 | `verify` only: one or more output files differ or are missing (each listed relative to the output root) |

## 4. Steps and their failures (all before any write)

| # | Step | Failure (exit 1) message names |
| --- | --- | --- |
| 1 | Read config (format, required fields), resolve paths relative to the config folder (`Path.GetFullPath`) | the field and the expected format |
| 2 | `output.root` exists, `output.marker` exists under it, `output.directory` exists under it | the missing path and the option (`--output-root`) to fix it |
| 3 | Every listed proto exists under its `root` | the proto path and its root |
| 4 | Catalog: run the host export (§1) into a temp file, or read `--catalog` / `catalog.file`; `format` major = 1 | host exit code + stderr lines, or the bad file |
| 5 | Version guard: export `frameworkVersion` = tool's framework version; export `protobufVersion` = pinned `Google.Protobuf`; with `hostProject`: `dotnet msbuild <host> -getItem:PackageVersion` → `Grpc.Tools` and `Google.Protobuf` = pinned | both versions and which side to change |
| 6 | protoc descriptor pass over consumer protos + all framework protos | protoc's own message (file:line), e.g. duplicate symbol |
| 7 | C# full-name clash between a consumer and a framework message | both messages and files |
| 8 | Selection and checks: section message known (consumer or framework protos); generated member names unique; player-facing templates expressible (no constraint, optional, default, catch-all); one method per player-facing route | the route or section and the rule |
| 9 | protoc C# pass, one run per consumer proto into a temp folder (layout flat / by-root) | protoc message; "expected output for <proto> missing" when protoc wrote no `.cs` |
| 10 | Render routes and sections files; normalize every file: `\r\n`/`\n` → selected line ending, UTF-8 without BOM | — |
| 11 | `generate`: for each file, compare bytes with disk; write only when different (temp file in the same folder, then replace); create subfolders only inside `output.directory` (by-root). `verify`: list differences | I/O error with the path |

After a `generate` run the tool prints one line per written file, one line per unchanged count, and for every file it
created: `new file <path>: Unity creates <path>.meta on import; commit it` (inside a UPM package folder the `.meta` must
be authored and committed with the file). Warnings (exit code unchanged): each untyped section (id constant only).

## 5. Guarantees

| Guarantee | How |
| --- | --- |
| Determinism (FR-023) | inputs only from config, protos and export; ordinal ordering; no timestamp, absolute path, user or machine in output; protoc gets protos as paths inside their import roots |
| Framework types never regenerated (FR-021) | the C# pass gets consumer protos only; framework protos are only `-I` roots and descriptor-pass inputs |
| Framework import roots without listing (FR-021) | `proto/`, `src/GameServer.Modules.{Identity,Economy,Iap}.Adapters/Protos`, `Grpc.Tools` well-known includes, always added |
| Pinned protoc (FR-022) | the tool's own restored `Grpc.Tools` (2.84.0), never the consumer's |
| No clean deletion (FR-026) | outputs are not items of any MSBuild project; the tool never deletes |
| `.meta` untouched (FR-026) | the tool reads, writes and deletes only the `.cs` files it generates |
| No folder outside the output (FR-025) | steps 2–3 fail before anything is created; only by-root subfolders inside `output.directory` |

## 6. Typical commands

```text
# framework repo: refresh the Unity package and the sample client
dotnet run --project tools/GameServer.WireGen -- generate --config sdk/GameServer.Client/wiregen.json
dotnet run --project tools/GameServer.WireGen -- generate --config samples/SampleGame/wiregen.json

# game repo (framework submodule at framework/), client in a sibling checkout, CRLF in the config
dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config tools/unity-codegen/wiregen.json
dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config tools/unity-codegen/wiregen.json --output-root D:/work/research-and-development-games/src/GameClient

# game CI
dotnet run --project framework/tools/GameServer.WireGen -c Release -- verify --config tools/unity-codegen/wiregen.json --no-build
```
