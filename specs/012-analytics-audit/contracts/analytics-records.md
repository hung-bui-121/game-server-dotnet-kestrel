# Contract — analytics records (JSONL), schema v1 (012)

Data-team-facing contract, published in PCR platform §18 and in `docs/guides/analytics.md`. Changing a field name, type,
meaning, time semantics, the dedupe rule or the file layout below is a breaking change (PCR-01, ARC-08) and, for a field,
increments `schemaVersion`. Adding an optional field keeps the version.

## 1. What is produced

| Event (`event`) | Fact | Delivery | Dedupe key |
| --- | --- | --- | --- |
| `payment` | a purchase verified by the store and granted by the server (`iap.purchase`) | **at-least-once** (committed with the grant, delivered by outbox relay + sweeper) | `transactionID` |
| `login` | `source = login`: a login that issued a new session (guest, provider, confirmed switch); `source = resume`: the first authenticated request of a player on a local day on one server instance | **best effort** (in-memory queue; may be lost on overload, disk error or crash — counted by `analytics_events_dropped_total`) | `eventId` (rarely needed) |

Never produced: refused or failed logins, identity link, replayed purchase requests (same request id), rejected
purchases (`ALREADY_GRANTED`, `INVALID_RECEIPT`, unknown product, economy rejection), failed store verifications.

## 2. Files

- Name: `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl` — one file per server instance, event type and **UTC** date of
  the server clock **at write time**. Parse names from the right (`event` has no `-`); `instanceId` may contain `-`.
- A file whose UTC date is before today's UTC date is complete and never appended to again.
- One JSON object per line, UTF-8, `\n`-terminated. **Skip any line that is not valid JSON** (a line cut by a crash;
  it may appear in the middle of a file after a restart). Such a line was never acknowledged: a payment in it is
  delivered again in a later line; a login in it is lost.
- **Never derive business dates from file names or file dates.** A payment may be written by another instance, later,
  and on a later UTC day than its `chargeTime` (redelivery after a failure).

## 3. Common fields (every record)

| Field | Type | Meaning |
| --- | --- | --- |
| `event` | string | `payment` or `login` |
| `schemaVersion` | int | `1` |
| `eventId` | string | 32 hex chars; identical in every copy of the same payment; unique per login/resume emission |
| `gameId` | string | game of the deployment |
| `environmentId` | string | deployment: `local`, `dev`, `beta`, `uat`, `live` (games may define others). **Filter `live` for revenue.** |
| `userID` | string | internal player id. After an account deletion is purged (≥ 30 days after the request), a payment delivered late carries the anonymous id `anon_…` instead |
| `{time}` | string | local time with offset, `2026-10-30T21:05:09.123+07:00` |
| `{time}Utc` | string | the same instant in UTC, `2026-10-30T14:05:09.123Z` |
| `timeZone` | string | IANA zone of the deployment, e.g. `Asia/Ho_Chi_Minh`, `Asia/Taipei`, `Europe/London` (`UTC` only in development/test) |
| `utcOffset` | string | offset valid **at that instant** (`+07:00`; `Europe/London` is `+00:00` in winter, `+01:00` in summer) |

Time semantics:

- `{time}` and `{time}Utc` always denote the same instant (millisecond precision, truncated).
- **`log_month` / `log_date`** = month / date of the local `{time}` string (first 7 / 10 characters). This is the
  deployment's local calendar, as the owner requested; for a UTC calendar use `{time}Utc`.
- The zone is per deployment (one game deployment = one market zone), not per player.

## 4. `payment` v1

| Field | Type | Meaning |
| --- | --- | --- |
| `chargeTime`, `chargeTimeUtc` | string | **server grant time** (commit of the purchase command), not the store purchase time. A purchase verified at 23:59:59 and granted at 00:00:01 belongs to the next day/month |
| `receiptId` | string | server receipt of the purchase (tracing) |
| `transactionID` | string | unique per granted purchase, prefixed by store: `apple:{transactionId}`, `google:{orderId}` (`GPA.…`), or `google:h:{32 hex}` (non-reversible digest of the Google purchase token when Google gives no order id: test and some promo purchases) |
| `store` | string | `google`, `apple` |
| `productId` | string | store product id; map to price with the store/catalog (price and currency are not in the record) |
| `orderId` | string, optional | store order id when the store gives one |
| `isTest` | bool | `true` = store test/sandbox purchase (Google license tester, Apple Sandbox/Xcode/LocalTesting) or a development fake purchase; always `false` on `live` (test purchases are refused there) |
| `purchaseCategory` | string, optional | Google category when reported: `test`, `promo` (promo code: a valid grant **without revenue**), `rewarded` |
| `storePurchaseTimeUtc` | string, optional | purchase time reported by the store, UTC |

**UL (unique paying users per month)**:

```sql
-- Spark SQL sketch
SELECT substr(chargeTime, 1, 7) AS log_month, COUNT(DISTINCT userID) AS ul
FROM (SELECT * FROM payment WHERE environmentId = 'live' AND NOT isTest) p
-- dedupe first: dropDuplicates(['transactionID'])
GROUP BY substr(chargeTime, 1, 7)
```

Dedupe: `dropDuplicates(["transactionID"])`. Copies of one payment are identical except possibly `userID` (a copy
delivered after the account purge carries the anonymous id) — keep the copy with the earliest file position or any copy;
`chargeTime`, `chargeTimeUtc`, `eventId`, `transactionID` and every other field are identical.

## 5. `login` v1

| Field | Type | Meaning |
| --- | --- | --- |
| `activeTime`, `activeTimeUtc` | string | server time of the login, or of the first authenticated request of the local day (resume) |
| `source` | string | `login` or `resume` (player came back with a stored session, no new login) |
| `provider` | string, `login` only | identity used: `guest`, `pgs` (Google Play Games), `gamecenter`, or a game-specific provider |
| `isNewUser` | bool | the login created the player (always `false` for `resume`) |

**Active days per player per month** (`active_days > 3`):

```sql
SELECT userID, substr(activeTime, 1, 7) AS log_month, COUNT(DISTINCT substr(activeTime, 1, 10)) AS active_days
FROM login WHERE environmentId = 'live'
GROUP BY userID, substr(activeTime, 1, 7)
```

Several `login`/`resume` records for one player on one local day are normal (several instances, restarts, re-logins);
count **distinct local dates**, never records. The resume "day" is the deployment's local day (`timeZone`).

## 6. Schema versioning

- `schemaVersion` is per event type. Adding an optional field: same version (readers must ignore unknown fields).
- Removing a field, renaming it, changing its type, format or meaning: new version, announced in PCR before release; for
  a transition period both versions may appear in the same files (filter by `schemaVersion`).

## 7. Retention and privacy (owner Q11, PRIV-01)

- `userID` is the raw internal player id (not hashed). It is not directly identifying, but it is personal data under the
  game's privacy policy, which must cover analytics files.
- Shipped analytics data is kept **25 months** (policy of the data store; the server does not enforce it). Account
  deletion does **not** rewrite files already written or shipped.
- Records never contain: session tokens, passwords, guest secrets, provider tokens or auth codes, provider subject ids,
  Google purchase tokens, receipt payloads or JWS, client IP addresses, request bodies.
