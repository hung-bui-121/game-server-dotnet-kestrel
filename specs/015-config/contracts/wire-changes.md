# Wire changes — 015 config

Player API (Protobuf) and HTTP behaviour of `POST /api/v1/config/get` (`platform.config`). The GM API (JSON) is
unchanged. Routes, declaration names, security profiles and the export format (014) are unchanged. Every change below is
additive under API-03: no field is renamed, renumbered, removed or given a new meaning.

## 1. Proto fields

`proto/gameserver/core/v1/core.proto`:

```proto
message ErrorEnvelope {
  // fields 1..9 unchanged
  string client_config_version = 10;   // client config version (015); config_version (8) stays the server version
}

message CommandResponse {
  // fields 1..8 unchanged
  string client_config_version = 9;
}

message SnapshotResponse {
  // fields 1..3 unchanged
  string client_config_version = 4;
}

message GetConfigRequest {
  string config_version = 1;   // unchanged: the version the client holds (client or server version); empty = none
}

message GetConfigResponse {
  string config_version = 1;          // server config version (unchanged meaning)
  bool not_modified = 2;
  repeated ConfigFile files = 3;
  int64 server_time_utc_ms = 4;
  string client_config_version = 5;   // the version the files belong to; the SDK caches them under it
}
```

`src/GameServer.Modules.Identity.Adapters/Protos/identity.proto`:

```proto
message LoginResponse {
  // fields 1..6 unchanged
  string client_config_version = 7;
}
```

`ReceiptView` is unchanged. Its `config_version` is the stored server version of a past command.

Generated code: the SDK package (`Runtime/Generated/gameserver/core/v1/Core.cs`, `Runtime/Generated/Identity.cs`) and the
server's protoc output gain one property per field. WireGen `verify` on the SDK config must pass after regeneration. The
sample and fixture outputs do not contain these messages.

## 2. Values per response

| Response | `config_version` | `client_config_version` |
| --- | --- | --- |
| `LoginResponse` (guest, provider, link) | server version of this instance | client version of this instance |
| `SnapshotResponse` | server | client |
| `CommandResponse` OK / business rejection / replay | server of the **instance** (as today, `CommandExecution`) | client of the instance |
| `ErrorEnvelope` (every Player error, middleware ones included) | server | client |
| `GetConfigResponse` full | server | client (= the version of `files`) |
| `GetConfigResponse` `not_modified` | server | client |
| `ReceiptView` | stored server version (receipt) | — |
| GM command responses (JSON `configVersion`) | server | — |

## 3. `GetConfig` comparison

```text
not_modified = request.config_version == client_version   (ordinal)
            || request.config_version == server_version   (ordinal; SDKs before 0.7.0 hold the server version)
empty request version → full bundle
any other value (older server version, older client version, v0.6.0-format value, garbage) → full bundle
```

Both versions are hashes of this instance's effective config. A client holding either one holds exactly the current
client-visible files.

## 4. Body bytes

Uncompressed full response = `GetConfigResponse{1, 3, 4, 5}` serialized in field order. Without the new field 5 it is
byte-identical to today's response for the same values (field 5 is appended last). `files` is sorted by name (ordinal),
as today. The request-time 413 (`PAYLOAD_TOO_LARGE`) applies when the uncompressed length > `Limits.ConfigBundleBytes`.
With the startup check (FR-053) it can no longer happen for a host that started.

## 5. Compression (`GetConfig` only)

| Request `Accept-Encoding` | Response with files ≥ 1024 B |
| --- | --- |
| absent | identity |
| `identity` | identity |
| `gzip` | `Content-Encoding: gzip` |
| `br` | `Content-Encoding: br` |
| `gzip, br` / `br, gzip` (no q) | `br` (tie → br) |
| `br;q=0.5, gzip` | `gzip` (higher q) |
| `gzip;q=0` / `br;q=0, gzip;q=0` | identity |
| `*` | `br` |
| `*;q=0.1, gzip;q=0.5` | `gzip` |
| `deflate` | identity (not offered) |
| unparseable | identity |

- `Vary: Accept-Encoding` on every `GetConfig` response while `GameServer:ConfigCompression=true` (compressed or not).
- `not_modified` and bodies under 1024 B are never compressed. A compressed result that is not smaller is sent
  uncompressed.
- The decoded body is byte-identical to the uncompressed body.
- `Content-Length` = bytes on the wire.
- `GameServer:ConfigCompression=false`: no `Content-Encoding` and no `Vary`, today's headers.
- No other route (player, GM, host, health, time, login, receipt, snapshot, commands, error responses) ever sends
  `Content-Encoding`, whatever the request asks (BREACH: login/session responses carry secrets).
- Request bodies are never decompressed (out of scope). A request with `Content-Encoding` is processed as today.

## 6. Compatibility

| Client | Server | Behaviour |
| --- | --- | --- |
| SDK ≤ pre-015 0.7.0 | 015 | Reads `config_version` (server), ignores field 5. Caches by server version. `GetConfig` with the current server version → `not_modified`. Downloads again on server-only edits (as today) until it updates. One full download after the upgrade (BC-5 value change) |
| SDK 015 | 015 | Caches by client version: no download on server-only edits |
| SDK 015 | v0.6.0 / pre-015 | Field 5 absent → falls back to `config_version`: today's behaviour |
| Mixed rollout | — | The effective version may alternate between instances: at most one download per switch until the rollout ends (CFG-01 allows two versions during a rollout) |
| HTTP client without decompression | 015 | Sends no `Accept-Encoding` (HttpClient without `AutomaticDecompression`) → identity |
