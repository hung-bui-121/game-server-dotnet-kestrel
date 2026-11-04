# Error codes, log lines, metrics and startup report lines — 016 login-command

The texts are contracts. Tests compare them exactly, except `{placeholders}`.

## 1. Core error codes (catalog, PCR platform §8)

| # | Name | HTTP | Retryable | Safe message | Details | Where |
| --- | --- | --- | --- | --- | --- | --- |
| 23 | `COMMAND_OUTPUT_TOO_LARGE` | 200 | no | The command result is too large. | `part` (`result`\|`receipt`), `bytes`, `limit` | command response (Player, GM, Dev); creation → logged, login answers `INTERNAL` |
| 24 | `AGGREGATE_VERSION_AHEAD` | 503 | yes (1000 ms) | Player data was written by a newer server version. Retry later. | `aggregate` | envelope: command touching/reading it, broken profile; listed in `unavailable_sections` elsewhere |
| 25 | `AGGREGATE_UNREADABLE` | 500 | no | Player data could not be read. | `aggregate` | as above |

SDK `ErrorCodes` gains the three names.

## 2. Log lines

| Event | Category, level | Template |
| --- | --- | --- |
| read failure, version ahead | `GameServer.Aggregates`, Warning | `Aggregate {AggregateId} of player {PlayerId} is unavailable: version_ahead (stored schemaVersion {Stored}, code {Code}). Deploy code that reads it before writing it (PCR platform §6).` |
| read failure, other reasons | `GameServer.Aggregates`, Error | `Aggregate {AggregateId} of player {PlayerId} is unavailable: {Reason} ({ExceptionType}).` |
| output too large | `GameServer.Commands`, Error | `Command {Operation} refused with COMMAND_OUTPUT_TOO_LARGE: {Part} is {Bytes} bytes, limit {Limit}. Fix: return less or raise GameServer:Commands:{SettingName} (PCR platform §22).` |
| account created | `GameServer.Accounts`, Information | `Account {PlayerId} created: {InitializerCount} initializers, receipt {ReceiptId}.` (`none` when no receipt) |
| initializer rejected | `GameServer.Accounts`, Error | `Account creation of player {PlayerId} failed: initializer {Initializer} (module {Module}) returned {Code}. Nothing was created; the next login retries.` |
| initializer threw | `GameServer.Accounts`, Error | `Account creation of player {PlayerId} failed: initializer {Initializer} (module {Module}) threw {ExceptionType}. Nothing was created; the next login retries.` |
| creation rule violated | `GameServer.Accounts`, Error | `Account creation of player {PlayerId} failed: {RuleLine} Nothing was created; the next login retries.` |

Rules:
- No line carries state content or an exception message from game code (API-04, constitution VII).
- The request id comes from the correlation scope.
- `ExceptionMiddleware` writes no second line for a read failure or a failed account creation (the reported-failure
  marker, research R5; T082 m1). The request log still records the code (`INTERNAL` for a failed creation). A store failure
  of the creation transaction is not marked: it keeps the middleware line `Request failed with INTERNAL: Login transaction
  failed for player {id}.`
- `account_creations_total{result="failed"}` also counts a creation that lost its anchor or met an unreadable initializer
  aggregate (T082 m5).
- Log volume: one line per failed read. For a hot player polling the snapshot, that is one line per poll (spec edge
  case); ops watch the metric.

## 3. Metrics

| Name | Type | Labels |
| --- | --- | --- |
| `aggregate_read_failures_total` | counter | `aggregateId`, `reason` |
| `command_output_too_large_total` | counter | `operation`, `part` |
| `account_creations_total` | counter | `result` = `created` \| `failed` |
| `account_initializer_failures_total` | counter | `initializer`, `result` = `rejected` \| `threw` \| `rule` |

`command_results_total{code}` and `http_errors_total{code}` show the new codes without change. The request log records
them through `IRequestOutcomeFeature`.

## 4. Startup report lines

One line per fault, in the aggregated report (011 FR-009), in serve and provision mode. The order follows
`ModuleRegistry.Validate`: modules → aggregates → commands → **account initializers** → **command settings** → the
existing later blocks.

### 4.1 Account initializers (FR-015, BC-9)

```text
[module {m}] module name '{m}' cannot prefix an account initializer. Fix: start the module name with a lowercase segment.
[module {m}] account initializer '{name}' has an invalid name. Fix: use '{prefix}<name>' in lowercase.
[module {m}] account initializer '{name}' is already registered by module '{other}'. Fix: initializer names must be unique per game.
[module {m}] account initializer '{name}' touches unknown aggregate '{id}'. Fix: register the aggregate or fix Touches.
[module {m}] account initializer '{name}' declares aggregate '{id}' twice. Fix: list each aggregate once in Touches.
[module {m}] account initializer '{name}' touches 'profile'. Fix: remove it; the framework writes the profile of a new account.
[module {m}] account initializer '{name}' allows {n} claim markers. Fix: use 0..2 (DAT-04).
[deployment] account creation touches {count} aggregates (profile + {list} from initializers {names}); max is 4. Fix: move state into fewer aggregates or seed it in a later command (DAT-09).
[deployment] account initializers reserve {sum} claim markers ({name}: {n}, …); max is 2. Fix: reserve fewer claims (DAT-04).
[module {m}] command 'identity.create_account' uses the operation reserved for account creation. Fix: rename the command.
```

A valid name matches `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$` and starts with `{prefix}` = the first segment of the module name
and a dot (`sample.game` → `sample.`, `fixture` → `fixture.`; T082 m10). When that first segment is no valid segment itself
(`^[a-z][a-z0-9_]*$` fails, e.g. `Sample.Game`), the module gets the first line once, the prefix check is skipped and only the
pattern applies to its initializers (`{prefix}` is then written `<module>.`; T082 round 2 m4).

### 4.2 Command settings (FR-032, BC-2)

```text
[deployment] GameServer:Commands:ReplayWindowHours is {v}; allowed 1..168 (CMD-07).
[deployment] GameServer:Commands:PerPlayerQueueLimit is {v}; allowed 0..16 (CMD-06).
[deployment] GameServer:Commands:ReceiptMaxBytes is {v}; allowed 1112..65536 (CMD-03).
[deployment] GameServer:Commands:ResultCompactMaxBytes is {v}; allowed 64..16384 (CMD-03).
[deployment] GameServer:Commands:ResultCompactMaxBytes {r} needs {need} receipt bytes (base64 + 1024) but ReceiptMaxBytes is {v}. Fix: raise ReceiptMaxBytes or lower ResultCompactMaxBytes.
[deployment] GameServer:Commands:MaxAggregatesPerGame is {v}; allowed 1..16 (DAT-09).
```

The lower bound 1112 is the cross rule for the smallest result: `4·⌈64/3⌉ + 1024` (T082 m2); a value below it gets the range
line and the cross line. The existing line `[deployment] {n} player aggregates registered (max {MaxAggregatesPerGame})` stays. When
`MaxAggregatesPerGame` is out of range, only the range line is written: the count check uses 16.

### 4.3 Sample game rule (FR-050)

```text
[module sample.game] config account.json: startingGold must be an integer from 0 to {maxQuantity} (the maxQuantity of gold in resources.json); found {value}.
```

## 5. TestKit lines

`CommandContextDouble.CheckCommit(outcome, encodeResult)` returns the runner's texts:

```text
Command '{operation}' result is {bytes} bytes; ResultCompactMaxBytes is {limit}: the runner rejects the command with COMMAND_OUTPUT_TOO_LARGE (part result) and nothing commits. Fix: return less or raise GameServer:Commands:ResultCompactMaxBytes.
Command '{operation}' receipt would be {bytes} bytes; ReceiptMaxBytes is {limit}: the runner rejects the command with COMMAND_OUTPUT_TOO_LARGE (part receipt) and nothing commits. Fix: fewer ledger/outbox entries or raise GameServer:Commands:ReceiptMaxBytes.
```

`CheckAccountCreation()` returns the account writer's texts. A creation is no command rejection (D15), so its receipt line
(T082, the T078 finding) and its aggregate-size line (T082 round 2, `CommitRules.CheckCreationAggregateSize`) name what the
login answers; the writer logs the same text as `{RuleLine}` of "creation rule violated" (§2):

```text
Command 'identity.create_account (initializer {names})' receipt would be {bytes} bytes; ReceiptMaxBytes is {limit}: the account writer refuses the creation and the login answers INTERNAL. Fix: fewer ledger/outbox entries or raise GameServer:Commands:ReceiptMaxBytes.
Command 'identity.create_account (initializer {name})' would store aggregate '{aggregateId}' as {bytes} bytes; its HardBytes is {hardBytes}: the account writer refuses the creation and the login answers INTERNAL. Fix: keep the state smaller or split it into another aggregate.
```

For an initializer, `'{operation}'` is `identity.create_account (initializer {name})` (the writer's receipt line names every
initializer, comma-separated; the aggregate line names the initializer that set the aggregate last). The size is measured
with the runner's envelope (`CommitRules.CheckAggregateSize`'s), so both lines keep the command's numbers.
