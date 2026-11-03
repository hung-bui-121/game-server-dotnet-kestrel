# SDK persistent config cache — 015 config

Contract of `PersistentConfigCache` (`sdk/unity/com.gameserver.client/Runtime/Core/PersistentConfigCache.cs`). The
container format is semantic for clients (PCR unity-sdk): a new format is a new format version, and an unknown version
reads as empty (FR-064).

## 1. Files in the cache folder

| File | Role |
| --- | --- |
| `gameserver-config.bin` | the stored version (format v1) |
| `gameserver-config.bin.tmp` | a save in progress; read only when the main file is missing or unusable (crash recovery) |

No other file is created, read or deleted. Config file names received from the server are strings inside the container
and never become paths (FR-063).

## 2. Container format v1 (little-endian)

| Offset | Size | Field | Rule |
| --- | --- | --- | --- |
| 0 | 4 | magic `47 53 43 43` ("GSCC") | else "not a config cache file" |
| 4 | 2 | format version = `1` | other → "unknown format version {n}" |
| 6 | 2 | reserved = `0` | ignored on read |
| 8 | 4 | payload length `P` | `8 + 4 + P + 32` must equal the file length, else "truncated" / "trailing bytes" |
| 12 | P | payload | below |
| 12 + P | 32 | SHA-256 of the payload | mismatch → "integrity check failed" |

Payload:

| Field | Encoding |
| --- | --- |
| version | `u32 length` + UTF-8 bytes (1..256 bytes) |
| file count `N` | `u32` |
| N × file | `u32 nameLength` + UTF-8 name (1..1024 bytes), `u32 contentLength` + bytes |

Writers sort files by name (ordinal). Readers check: every length ≤ remaining payload bytes (no allocation larger than
the file), valid UTF-8 (strict decoder), names unique and non-empty, `N ≤ 100000`. Any failure → empty cache.

## 3. Load (constructor)

```text
create folder if missing (failure → empty, LoadProblem)
main := read "gameserver-config.bin"
if main is absent: if tmp is a valid container → use it (and promote it with the next Store); else empty, LoadProblem null
if main is invalid: if tmp is valid → use tmp; else empty, LoadProblem = reason(main)
never throws for I/O or format problems (IOException, UnauthorizedAccessException, format, hash)
```

Reasons (exact texts are not part of the contract, the kinds are): `truncated`, `trailing bytes`, `not a config cache
file`, `unknown format version {n}`, `integrity check failed`, `invalid UTF-8`, `duplicate file name`, `unreadable:
{exception type}`.

## 4. Store

```text
1. Version, Files := new values (in memory, immediately)          ← SDK sees them even if the disk fails
2. write tmp: open (create/truncate) → write header+payload+hash → Flush(flushToDisk: true) → close
3. replace: main exists ? File.Replace(tmp, main, null) : File.Move(tmp, main)
4. success → WriteProblem = null; any exception in 2–3 → WriteProblem = reason; tmp deleted best effort
```

| Process death during | Next start loads |
| --- | --- |
| step 2 (partial tmp) | main (old version); tmp invalid, ignored |
| between 2 and 3 | main (old version) |
| step 3 (atomic rename) | old or new, never a mix |
| a runtime whose File.Replace is delete + move (none seen: Windows .NET, Mono Editor; devices T058), between the two | tmp (new version, valid), by the load-time recovery |

`Store` with the version already stored and identical files skips the write. One version is kept.

## 5. Behaviour with `GameServerClient`

| Moment | Effect |
| --- | --- |
| App start | `new PersistentConfigCache(folder)` → `Config.Version`/`Files` before any network call |
| Login / any response carries client version = `Config.Version` | `ConfigOutdated` false, no `ConfigVersionChanged` |
| Response carries another version W | `ConfigOutdated` true, `ConfigVersionChanged(W)` once |
| `RefreshConfigAsync` | sends `Config.Version` → `not_modified` (nothing written) or files → `Store(W, files)` |
| Disk unwritable | refresh still returns `true`, the game uses W in memory, `WriteProblem` set |

## 6. Threading and cost

All I/O is synchronous on the caller's thread (the Unity main thread with the Unity transport). Budget: load and save of
1 MiB ≤ 50 ms each outside Unity (SC-006). Not thread-safe.

## 7. Tests (FR-067, outside Unity, `tests/GameServer.Client.Tests/PersistentConfigCacheTests.cs`)

- Round trip; empty bundle (version + 0 files); 1 MiB bundle (perf opt-in).
- Simulated restart: a second instance on the same folder sees version + files.
- Faults through `CacheFileOps`: throw at open-temp, at each write, at flush, at replace/move; kill simulation =
  truncate tmp at every byte offset of a small container → every case loads old or new.
- Corrupt main file: zero length, truncated at every offset of a small container, one flipped bit per region (header,
  payload, hash), unknown format version, wrong magic, trailing bytes, duplicate names, invalid UTF-8, unreadable
  (locked or permission denied) → empty, `LoadProblem` set, no exception.
- Unwritable folder (read-only attribute / ACL on Windows, mode 0555 on Unix) → `Store` does not throw, values in memory,
  `WriteProblem` set.
- Path-like names (`../x.json`, `/etc/passwd`, `C:\x`, `a/b.json`) → round trip as data; the folder holds only the two
  known file names.
- End to end with the sample host: client A logs in and refreshes (download), client B on the same folder logs in →
  `ConfigOutdated` false, `RefreshConfigAsync` → `false`, 0 files received; after a server-only edit (second host on
  edited config) still 0 downloads.
