# SDK pending-command store — 016 login-command

`PersistentPendingCommandStore` (`Runtime/Core`, C# 9, `netstandard2.1`, no UnityEngine) and its client flow. Decision:
research R11. Same file rules as the 015 config cache (`contracts/sdk-cache.md` of 015): temp file, flush to device,
rename; damaged = skipped with a reason; disk failures never thrown; names never come from server data.

## 1. Folder layout

```text
{folder}/
├── 3f2a…-….pending        one committed entry (file name = lowercase UUID D of the request id + ".pending")
├── 7b11…-….pending
└── 9c0d…-….pending.tmp    leftover of a crash during Record: deleted at load, never read
```

- One folder per server environment and per client: the guide says so, as for the config cache.
- Unity: `UnityPendingCommandStore.Create(env)` → `Application.persistentDataPath/gameserver-pending/{env}`, name rule of
  `UnityConfigCache` (`[a-z0-9._-]{1,64}`, no edge dots, no Windows device names), iOS no-backup flag on the folder.

## 2. Entry container, format v1 (little-endian)

```text
offset  size  field
0       4     magic "GSPC" (0x47 0x53 0x50 0x43)
4       2     format version = 1
6       2     reserved = 0
8       4     payload length N (bytes)
12      N     payload:
                u32 len + UTF-8   requestId     (36 bytes, lowercase UUID D; must equal the file name stem)
                u32 len + UTF-8   playerId      (1..128 bytes)
                u32 len + UTF-8   route         (1..512 bytes)
                i64               recordedAtServerUtcMs
                u32 len + bytes   body          (0..1 MiB)
12+N    32    SHA-256 of the payload
```

Readers refuse:
- a wrong magic;
- an unknown format version;
- `N` larger than the file allows;
- lengths beyond their bounds or beyond the payload;
- invalid UTF-8;
- a request id that is not a lowercase UUID D, or does not match the file name;
- trailing bytes;
- a hash mismatch.

A refused file is skipped, deleted, and named in `LoadProblem` (`"{file}: {reason}"`, joined with `; `). The format is
semantic for clients: a change is a new version, and an unknown version is skipped (PCR unity-sdk).

## 3. Algorithms

**Load (constructor).** Create the folder if missing → enumerate `*.pending.tmp` and delete them (best effort) →
enumerate `*.pending` → read and validate each → keep valid entries ordered by `recordedAtServerUtc`, then request id.
File problems while enumerating give an empty store with `LoadProblem = "unreadable: {ExceptionType}"`. The constructor
never throws for the disk.

**Record(entry).**
1. Memory first: add or replace the entry in `Entries`.
2. Serialize. When not representable (lengths), set `WriteProblem` and return.
3. `OpenTemp({id}.pending.tmp)`, write header, payload and hash, `Flush(true)`, close.
4. `Move(tmp, {id}.pending)`. If the final name exists (same id recorded again), `Replace(tmp, final)`.
5. Success clears `WriteProblem`. A file problem sets `WriteProblem = "not saved: {Type}: {Message}"` and deletes the temp
   file (best effort). Never throws.

**Remove(id).** Memory first. Then `Delete({id}.pending)` if it exists, without a flush. A file problem sets
`WriteProblem`. Never throws.

**Seam.** All file operations go through `CacheFileOps` (015) plus the new virtual `EnumerateFiles(folder, pattern)`.
Tests derive from it to fail one step.

## 4. Client flow

```text
SendCommandAsync(route, req, parser, requestId?)            (store configured)
  id   = requestId ?? NewRequestId()          (validated, lowercase)
  body = req.ToByteArray()
  store.Record(new PendingCommand(id, session.PlayerId, route, body, ServerNow))   ← before the first attempt
  try   response = SendAsync(… body, id …)                  (existing retries, same id + bytes)
        store.Remove(id)                                    ← CommandResponse with any code
  catch GameServerException e when e is an envelope error and !e.Error.Retryable
        store.Remove(id); throw                             ← final answer
  catch anything else (network, invalid response, retryable last answer, cancellation)
        keep; throw                                         ← outcome unknown

ResendPendingAsync(entry, parser)
  StatusOf(entry) must be Resendable, else InvalidOperationException
  SendAsync(entry.Route, entry.Body, entry.RequestId)       ← same bytes, same id; then the same Remove/keep rules
```

`StatusOf`:
- `OtherPlayer` when `entry.PlayerId` ≠ the session's player id, or when there is no session;
- `Expired` when `ServerNow − RecordedAtServerUtc ≥ PendingCommandMaxAge`;
- otherwise `Resendable`.

Without a store, nothing is written. With a caller id but no store, the id is used and nothing is written.

## 5. Crash table (SC-007)

| Process dies… | Next start lists | Resend gives |
| --- | --- | --- |
| before `Record` finished (temp only, or nothing) | nothing (temp deleted) | — (the request never left: nothing ran) |
| after `Record`, before the request reached the server | the entry | executed once |
| after the server committed, before the response | the entry | `replayed = true`, original receipt id |
| after the response, before `Remove` finished | the entry | replay (commit) or the same rejection again (a rejection has no receipt) |
| after `Remove` | nothing | — |

## 6. Costs and threading

- Record: one small file write + one device flush + one rename. Remove: one delete.
- SC-008 target: ≤ 10 ms p95 each for a 4 KiB body outside Unity **[VERIFY V8]**; fallback `Flush(false)`.
- Load: one directory enumeration + one read per pending entry (normally 0-3).
- Synchronous on the calling thread (the Unity main thread). Not thread-safe. One client per folder.

## 7. Tests (`GameServer.Client.Tests`)

- `PendingCommandFormatTests`: round trip; every refused shape of §2 (truncated header, payload, hash; wrong magic;
  version 2; bad UTF-8; id ≠ file name; trailing bytes; hash flip) → skipped, reason, 0 exceptions.
- `PersistentPendingCommandStoreTests`:
  - fault at every `CacheFileOps` step of Record and Remove (open, write, flush, move, replace, delete) → old or new entry
    loadable, `WriteProblem` set, no exception;
  - leftover temp deleted;
  - read-only folder (`ReadOnlyFolder` helper of 015);
  - two entries + one damaged → two loaded.
- `PendingCommandClientTests` (fake transport):
  - remove on OK, on business code, on non-retryable envelope;
  - keep on network error, `COMMIT_UNKNOWN_RETRY`, `AGGREGATE_VERSION_AHEAD` as last answer, cancellation after record;
  - record happens before the first transport call;
  - login/logout/snapshot/config/time never touch the store;
  - status `OtherPlayer` / `Expired` (server clock, a device clock change does not move it);
  - resend refused for non-resendable entries;
  - invalid caller id → `ArgumentException` with 0 transport calls and 0 records.
- `PendingCommandEndToEndTests` (sample host): the four deaths of §5 with a new client on the same folder → one ledger
  entry, replay flag and receipt id (SC-007).
- Opt-in perf (`GAMESERVER_PERF=1`): record and remove 4 KiB, 200 iterations, p95 ≤ 10 ms (SC-008).
