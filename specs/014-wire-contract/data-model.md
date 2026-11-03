# Data model — 014 wire-contract

No persisted document, collection, index or wire message changes (FR-055). This file defines the in-memory catalog, the
startup report lines, the export and configuration file formats, the generated-name rules, the request-event field and the
conformance algorithm. C# signatures: [contracts/public-api.md](contracts/public-api.md).

## 1. Route catalog

### 1.1 `RouteDeclaration` (Core, immutable)

| Field | Type | Rule |
| --- | --- | --- |
| `Name` | string | `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, ≤ 64 chars; unique per host; command routes default to the operation; stable (renaming = breaking, ARC-08) |
| `Methods` | `IReadOnlyList<string>` | upper-case HTTP methods, ordinal-sorted, distinct; empty = any method (health probes) |
| `Template` | string | canonical: exactly one leading `/`; valid ASP.NET route template; version segment `(^|/)v[0-9]+(/|$)` unless `/health/live` or `/health/ready` |
| `Profile` | `SecurityProfile` | Public, Player, Gm, Dev |
| `Surface` | `RouteSurface` | `PlayerApi` (mapped iff `Hosting:MapPlayerApi`), `GmApi` (iff `Hosting:MapGmApi`), `Host` (always) |
| `Operation` | string? | command operation; set by `RouteDeclaration.Command` |
| `Permission` | string? | GM permission; required for profile Gm, null otherwise |
| `Condition` | string? | name of a registered hosting condition; null = always |

### 1.2 Catalog entries (Platform.Web)

`RouteEntry`: `Declaration`, `Owner` (module name, `platform`, or `host`), `Origin` (`Framework` | `Game`), `ServiceOnly`
(owner registered with `ServiceOnly()`).

`SectionEntry`: `Id` (aggregate id), `Owner` (module declaring the aggregate), `Origin`, `MessageFullName` (proto full
name, null for an untyped mapper), `HiddenMembers` (ordinal-sorted member paths), `MayStayDefaultFields` (ordinal-sorted
proto field paths). Only client-visible aggregates with a wire mapper.

Origin rule: `Framework` when the owner is `platform` or the module type's assembly name starts with `GameServer.`
(reserved prefix, documented in the consumer guide); else `Game`. Host declarations are `Game`.

Hosting conditions: `development_tools` (platform; `GameServerOptions.AllowsDevelopmentTools`), `gm.sso` (GM module;
`Gm:Oidc` configured).

### 1.3 Framework declarations (FR-002)

Templates are today's constants; nothing on the wire changes. Methods are those mapped today.

| Name | Method | Template | Profile | Surface | Owner | Operation | Condition |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `platform.time` | GET | `/v1/time` | Public | Host | platform | — | — |
| `platform.snapshot` | POST | `/api/v1/player/snapshot` | Player | PlayerApi | platform | — | — |
| `platform.config` | POST | `/api/v1/config/get` | Player | PlayerApi | platform | — | — |
| `platform.receipt` | GET | `/api/v1/player/receipts/{receiptId}` | Player | PlayerApi | platform | — | — |
| `platform.gm_openapi` | GET | `/gm/v1/openapi/{documentName}.json` | Public | GmApi | platform | — | development_tools |
| `platform.health_live` | any | `/health/live` | Public | Host | platform.observability | — | — |
| `platform.health_ready` | GET | `/health/ready` | Public | Host | platform.observability | — | — |
| `identity.login_guest` | POST | `/api/v1/identity/login/guest` | Public | PlayerApi | identity | — | — |
| `identity.login_provider` | POST | `/api/v1/identity/login/provider` | Public | PlayerApi | identity | — | — |
| `identity.logout` | POST | `/api/v1/identity/logout` | Player | PlayerApi | identity | — | — |
| `identity.set_display_name` | POST | `/api/v1/identity/display-name` | Player | PlayerApi | identity | `identity.set_display_name` | — |
| `identity.link` | POST | `/api/v1/identity/link` | Player | PlayerApi | identity | `identity.link` | — |
| `identity.request_deletion` | POST | `/api/v1/identity/delete-account` | Player | PlayerApi | identity | `identity.request_deletion` | — |
| `identity.ban` | POST | `/gm/v1/players/{playerId}/ban` | Gm | GmApi | identity | `identity.ban` | — |
| `identity.unban` | POST | `/gm/v1/players/{playerId}/unban` | Gm | GmApi | identity | `identity.unban` | — |
| `identity.cancel_deletion` | POST | `/gm/v1/players/{playerId}/deletion/cancel` | Gm | GmApi | identity | `identity.cancel_deletion` | — |
| `iap.purchase` | POST | `/api/v1/iap/purchase` | Player | PlayerApi | iap | `iap.purchase` | — |
| `economy.adjust` | POST | `/gm/v1/players/{playerId}/resources/adjust` | Gm | GmApi | economy | `economy.adjust` | — |
| `economy.ledger` | GET | `/gm/v1/players/{playerId}/ledger` | Gm | GmApi | economy | — | — |
| `cheat.grant` | POST | `/api/v1/cheat/grant` | Dev | PlayerApi | economy.cheat | `cheat.grant` | — |
| `gm.auth_csrf` | GET | `/gm/v1/auth/csrf` | Public | GmApi | gm | — | — |
| `gm.auth_login` | POST | `/gm/v1/auth/login` | Public | GmApi | gm | — | development_tools |
| `gm.auth_logout` | POST | `/gm/v1/auth/logout` | Gm | GmApi | gm | — | — |
| `gm.sso_start` | GET | `/gm/v1/auth/sso/start` | Public | GmApi | gm | — | gm.sso |
| `gm.sso_callback` | GET | `/gm/v1/auth/sso/callback` | Public | GmApi | gm | — | gm.sso |
| `gm.me` | GET | `/gm/v1/me` | Gm | GmApi | gm | — | — |
| `gm.ui` | GET | `/gm/v1/ui` | Public | GmApi | gm | — | — |
| `gm.health` | GET | `/gm/v1/health` | Gm | GmApi | gm | — | — |
| `gm.ops_version` | GET | `/gm/v1/ops/version` | Gm | GmApi | gm | — | — |
| `gm.ops_rotate_keys` | POST | `/gm/v1/ops/dataprotection/rotate` | Gm | GmApi | gm | — | — |
| `gm.ops_gate` | GET | `/gm/v1/ops/gate` | Gm | GmApi | gm | — | — |
| `gm.ops_gate_maintenance` | PUT | `/gm/v1/ops/gate/maintenance` | Gm | GmApi | gm | — | — |
| `gm.ops_gate_min_client_version` | PUT | `/gm/v1/ops/gate/min-client-version` | Gm | GmApi | gm | — | — |
| `gm.player` | GET | `/gm/v1/players/{playerId}` | Gm | GmApi | gm | — | — |
| `gm.player_by_mapping` | GET | `/gm/v1/players/by-mapping/{mappingKey}` | Gm | GmApi | gm | — | — |
| `gm.player_search` | GET | `/gm/v1/players/search` | Gm | GmApi | gm | — | — |
| `gm.player_receipts` | GET | `/gm/v1/players/{playerId}/receipts` | Gm | GmApi | gm | — | — |
| `gm.player_audit` | GET | `/gm/v1/players/{playerId}/audit` | Gm | GmApi | gm | — | — |
| `gm.receipt` | GET | `/gm/v1/receipts/{receiptId}` | Gm | GmApi | gm | — | — |
| `gm.staff_list` | GET | `/gm/v1/staff` | Gm | GmApi | gm | — | — |
| `gm.staff_create` | POST | `/gm/v1/staff` | Gm | GmApi | gm | — | — |
| `gm.staff` | GET | `/gm/v1/staff/{staffId}` | Gm | GmApi | gm | — | — |
| `gm.staff_audit` | GET | `/gm/v1/staff/{staffId}/audit` | Gm | GmApi | gm | — | — |
| `gm.staff_grant` | PUT | `/gm/v1/staff/{staffId}/grant` | Gm | GmApi | gm | — | — |
| `gm.staff_disable` | POST | `/gm/v1/staff/{staffId}/disable` | Gm | GmApi | gm | — | — |
| `gm.staff_enable` | POST | `/gm/v1/staff/{staffId}/enable` | Gm | GmApi | gm | — | — |
| `gm.staff_revoke_sessions` | POST | `/gm/v1/staff/{staffId}/sessions/revoke` | Gm | GmApi | gm | — | — |

GM permissions: each declaration carries the permission its endpoint passes to `AsGmEndpoint` today (`GmPermissions.*`,
`IdentityPermissions.PlayerBan`, `EconomyPermissions.*`); the implementer copies them from the mapping code, and the check
proves they agree. If the code differs from the table above in a method (verify per file while migrating), the code wins
and the table in PCR is corrected.

### 1.4 Sample, template and fixture declarations

| Name | Method | Template | Profile | Surface | Owner | Operation |
| --- | --- | --- | --- | --- | --- | --- |
| `battle.start` | POST | `/api/v1/battle/start` | Player | PlayerApi | sample.game | `battle.start` |
| `battle.end` | POST | `/api/v1/battle/end` | Player | PlayerApi | sample.game | `battle.end` |
| `shop.buy` | POST | `/api/v1/shop/buy` | Player | PlayerApi | sample.game | `shop.buy` |
| `progression.set` | POST | `/gm/v1/players/{playerId}/progression` | Gm | GmApi | sample.game | `progression.set` |
| `counter.increment` | POST | `/api/v1/counter/increment` | Player | PlayerApi | sample.counter | `counter.increment` |
| `counter.get` | POST | `/api/v1/counter/get` | Player | PlayerApi | sample.counter | — |
| `counter.gm_read` | GET | `/gm/v1/counter/{playerId}` | Gm | GmApi | sample.counter | — |
| `template.earn` | POST | `/api/v1/template/earn` | Player | PlayerApi | template.module | `template.earn` |
| `fixture.save` | POST | `/api/v1/fixture/save` | Player | PlayerApi | fixture | `fixture.save` |
| `fixture.card` | POST | `/api/v1/fixture/card` | Player | PlayerApi | fixture | — (query returning the `card` section message) |

### 1.5 Sections

| Id | Owner | Message | Hidden | May stay default |
| --- | --- | --- | --- | --- |
| `wallet` | economy | `gameserver.economy.v1.WalletState` | — | — |
| `inventory` | economy | `gameserver.economy.v1.InventoryState` | — | — |
| `profile` | identity | `gameserver.identity.v1.ProfileState` | as today's mapper (only fields it maps; others declared hidden after the conformance run) | — |
| `progression`, `battle` | sample.game | `samplegame.v1.ProgressionState`, `samplegame.v1.BattleState` | per conformance run | — |
| `counter` | sample.counter | `sample.counter.v1.CounterState` | per conformance run | — |
| `template` | template.module | `TemplateState` | — | — |
| `card` | fixture | `fixture.v1.CardState` | `InternalNote` | — |

The hidden lists of existing framework/sample aggregates are whatever the first conformance run shows as intentionally
not sent; each becomes an explicit `Hidden(...)` and a PCR line (none is expected to change the wire).

## 2. Startup report lines

One line per fault, prefixed like the existing validators; every line names the fix. `{label}` = `METHODS /template`.

### 2.1 Declarations (`WireCatalog.Validate`, also in `--provision` and export)

| Fault | Line |
| --- | --- |
| Bad name | `[route {name}] (owner {owner}) name does not match ^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$ (≤ 64). Fix: use <area>.<route> in lower case (FG-026).` |
| Duplicate name | `[route {name}] is declared by {ownerA} and {ownerB}. Fix: give each route a unique name (FG-026).` |
| Duplicate method + template | `[route {method} {template}] is declared twice: {nameA} ({ownerA}) and {nameB} ({ownerB}). Fix: declare it once (ARC-07).` |
| Invalid template | `[route {name}] template '{template}' is not a valid route template: {parser message}. Fix: correct the declaration (FG-026).` |
| No version | `[route {name}] template '{template}' has no version segment. Fix: prefix with /v{n}/ (API-02).` |
| Gm without permission / non-Gm with permission | `[route {name}] has profile {profile} and permission '{permission}'. Fix: GM routes declare their permission, other routes none (SEC-07).` |
| Unknown condition | `[route {name}] uses hosting condition '{condition}' which is not registered. Fix: services.AddRouteCondition("{condition}", …) in the module (FG-026).` |
| `PublicEndpoints` vs profile (BC-5) | `[module {module}] lists {template} in PublicEndpoints but route {name} declares profile {profile}. Fix: declare it Public or remove it from PublicEndpoints (SEC-09).` |
| Duplicate mapper (BC-4) | `[aggregate {id}] has {n} wire mappers. Fix: register one mapper per aggregate; replace a framework mapper with ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).` |
| Unknown hidden member | `[aggregate {id}] hidden member '{path}' does not exist on {TState}. Fix: use C# member names joined by '.' (FG-027).` |
| Unknown may-stay-default field | `[aggregate {id}] field '{path}' does not exist on {message full name}. Fix: use proto field names joined by '.' (FG-027).` |
| Untyped mapper (Warning, not error) | `[aggregate {id}] wire mapper registered without its message type: the catalog has no message for this section and Unity gets no parser. Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028).` |

### 2.2 Endpoints (`EndpointCatalogValidator`)

| Fault | Line |
| --- | --- |
| Not declared | `[endpoint {label}] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).` |
| Template differs (metadata-bound) | `[route {name}] declared at {declared template} but mapped at {mapped template}. Fix: map it with the declaration and no route group prefix, or correct the declaration (FG-026).` |
| Method differs | `[endpoint {label}] maps method {method} but route {name} declares {declared methods}. Fix: declare one route per method or correct the mapping (FG-026).` |
| Profile differs | `[endpoint {label}] has profile {mapped} but route {name} declares {declared}. Fix: use the As*Endpoint helper of the declared profile or correct the declaration (SEC-08).` |
| Operation differs | `[endpoint {label}] runs command '{mapped}' but route {name} declares '{declared}'. Fix: map it with MapPlayerCommand/MapDevCommand/MapGmCommand(declaration, …) (ARC-07).` |
| Permission differs | `[endpoint {label}] requires permission '{mapped}' but route {name} declares '{declared}'. Fix: map it with the declaration (SEC-07).` |
| Declared, not mapped | `[route {name}] ({label}, owner {owner}, surface {surface}) is declared but not mapped. Fix: map it in the module's Map{Surface} method or remove the declaration (FG-026).` |

Late-mapped endpoints get exactly these lines (and the existing security/command lines); nothing marks them as late.

## 3. Catalog export (`--export-wire-contract`, format `gameserver.wire-contract/1`)

UTF-8 without BOM, LF, 2-space indentation, properties in the order below, nulls and empty arrays omitted, routes ordered
by `name`, sections by `id` (ordinal). Example in [generated-files.md §1](contracts/generated-files.md#1-catalog-export).

| Property | Type | Meaning |
| --- | --- | --- |
| `format` | string | `gameserver.wire-contract/1`; readers fail on another major, ignore unknown properties |
| `frameworkVersion` | string | `GameServerVersion.Framework` (e.g. `0.7.0`) |
| `protobufVersion` | string | informational version of the `Google.Protobuf` assembly loaded by the host |
| `routes[]` | object | `name`, `methods[]` (omitted = any), `template`, `profile`, `surface`, `owner`, `origin` (`framework`/`game`), `operation?`, `permission?`, `condition?`, `serviceOnly?` (true only) |
| `sections[]` | object | `id`, `owner`, `origin`, `message?`, `hiddenMembers[]?`, `mayStayDefault[]?` |

No timestamp, path, machine, user, game id or environment.

### 3.1 Generator selection

- Routes: `origin == config.origin` ∧ `surface ∈ {PlayerApi, Host}` ∧ `profile ∈ {Player, Public, Dev}` ∧ `!serviceOnly`.
- Sections: `origin == config.origin`.
- Messages: a selected section's `message` must be a message of the consumer protos (game) or the framework protos
  (framework, or a game section reusing a framework message); untyped sections → id only.

## 4. Generator configuration (`wiregen.json`, format `gameserver.wiregen/1`)

Paths relative to the folder of the config file; mixed separators accepted.

| Property | Required | Default | Meaning |
| --- | --- | --- | --- |
| `format` | yes | — | `gameserver.wiregen/1` |
| `catalog.hostProject` | one of | — | host `.csproj` (or folder) run with `--export-wire-contract` |
| `catalog.environment` | no | `Local` | flavor passed as `--environment` |
| `catalog.file` | one of | — | a committed export instead of running the host |
| `origin` | no | `game` | `game` or `framework` |
| `protos[]` | yes | — | `{ "root": "<dir>", "files": ["<path relative to root>", …] }`; root = protoc import root of those files |
| `importRoots[]` | no | `[]` | extra `-I` roots of the consumer (framework roots are always added) |
| `output.root` | no | config folder | must exist (e.g. the Unity client root) |
| `output.marker` | no | — | path relative to `output.root` that must exist (client-root guard) |
| `output.directory` | yes | — | relative to `output.root`; must exist; never created |
| `output.layout` | no | `flat` | `flat` (file name only) or `by-root` (proto's folder under its root, e.g. `gameserver/core/v1/Core.cs`) |
| `output.lineEndings` | no | `lf` | `lf` or `crlf` |
| `namespace` | yes | — | C# namespace of the route and section files |
| `routesClass` | no | `GameRoutes` | class name of the route file (`<routesClass>.cs`) |
| `sectionsClass` | no | `GameSections` | class name of the section file (`<sectionsClass>.cs`) |

Committed configs:

| File | origin | Catalog | Protos | Output | Layout / EOL | Namespace / classes |
| --- | --- | --- | --- | --- | --- | --- |
| `sdk/GameServer.Client/wiregen.json` | framework | `../../samples/SampleHost` | `proto: gameserver/core/v1/core.proto`; identity, economy, iap Protos | `../unity/com.gameserver.client/Runtime/Generated` | by-root / lf | `GameServer.Client` / `FrameworkRoutes`, `FrameworkSections` |
| `samples/SampleGame/wiregen.json` | game | `../SampleHost` | `SampleGame.Adapters/Protos: sample_game.proto`; `../../modules/Sample.Counter/Sample.Counter.Adapters/Protos: sample_counter.proto` | root `KestrelDemo`, marker `Assets/SampleGame`, dir `Assets/SampleGame/Generated` | flat / lf | `SampleGame.Client` / `SampleGameRoutes`, `SampleGameSections` |
| `tests/ConsumerFixture/tools/wiregen.json` | game | `../src/Fixture.Host` | `../src/Fixture.Game/Protos: fixture_game.proto` | root `../client`, marker `Assets`, dir `Assets/Generated` | flat / lf | `Fixture.Client` / `FixtureRoutes`, `FixtureSections` |
| zh-pvp (game repo, illustrative) | game | `../../src/ZhPvp.Host` | `../../src/ZhPvp.Game.Adapters/Protos: zhpvp_game.proto` | root `../../../research-and-development-games/src/GameClient` (override `--output-root`), marker `Assets/MadProject/Scripts`, dir `Assets/MadProject/Scripts/Online/Generated` | flat / crlf | game's choice |

## 5. Generated names

Rule: split the route name or section id on `.` and `_`, upper-case the first letter of each part, join. Member sets per
file must be unique (route `X`, `XMethod`; section `X`, `XId`, `Table`), otherwise generation fails naming both sources.

### 5.1 SDK route constants (FR-033)

| `GameServerClient` constant (kept) | Value (unchanged) | Defined as |
| --- | --- | --- |
| `LoginRoute` | `api/v1/identity/login/guest` | `FrameworkRoutes.IdentityLoginGuest` |
| `ProviderLoginRoute` | `api/v1/identity/login/provider` | `FrameworkRoutes.IdentityLoginProvider` |
| `LinkRoute` | `api/v1/identity/link` | `FrameworkRoutes.IdentityLink` |
| `LogoutRoute` | `api/v1/identity/logout` | `FrameworkRoutes.IdentityLogout` |
| `DeleteAccountRoute` | `api/v1/identity/delete-account` | `FrameworkRoutes.IdentityRequestDeletion` |
| `SnapshotRoute` | `api/v1/player/snapshot` | `FrameworkRoutes.PlatformSnapshot` |
| `ConfigRoute` | `api/v1/config/get` | `FrameworkRoutes.PlatformConfig` |
| `TimeRoute` | `v1/time` | `FrameworkRoutes.PlatformTime` |

Also generated (no SDK method): `IapPurchase`, `CheatGrant`, `IdentitySetDisplayName`, `PlatformReceipt(string
receiptId)`, `PlatformHealthLive` (method `*`), `PlatformHealthReady`.

### 5.2 Framework sections

`FrameworkSections.Wallet` / `WalletId = "wallet"`, `Inventory` / `InventoryId`, `Profile` / `ProfileId`, `Table`.

## 6. Request outcome and event (FR-015)

| Member / field | Value |
| --- | --- |
| `IRequestOutcomeFeature.RouteName` | name of the matched catalog entry; null for unmatched paths and undeclared endpoints (cannot exist after startup) |
| Request event property `RouteName` | same value; added after `Route` (013 fields unchanged); omitted from the console line |

## 7. Conformance (`WireConformance`)

### 7.1 Member tree

Built from `GameServerJson.Options.GetTypeInfo(TState)`: a member = `JsonPropertyInfo` with `Set != null` or an
`AssociatedParameter`; path segment = C# member name (from `AttributeProvider`); JSON name used to build documents.
Node kinds: scalar leaf, object (recurse; depth limit 8, cycle → `MemberNotFillable`), nullable (wrapper of the inner
kind), collection (`IEnumerable<T>` other than string/dictionary: element node, transparent in the path), dictionary
(`IDictionary<K,V>`/`IReadOnlyDictionary<K,V>`: key leaf attributed to the dictionary path, value node transparent).

### 7.2 Sample and perturbation values (each leaf gets a distinct ordinal `n`, 1-based, in tree order)

| Leaf type | Sample | Perturbation (≥ 1 wire unit) |
| --- | --- | --- |
| integer types | `n` (wrapped into the type's range) | `+1` |
| `float`, `double`, `decimal` | `n + 0.5` | `+1` |
| `bool` | `true` | `false` |
| `string` | `"s{n}"` | `+ "x"` |
| enum | first defined value ≠ 0 (if only 0 exists: unsupported) | next defined value, wrapping to another non-zero value when possible |
| `DateTimeOffset`, `DateTime` (UTC) | `2026-01-02T03:04:05.006Z + n minutes` | `+1 s` |
| `TimeSpan` | `n s` | `+1 s` |
| `Guid` | deterministic from `n` | another deterministic value |
| `byte[]` | `[n]` | `[n, 1]` |
| nullable `T?` | sample of `T` | perturbation of `T` |
| collection | one element (sample of the element) | element leaves perturbed inside that element |
| dictionary | one entry: key sample (string `"k{n}"`, integer `n`, enum, `Guid`), value sample | key: perturbed key; value leaves perturbed inside the entry |
| anything else (`object`, `JsonNode`, interfaces without a known collection shape) | unsupported → `MemberNotFillable` unless `WithMemberValue` | — |

Values are written into JSON through `JsonSerializer.SerializeToNode(value, GameServerJson.Options)`, so converters
(camelCase enums, dates) apply. `WithMemberValue(aggregate, path, value)` replaces the sample of that member (its
perturbation is then skipped, reported in `CheckedAggregates` notes); `WithSampleState(aggregate, state)` replaces the whole
sample (perturbations are applied to its serialized JSON).

### 7.3 Proto "default" (FR-061)

| Field kind | Default when |
| --- | --- |
| singular scalar/string/bytes without presence | equals the proto3 default (0, false, "", empty) |
| field with presence (`optional`, message) | not present |
| enum | value 0 |
| repeated | empty, or every element default (messages recursively) |
| map | empty, or every value default |
| oneof | no case set (fields of other cases are not reported) |

Paths: proto field names joined by `.`, elements transparent (`slots.unlock_end_utc_ms`).

### 7.4 Findings

`WireConformanceProblem`: `FieldAtDefault`, `MemberNotOnWire`, `HiddenMemberOnWire`, `MemberNotFillable`,
`SampleNotSerializable`, `UntypedMapper`, `NoMapper`. Report text: one line per finding,
`[aggregate {id}] {problem}: {path} — {fix}`; `ThrowIfFailed()` throws `WireConformanceException` carrying every line.
Mutation set (FR-064) maps to: unmapped member → `MemberNotOnWire`; unset proto field → `FieldAtDefault`; nested unmapped
member → `MemberNotOnWire` (`ActiveBattle.Seed`); stale hidden member → `HiddenMemberOnWire`; hidden member reaching the
wire → `HiddenMemberOnWire`; empty mapped collection → `FieldAtDefault` + `MemberNotOnWire` (element leaves); unknown hidden
path → startup line §2.1 (and `SampleNotSerializable` is not involved).
