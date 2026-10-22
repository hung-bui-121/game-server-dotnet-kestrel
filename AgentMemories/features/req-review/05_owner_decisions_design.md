# Thiết kế lại theo quyết định owner (2026-10-22) — đầu vào cho v1.1 rev 3

Nguồn: `04_owner_answers.md` (8 thay đổi, 15 chấp nhận, 3 mở), `Game_Server_System_Requirements_v1.1.md` rev 2, `02_technical_fitness.md` §2–§4, `03_v11_architect_review.md` (B-01, M-01..M-06). Vai trò: system-architect. Đầu ra: văn bản yêu cầu thay thế để task-planner áp vào rev 3; không có code.

Quy ước trong tài liệu này: `[MẶC ĐỊNH — chờ owner chốt, TBD-xx]` = giá trị tôi tự chọn; `[VERIFY] Vn` = fact chưa xác minh (V15 trở đi là mới, mục §J). "pa" = player aggregate.

---

## §0. Tóm tắt quyết định thiết kế

| # | Quyết định | Một câu |
| --- | --- | --- |
| 0.1 | **Mô hình dữ liệu**: nhiều player aggregate (TBD-07, owner chốt) | Mỗi aggregate = một document trong collection riêng `pa_<aggregateId>` (key = `playerId`), có `rev` đơn riêng, ngân sách kích thước riêng; game đăng ký qua `PlayerAggregateDescriptor`; framework bắt buộc có `profile` (sessionEpoch, ban, deleted, identity projection). Section của API-10 = aggregate. |
| 0.2 | **Cơ chế commit**: Couchbase distributed transaction cho **mọi** command thay đổi trạng thái | Một command = một transaction gồm: các aggregate chạm (≤ 4) + **một receipt document** `rcpt::{receiptId}` (Insert) + claim marker nếu có. Receipt document đồng thời là idempotency entry, receipt bền vững và **outbox carrier** (ledger/audit/score). Không còn receipt ring, không còn outbox nhúng trong aggregate, không còn "tầng A/tầng B", không còn claim-marker saga với reaper. |
| 0.3 | **Mọi ghi vào `pa_*` đi qua transaction runner** (kể cả login tăng `sessionEpoch`, purge PRIV-01) | Loại bỏ hoàn toàn hazard trộn ghi transaction/không-transaction trên cùng document ([VERIFY] V15); một đường commit, một đường tra cứu, một drainer. Chi phí: single-aggregate command cũng trả phí transaction (≈ 7 KV op thay vì 1). |
| 0.4 | **Idempotency** | Tra cứu = KV get `rcpt::{receiptId}` (`receiptId` = hash(actorId, operation, requestId)) ngay trong transaction; window = **24 h** thực thi khi tra cứu (vế "64 command" bỏ vì không còn ring — ngữ nghĩa "trong window cùng kết quả, ngoài window xử lý như mới và bị invariant từ chối" giữ nguyên). Retention receipt 180 ngày (TBD-08) không đổi. |
| 0.5 | **Battle** = aggregate riêng `battle`, `activeBattle` đơn | StartBattle (1 aggregate) khi đang có battle cũ: **thay thế** (battle cũ = abandoned, không thưởng) theo policy mặc định; EndBattle = Battle + Progression + Wallet trong một transaction; hết hạn logic 24 h. |
| 0.6 | **Session** = 1 phiên/player, takeover bằng `sessionEpoch++` ở mỗi login | Cửa sổ ≤ 60 s hai phiên cùng sống (cache per instance) **chấp nhận**; mã `SESSION_REPLACED` (401, không retry, SDK không tự re-login). |
| 0.7 | **Provider** = Google Play Games Services v2 (server auth code → access token → `players/me`) + Apple Game Center (`fetchItems` signature) | Key `pgs:{playerId}` và `gamecenter:{teamPlayerId}`; Google Sign-In/Facebook ra ngoài phạm vi (game thêm adapter qua `IIdentityProviderAdapter`). |
| 0.8 | **Sample P0** = Login, StartBattle, EndBattle, IAPurchase, BuyVirtualItem (SMP-01..05) | IAP-01/02 + ECO-08 lên P0 (verify + grant idempotent, verifier giả trong CI); IAP-03 P0.5; MIG xoá; mọi tham chiếu Sniper PVP gỡ; TBD-06 Mở. |
| 0.9 | **Config = tập file** trong artifact deploy, reload = restart (rolling) | `configVersion = {label}+{hash12}` (label từ `manifest.json`, hash tính từ nội dung hiệu lực); endpoint Public `/v1/config/{configVersion}/{file}` cho client (CDN tuỳ deployment); không có `config` collection, không có GM page config, không `PublishConfig`. |
| 0.10 | **Một game/deployment** | `gameId` chỉ từ deployment config; bỏ "session claim"; multi-game hosting = ngoài phạm vi; giữ scope `g_<gameId>` và trường `gameId` trong document. |

Sơ đồ đường ghi của một command (ASCII):

```
Client ──POST /v1/cmd/{op} (requestId, bytes)──▶ Platform.Web
   │  auth (session epoch, ban) → GATE → per-player limiter (CMD-06)
   ▼
ICommandRunner.RunAsync  ── Couchbase Transactions.RunAsync(lambda, expiration 10 s)
   │ 1. ctx.GetOptional(rcpt::{receiptId})  ── có → so bodyHash → trả kết quả cũ (không ghi)
   │ 2. ctx.Get(pa_x::{playerId}) cho mọi aggregate trong Touches/Reads (absent → default, rev 0)
   │ 3. handler (thuần, không side effect ngoài ctx) → state mới, ledger/audit entries, claim keys
   │ 4. ctx.Replace/Insert aggregates (rev++), ctx.Insert claim markers, ctx.Insert receipt{outbox[], outboxPending=true}
   │ 5. commit (Majority)
   ▼
relay inline best-effort: Insert ledger/gm_audit/score projection theo entryId → MutateIn receipt.outboxPending=false
   │ thất bại → sweeper (OPS-05) quét index receipts.outboxPending = true
   ▼
Response: resultCode + SectionUpdate[] (chỉ aggregate đã đổi, full = true) + serverTimeUtc + configVersion
```

---

## §A. Mô hình nhiều aggregate (TBD-07)

### A.1. Định nghĩa aggregate

**Phân tích.** Owner yêu cầu ranh giới theo nghiệp vụ, kích thước kiểm soát, linh động theo game. Ba cách đặt document: (i) một collection `players` với key `{playerId}::{aggregateId}`; (ii) collection riêng per aggregate, key = `playerId`; (iii) một document chứa nhiều aggregate (= rev 2, bị bác). Chọn **(ii)** vì: DAT-01 đã quy module = collection và aggregate có module sở hữu; RBAC/index/backup/metric theo collection; key đơn giản (`playerId`) nên snapshot = N KV get song song với key biết trước (PERF-07); Couchbase transaction xuyên collection trong cùng scope không có chi phí thêm. Giới hạn số collection ([VERIFY] V3) không phải vấn đề với ≤ 16 aggregate/game và một game/deployment.

**Quyết định.**
- `PlayerAggregateDescriptor` đăng ký qua `ModuleDescriptor` của module sở hữu (Identity sở hữu `profile`; Progression sở hữu `progression`; Economy sở hữu `wallet`, `inventory`; `SampleGame` sở hữu `battle`). Game có thể thêm/bỏ aggregate của mình, không bỏ `profile`.
- Mỗi aggregate: collection `pa_<aggregateId>`, key `playerId`, `rev` đơn (uint64, tăng mỗi lần ghi), `schemaVersion`, upgrader chain riêng, ngân sách: cảnh báo 32 KiB, cứng 64 KiB `[MẶC ĐỊNH — chờ owner chốt, TBD-07]`; tối đa 16 aggregate/player `[MẶC ĐỊNH, TBD-07]`.
- **Document chưa tồn tại ⇒ trạng thái mặc định** (`IAggregateFactory.CreateDefault`), `rev = 0`; lần ghi đầu là Insert trong transaction. Hệ quả: tạo player lần đầu chỉ cần insert mapping `identities` (không còn chuỗi "insert aggregate → mapping → xoá orphan" của m-19); module mới thêm aggregate sau này không cần backfill.
- `profile` (framework, bắt buộc): `sessionEpoch`, `ban`, `deleted`, `displayName`, `identities[]` (projection), `createdAtUtc`. Không chứa resource.
- Section (API-10) ≡ aggregate: `sectionId = aggregateId`; `ClientVisible = false` cho aggregate không trả về client (ví dụ `battle.evidence` nếu game tách).

### A.2. Cơ chế commit cho command chạm nhiều aggregate

Thực tế quan trọng: với mô hình này, **đa số command mutation của P0 là đa document** (EndBattle 3, BuyVirtualItem 2, IAPurchase 2 + marker; chỉ StartBattle là 1) — nghĩa là cơ chế đa document chính là hot path, không phải ngoại lệ như rev 2.

| Tiêu chí | (a) Transaction cho mọi command | (b) Command document + saga/outbox (ghi intent, áp từng aggregate bằng CAS, sweeper hoàn tất) | (c) Lai: transaction khi ≤ N aggregate, saga cho job |
| --- | --- | --- | --- |
| Atomicity ECO-03/ECO-04 | Tuyệt đối: debit + credit + receipt cùng commit | **Vi phạm tạm thời**: wallet đã trừ, inventory chưa cộng; snapshot/Login ở giữa thấy trạng thái lệch; bước sau thất bại (`maxQuantity`) phải **bù** (debit ngược) → ledger nhiễu, support ticket | Như (a) cho command |
| Double-effect khi timeout / CommitAmbiguous (CMD-04) | Receipt `Insert` nằm trong transaction là mỏ neo duy nhất; retry cùng `requestId` chạy lại lambda: `ctx.Get` receipt thấy đã commit → trả kết quả cũ; nếu transaction cũ còn pending → Insert xung đột → SDK chờ/retry tới khi ATR cũ resolve (commit hoặc rollback) | Mỗi bước là một CAS có thể ambiguous riêng; cần `appliedCommands[]` trong **từng** aggregate để áp idempotent; sweeper phải re-validate | Như (a) |
| Latency (server-side) | ≈ 2(N+1)+3 KV op với Majority, ước **10–30 ms** cho N = 2–3 (02 §2.2: ×3–5 so với 1 CAS) — phải đo (ACC-16) | 1 insert intent + N CAS + 1 update intent, tuần tự ≈ N+2 op nhưng **không rẻ hơn** đáng kể vì phải tuần tự và có bước bù | Như (a) |
| Độ phức tạp code | Thấp: SDK lo ATR/staging/retry/cleanup; app chỉ cần lambda thuần | Cao: state machine intent, thứ tự bước, bù, sweeper re-validate, hai đường tra cứu | Hai đường commit, hai mô hình lỗi |
| Hazard trộn ghi | Phải tránh ghi non-transactional lên document đang staged ([VERIFY] V15) → giải bằng quy tắc 0.3 | Không có | Có (job saga + command txn cùng doc) |
| Phụ thuộc vận hành | Majority durability (đã là mặc định DAT-05), Server ≥ 7.x, lost-cleanup của SDK, collection metadata ATR ([VERIFY] V17) | Không thêm | Như (a) |

**Quyết định: (a) — transaction cho mọi command thay đổi trạng thái, kể cả command một aggregate.** Lý do thêm cho "kể cả một aggregate": receipt document tách riêng khiến ngay cả command một aggregate cũng là 2 document; giữ ring nhúng cho trường hợp đó sẽ tạo hai đường tra cứu và hazard V15 (StartBattle ghi CAS thường lên `pa_battle` trong lúc EndBattle retry đang staged cùng document). Một đường duy nhất rẻ hơn về rủi ro so với ~6 KV op tiết kiệm được ở StartBattle.

Saga (b) **không** dùng cho command; shape "intent + progress idempotent" chỉ còn ở job dài (JOB-01 lease + progress document, P1) và GM batch.

**Invariant được giữ thế nào**
- ECO-03: số dư đọc bằng `ctx.Get` trong transaction, kiểm tra và ghi trong cùng transaction; hai debit đồng thời trên hai instance → write-write conflict → SDK retry lambda với số dư mới → một bên `INSUFFICIENT_FUNDS`.
- ECO-04: mọi aggregate + receipt + claim marker commit cùng lúc; không có partial success.
- CMD-03: receipt là document trong transaction ⇒ nguyên tử với state theo định nghĩa; "không ghi receipt trước khi state được lưu" thoả.
- CMD-04: timeout/`TransactionCommitAmbiguousException` → 503 `COMMIT_UNKNOWN_RETRY`; retry cùng `requestId` → lambda mới tra receipt (transaction-aware read) → có thì trả cùng `resultCode/receiptId/newRev[]` kèm aggregate hiện hành (`full = true`); không có thì thực hiện — nhưng Insert receipt sẽ xung đột với staged insert nếu transaction cũ chưa resolve, nên không thể có hai hiệu ứng.
- CMD-02: `bodyHash` lưu trong receipt; khác hash → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` (không ghi).
- CMD-05: conflict do SDK retry nội bộ trong `TransactionExpiration` (10 s `[MẶC ĐỊNH, TBD-19]`); hết hạn không ambiguous → 409 `CONCURRENT_UPDATE` (retryable); ambiguous → CMD-04. Repository vẫn không có `Upsert`; document ngoài `pa_*`/`receipts` (sessions, identities, counters, gate, gm_staff, giftcodes) ghi CAS/Insert thường.
- Handler là hàm thuần qua `ICommandContext`: **không gọi HTTP/outbound trong lambda** (lambda có thể chạy lại). IAP verify với store chạy **trước** transaction trong adapter; transaction chỉ tiêu thụ kết quả đã verify.

### A.3. Receipt / idempotency khi không còn ring

- Document `rcpt::{receiptId}` trong `receipts`: `{receiptId, actorId (playerId hoặc gm staff id), operation, requestId, bodyHash, resultCode, resultCompact (≤ 512 B), configVersion, schemaVersions{aggregateId: v}, aggregates[{aggregateId, newRev}], claimKeys[], committedAtUtc, outbox[{entryId, kind: ledger|audit|score|event, payload}], outboxPending}`; tổng ≤ 4 KiB `[MẶC ĐỊNH, TBD-13]`; `gameId`/`environmentId`/`schemaVersion` theo DAT-10. Insert một lần trong transaction; sau khi transaction trả về chỉ `outboxPending` được sửa (MutateIn thường) — document không bao giờ là đối tượng của transaction thứ hai.
- `receiptId` = hash xác định của (`actorId`, operation, `requestId`) (mở rộng m-10 từ `playerId` sang `actorId` để GM command dùng cùng đường).
- Tra cứu khi retry: `ctx.GetOptional` theo key — O(1), không quét; entry `committedAtUtc` cũ hơn 24 h coi như không tồn tại (window, `[MẶC ĐỊNH, TBD-13]`). `GetReceipt(receiptId)` (CMD-09 b) đọc cùng document bằng KV thường.
- Receipt vừa là projection (không còn bước "relay receipt") nên chi phí relay giảm một insert so với rev 2; retention 180 ngày bằng job purge (TBD-08) giữ nguyên; ledger/audit không xoá.
- Ledger/audit: outbox entry nằm trong receipt, relay inline sau commit (Insert `ledger::{entryId}`, `gm_audit::{entryId}` idempotent) rồi MutateIn `outboxPending=false`; entry **không bị xoá** khỏi receipt (receipt là bản ghi đầy đủ cho `GetReceipt`). Sweeper quét partial index `WHERE outboxPending = true` trên `receipts` (N1QL cho job, hợp PERF-07).
- Tính "một lần" ngoài window: claim marker (`gclaim::`, `extref::`, `membership::`) Insert **trong transaction** — không còn trạng thái `pending/completed`, không reaper; `activeBattle` trong `pa_battle`.

### A.4. Ordering per player, retry, chi phí chuẩn

- CMD-06 giữ: `PartitionedRateLimiter` theo `playerId` (concurrency 1, queue 2) per instance; xuyên instance bằng transaction conflict (SDK retry). GM command lên aggregate của player đi cùng partition `playerId` của target. Command chạm aggregate của > 1 player: ngoài phạm vi P0 (transaction vẫn bảo đảm atomic, ordering chỉ theo actor).
- Chi phí chuẩn một command (ghi vào DAT-09 mới, phải đo ở ACC-16): 1 transaction với (N aggregate + 1 receipt + k marker) document ≈ 2(N+1+k)+3 KV op durability Majority; relay inline best-effort 0..n Insert ledger, 0..1 Insert `gm_audit`, 1 MutateIn receipt. So với rev 2 (1 CAS + 3–4 relay): số KV op ×2–3, latency ×3–5 (ước lượng 02 §2.2). Bù lại: không có write amplification do rewrite aggregate 128–256 KiB; mỗi document ghi chỉ ≤ 64 KiB.
- Giới hạn transaction: command khai `Touches ≤ 4` aggregate `[MẶC ĐỊNH, TBD-07]`, marker ≤ 2 → ≤ 7 document; vượt → startup fail (ARC-07).
- Escape hatch (không làm ở P0, ghi vào §G của PCR): "fast path một aggregate + receipt nhúng" chỉ xem xét ở P1 nếu ACC-16 cho thấy p95 không đạt, và chỉ sau khi V15 được xác minh cho phép trộn ghi an toàn.

### A.5. Contract tối thiểu (C#, chỉ chữ ký)

```csharp
public sealed record PlayerAggregateDescriptor(
    string AggregateId,        // = section id trên wire; ^[a-z][a-z0-9_]{1,31}$
    string Collection,         // mặc định "pa_" + AggregateId
    Type StateType,
    int SchemaVersion,
    bool ClientVisible = true,
    int WarnSizeBytes = 32 * 1024,
    int HardSizeBytes = 64 * 1024);

public interface IAggregateFactory<TState> { TState CreateDefault(string playerId); }   // document absent ⇒ state này, rev = 0

public interface IPlayerAggregateRegistry
{
    void Register<TState>(PlayerAggregateDescriptor descriptor, IAggregateFactory<TState> factory, IDocumentUpgrader<TState>? upgrader = null);
    IReadOnlyList<PlayerAggregateDescriptor> All { get; }
}

public sealed record CommandDescriptor(
    string Operation,
    IReadOnlyList<string> Touches,   // aggregateId được ghi; ≤ 4
    IReadOnlyList<string> Reads);    // aggregateId chỉ đọc (không rev++)

public interface ICommandContext            // bề mặt duy nhất handler được dùng để đọc/ghi
{
    IActorContext Actor { get; }
    IClock Clock { get; }
    ConfigSnapshot Config { get; }                                   // configVersion + section typed
    ValueTask<TState> GetAsync<TState>(string aggregateId, CancellationToken ct);   // read-your-writes trong transaction
    void Set<TState>(string aggregateId, TState state);              // đánh dấu ghi; rev++ khi commit; aggregateId phải thuộc Touches
    ValueTask ClaimAsync(string markerCollection, string markerKey, object payload, CancellationToken ct); // Insert trong txn; tồn tại ⇒ ClaimConflictException
    void Emit(OutboxEntry entry);                                     // ledger / audit / score / event
}

public interface ICommandHandler<TRequest, TResult>
{
    Task<CommandOutcome<TResult>> ExecuteAsync(ICommandContext ctx, TRequest request, CancellationToken ct);
}

public sealed record CommandOutcome<TResult>(ErrorCode Code, TResult? Result, ResultCompact Compact);

public interface ICommandRunner          // Platform.Couchbase; điểm duy nhất ghi pa_* và receipts
{
    Task<CommandResponse> RunAsync(CommandEnvelope envelope, CancellationToken requestCt);
    // 1 transaction: lookup receipt → reads → handler → Replace/Insert aggregates → Insert markers → Insert receipt → commit;
    // sau commit: relay outbox inline best-effort. Không nhận requestCt cho bước commit (PERF-02).
}

public interface IPlayerAggregateWriter  // dùng cho ghi KHÔNG phải command (login epoch++, purge PRIV-01); vẫn là transaction
{
    Task MutateAsync(string playerId, IReadOnlyList<string> aggregateIds, Func<ICommandContext, ValueTask> mutate, CancellationToken ct);
}

public interface IPlayerSnapshotReader   // đọc ngoài transaction (KV get song song), dùng cho Login/GetSnapshot/GM lookup
{
    Task<IReadOnlyList<Section>> ReadAsync(string playerId, IReadOnlyList<string>? aggregateIds, CancellationToken ct);
}
```

Arch test (ARC-01/CMD-05 mới): không assembly nào ngoài `Platform.Couchbase` tham chiếu `ICollection.ReplaceAsync/UpsertAsync/InsertAsync/RemoveAsync` cho collection có prefix `pa_` hoặc `receipts`.

### A.6. Trả lời lại rủi ro của 02/03 dưới mô hình nhiều aggregate

| Rủi ro cũ | Dưới thiết kế mới |
| --- | --- |
| 02 R1: transaction trên hot path → latency ×3–5, retry storm, CommitAmbiguous khó | **Chấp nhận có chủ đích** (hệ quả trực tiếp của TBD-07). Giảm nhẹ: ≤ 4 aggregate/command, document nhỏ (≤ 64 KiB), per-player limiter chặn retry storm cùng player, ambiguous giải bằng receipt key trong transaction. Bắt buộc đo ở ACC-16 với số TBD-06. |
| 02 R2: receipt tách document không nguyên tử → double effect | Hết: receipt là thành viên transaction. |
| 02 R3: giftcode hot key | Không đổi: counter reserve-then-confirm ngoài transaction (counter op không nằm trong transaction [VERIFY] V16); marker per-player Insert trong transaction. |
| 03 B-01: EndBattle chạm 2 document → thưởng hai lần | `pa_battle` + `pa_progression` + `pa_wallet` trong một transaction; hai EndBattle khác `requestId` cùng `battleId`: cái thứ hai đọc `activeBattle = null` (sau conflict retry) → `NO_ACTIVE_BATTLE`. |
| 03 M-01: ngân sách ring | Không còn ring; receipt ≤ 4 KiB tách khỏi ngân sách aggregate. |
| 03 M-02: receipt tăng vô hạn, chi phí projection | Purge 180 ngày giữ; chi phí relay giảm 1 insert; chi phí transaction tăng — ghi tường minh ở DAT-09. |
| 03 M-03: thiếu cơ chế (c) | (c) co lại thành "marker Insert trong transaction (a)"; saga + reaper bị xoá; counter là trường hợp duy nhất ngoài transaction. |
| 03 M-04: section quá lớn | Section = aggregate ≤ 32/64 KiB; game tự tách inventory lớn thành nhiều aggregate. |
| 03 M-05: TBD-18 | Owner chốt Không → gỡ toàn bộ tham chiếu Sniper PVP (§E). |
| 03 m-19: thứ tự tạo player/orphan | Hết: aggregate absent = default; tạo = insert mapping; link = transaction {Insert mapping, Set profile, Insert receipt}. |

---

## §B. Battle (TBD-02)

**Phân tích.** Battle là aggregate riêng (không phải field trong Progression) vì: tần suất ghi khác (mỗi trận 2 lần vs progression đổi khi lên cấp), kích thước evidence cần kiểm soát riêng, game thay/ bỏ module battle không đụng Progression, và cần một command một-aggregate (StartBattle) để kiểm chứng đường transaction 2 document. Chi phí: EndBattle thêm 1 document trong transaction (+2 KV op).

**Quyết định.**
- `pa_battle`: `{activeBattle: {battleId, startedAtUtc, expiresAtUtc, configVersion, seed, mode}?, stats: {started, completed, abandoned}, lastCompletedBattleId}`. Một `activeBattle` duy nhất.
- StartBattle: kiểm tra eligibility theo policy game (`IBattleStartPolicy`: điều kiện mở, cooldown…); nếu đang có `activeBattle` chưa hết hạn → policy `OnActiveBattle` mặc định **Replace** (battle cũ ghi `abandoned`, không thưởng) `[MẶC ĐỊNH — chờ owner chốt, TBD-02]`; policy `Reject` trả `BATTLE_IN_PROGRESS` (200, business). Lý do chọn Replace: mobile client bị kill/crash giữa trận là thường; Reject bắt buộc thêm command AbandonBattle và UX; Replace vẫn bảo đảm chỉ một battle được thưởng. Hết hạn (`expiresAtUtc` = start + 24 h `[MẶC ĐỊNH, TBD-02]`) → coi như không có battle, dọn ở command kế tiếp.
- EndBattle(battleId, evidence): `activeBattle` null hoặc `battleId` khác → `NO_ACTIVE_BATTLE`; quá `expiresAtUtc` theo `IClock` → `BATTLE_EXPIRED` và xoá `activeBattle` (ghi `pa_battle`, có receipt); hợp lệ → `IBattleResultPolicy` kiểm evidence (P0: eligibility-only), tính reward theo `configVersion` **đã ghi trong `activeBattle`**, cập nhật `pa_progression`, `pa_wallet` (`pa_inventory` nếu có drop), xoá `activeBattle`, `stats.completed++`, outbox ledger — một transaction.
- Mã lỗi (catalog, dải module Battle): `BATTLE_IN_PROGRESS`, `NO_ACTIVE_BATTLE`, `BATTLE_EXPIRED` — HTTP 200, `retryable = false`.

---

## §C. Session single + takeover (TBD-04a)

**Phân tích.** Cơ chế rẻ nhất tái dùng `sessionEpoch` đã có: mỗi login tăng `sessionEpoch` trong `pa_profile` và session mới mang epoch mới; session hợp lệ ⇔ `session.epoch == profile.sessionEpoch`. Không cần `currentSessionId`, không cần xoá session cũ (TTL tự dọn; xoá best-effort). Cửa sổ 60 s: hai lựa chọn — (1) giữ cache ≤ 60 s, chấp nhận hai phiên cùng sống ≤ 60 s; (2) đọc `pa_profile` mỗi request (+1 KV get ~0,5 ms). Chọn **(1)** `[MẶC ĐỊNH — chờ owner chốt, TBD-04a]`: hậu quả của cửa sổ chỉ là UX (thiết bị cũ thấy mình còn đăng nhập ≤ 60 s rồi nhận `SESSION_REPLACED`); state vẫn nhất quán nhờ transaction + per-player ordering; không thêm chi phí hot path. Nếu owner cần "đá ra tức thì", (2) là một cấu hình `SessionCheck = Strict` — chưa làm ở P0.

**Quyết định.**
- Login = transaction trên `pa_profile` (`sessionEpoch++`, cập nhật `identities[]` projection nếu cần) qua `IPlayerAggregateWriter` (không receipt), rồi Insert `sessions` `{tokenHash, playerId, epoch, createdAt, lastTouchAt, expiry}`; session cũ (nếu biết) Remove best-effort.
- Xác thực mỗi request (thứ tự): token → session doc (cache ≤ 60 s) → `pa_profile` (cache ≤ 60 s): `deleted` → 401 `ACCOUNT_DELETED`; `ban.scope = login` → `ACCOUNT_SUSPENDED`; `session.epoch < profile.sessionEpoch` → 401 `SESSION_REPLACED` (`retryable = false`); token không có/hết hạn → 401 `UNAUTHENTICATED`.
- SDK: nhận `SESSION_REPLACED` → phát sự kiện cho game hiển thị "đã đăng nhập ở thiết bị khác", **không** tự re-login (tránh hai thiết bị đá nhau vô hạn); game quyết định cho người dùng bấm "đăng nhập lại".
- BAN-01 giữ nguyên (ban `login` cũng tăng epoch; precedence `ACCOUNT_SUSPENDED` trước `SESSION_REPLACED`). PRIV-01: đánh dấu `deleted` + epoch++.

---

## §D. Provider Google Play Games + Apple Game Center (TBD-03)

**PGS v2 (Android).** Client (plugin PGS v2) gọi `requestServerSideAccess(webClientId, forceRefreshToken = false)` → **server auth code** gửi lên server trong request login/link. Server: POST `https://oauth2.googleapis.com/token` (`grant_type = authorization_code`, web client id/secret từ OPS-08, `code`) → `access_token` (không cần refresh token vì chỉ cần danh tính một lần); GET `https://www.googleapis.com/games/v1/players/me` với Bearer → `playerId` (+ `gamePlayerId`, `originalPlayerId` nếu có) [VERIFY] V18. Key identity: **`pgs:{playerId}`** — `playerId` là id PGS trả về cho app này, ổn định qua cài lại; `gameProjectId` trong key của rev 2 bỏ vì một game/deployment và scope Couchbase đã tách game; lưu `gamePlayerId`/`originalPlayerId` trong value của mapping để đối chiếu nếu Google đổi scheme. Không chấp nhận raw `playerId` do client gửi (API-12).

**Game Center (iOS).** Client gọi `GKLocalPlayer.local.fetchItems(forIdentityVerificationSignature:)` → `publicKeyURL`, `signature`, `salt`, `timestamp` + `teamPlayerID`, `gamePlayerID`, `bundleId`. Server: (1) `publicKeyURL` host phải kết thúc `.apple.com` (chống SSRF), tải chứng chỉ (cache theo URL ≤ 24 h `[MẶC ĐỊNH, TBD-03]`), xác minh chuỗi tới Apple root; (2) `|now − timestamp| ≤ 10 phút` `[MẶC ĐỊNH, TBD-03]`; (3) payload = `teamPlayerID (UTF-8) ‖ bundleId (UTF-8) ‖ timestamp (uint64 big-endian) ‖ salt`, verify RSA-SHA256 với public key [VERIFY] V19; (4) `bundleId` phải bằng giá trị cấu hình. Key identity: **`gamecenter:{teamPlayerId}`** — ổn định qua cài lại và xuyên các game cùng team (đúng mục đích IDN-11 "tìm lại cùng player"); `gamePlayerId` lưu trong value mapping. Không dùng `playerID` cũ (deprecated).

**Chung.** Contract `IIdentityProviderAdapter { string ProviderId; Task<ProviderIdentity> VerifyAsync(ProviderCredential, CancellationToken); }` → `{MappingKey, Attributes}`; Google Sign-In và Facebook **ngoài phạm vi** (game thêm adapter riêng qua contract này); V12 xoá. Rate limit SEC-13 theo "provider subject" = `MappingKey`. Phase IDN-03/04/05/06/11 giữ P0.5.

---

## §E. Sample command P0 (TBD-01) và IAP

- `SampleGame` (G7 mới): 5 aggregate `profile`, `progression {level, xp, stage}`, `wallet {gold, gem}`, `inventory {items[itemId → qty]}` (stackable), `battle`; catalog từ file config: `resources`, `shop` (`shopItemId → {price: {resourceType, amount}, grant: {itemId, qty}}`), `iap` (`platform:productId → grants[]`), `battle` (reward theo stage).
- **SMP-01 Login**: framework (guest P0; PGS/Game Center P0.5); trả session + snapshot mọi aggregate `ClientVisible` + `configVersion`.
- **SMP-02 StartBattle** (Touches: `battle`) — §B.
- **SMP-03 EndBattle** (Touches: `battle`, `progression`, `wallet`) — §B.
- **SMP-04 IAPurchase** (Touches: `wallet`, `inventory`; marker `extref::iap_{store}::{externalReference}`): adapter verify với store **trước** transaction (outbound HTTP, idempotent); transaction: `ClaimAsync(extref)` (tồn tại → `ALREADY_GRANTED`), grant theo catalog `iap`, ledger `sourceType = iap` với `orderId/transactionId`, receipt. Google: Play Developer API `purchases.products.get(packageName, productId, purchaseToken)` bằng service account (OPS-08); hợp lệ khi `purchaseState = 0`; `externalReference = purchaseToken` (luôn có; `orderId` có thể rỗng với test purchase [VERIFY] V20) và `orderId` lưu trong ledger `[MẶC ĐỊNH, TBD-01]`. Apple: StoreKit 2 `jwsRepresentation` do client gửi; server xác minh chuỗi x5c tới Apple Root CA, `bundleId`, `environment` khớp deployment environment, `productId`; `externalReference = transactionId` [VERIFY] V21 `[MẶC ĐỊNH, TBD-01]`. Acknowledge/consume: client thực hiện sau khi server trả thành công (Unity IAP `ConfirmPendingPurchase`); server không gọi acknowledge ở P0 `[MẶC ĐỊNH, TBD-01]`. CI dùng `FakeStoreVerifier`; sandbox thật kiểm thủ công trước P0.5.
- **SMP-05 BuyVirtualItem** (Touches: `wallet`, `inventory`): catalog `shop`; debit kiểm số dư (`INSUFFICIENT_FUNDS`), credit kiểm `maxQuantity` (ECO-02) — cả hai trong một transaction; 2 ledger entry cùng `receiptId`.
- IAP-01/02 → **P0**; ECO-08 → **P0** (IAPurchase cần); IAP-03 (Google RTDN/voided purchases, Apple App Store Server Notifications v2; refund = command hệ thống có receipt/ledger, không âm hoá ngoài rule PCR) → **P0.5**.
- Xoá: MIG-01/02, mục 15.2, deliverable 9, quy ước "P0.5*/P1*" ở 3.1, `legacy:{provider}:{id}` ở IDN-01, G2 cũ, các bullet TBD-18 ở 2.5, "từ Sniper PVP" ở 12.1/TBD-06/TBD-15, V7 "phiên bản của Sniper PVP", V13. TBD-06 → Mở (không nguồn giá trị tạm). TBD-18 → Chốt: Không.

---

## §F. Game config = file (TBD-17)

**Quyết định.**
- Config là thư mục `config/` trong artifact deploy (image/package) gồm `manifest.json` `{label, files[], clientVisible[]}` và các file JSON (game có thể đăng ký `IConfigFileLoader` cho định dạng khác). Overlay: `config/base/` + `config/env/<Development|Staging|Production>/` (merge theo file, env đè base) — environment override nằm trong artifact, không phải volume `[MẶC ĐỊNH — chờ owner chốt, TBD-17]`.
- `configVersion = "{manifest.label}+{sha256(effective content)[0..12]}"`; tính một lần khi startup; thiếu/không parse được → startup fail (ARC-06). Receipt ghi `configVersion` (CFG-02 giữ).
- Reload = **restart** (rolling deploy). Không file-watch ở Staging/Production (per-instance skew không có điều phối). Ở Development, host **có thể** bật file-watch (SHOULD) cho năng suất dev. Lý do: config đi cùng binary nên skew version config ≡ skew binary, đã được OPS-06 xử lý; mọi instance cùng version sau khi rollout hoàn tất; trong rollout, receipt của từng command ghi đúng version instance đó dùng — chấp nhận và ghi PCR. Readiness/metric `config_version_info{version}` để ops thấy skew.
- Phân phối client: framework cung cấp endpoint Public `GET /v1/config/{configVersion}/{file}` chỉ cho file trong `clientVisible[]`, header cache immutable; CDN đứng trước là việc deployment (ghi OPS runbook), không phải contract. CFG-03 giữ: client nhận `configVersion` trong mọi response, tải lại khi đổi.
- Hệ quả: bỏ collection `config`, bỏ `PublishConfig`, bỏ "GM sửa config qua page P1", bỏ cache config ≤ 30 s; `gate` (maintenance, `minClientVersion`) **vẫn** là document Couchbase do GM sửa (GATE-01/02 không đổi) vì cần đổi không deploy. Resource Catalog (ECO-07) và IAP catalog đọc từ config file.

---

## §G. Một game/deployment (TBD-11)

- ARC-10: `gameId` và `environmentId` **chỉ** từ deployment config; bỏ "session claim"; startup fail nếu thiếu.
- DAT-01: giữ scope `g_<gameId>` (tái dùng cluster cho nhiều deployment) và trường `gameId`/`environmentId` trong document (defense-in-depth); bỏ nhánh claim.
- G1 → chuyển sang 2.5 "Ngoài phạm vi": hosting nhiều game trong một deployment. ARC-09 giữ nguyên (scope/trường bắt buộc từ P0 — chi phí 0, bảo vệ cluster dùng chung).
- D4 viết lại; TBD-11 → Chốt.

---

## §H. Rà soát dây chuyền

| ID / mục | Ảnh hưởng | Sửa gì (chi tiết ở §I) |
| --- | --- | --- |
| 2.2 Persistence / Scheduler | Transactions là bắt buộc P0 | Persistence: "transactions bắt buộc, metadata collection riêng"; Scheduler/outbox: outbox trong receipt |
| 2.3 D1–D4 | Toàn bộ | Viết lại |
| 2.4 G1, G2, G7 | TBD-11/18/01 | G1 xoá (→ 2.5), G2 viết lại, G7 = 5 aggregate/5 command |
| 2.5, 2.6, 3.1, 19.1, 19.2 | IAP P0, MIG xoá, provider | Cập nhật |
| 3.2 glossary | Identity, Aggregate, Section, Idempotency entry, Receipt, Claim marker, Outbox, Config version | Viết lại |
| 4.1 Platform.Couchbase | Transaction runner | Thêm "transaction runner (điểm ghi duy nhất cho `pa_*`/`receipts`)" |
| ARC-10, DAT-01 | TBD-11 | Bỏ claim |
| API-05, API-11 | Mã mới/xoá | Thêm `SESSION_REPLACED`, `ACCOUNT_DELETED`, `BATTLE_*`, `INSUFFICIENT_FUNDS`, `INVALID_RECEIPT`; xoá `COMMAND_IN_PROGRESS` |
| API-10, D3, SDK-03 | Section = aggregate | `sectionId = aggregateId`, `rev` đơn per aggregate |
| IDN-01, IDN-03, IDN-04, IDN-05, IDN-08, IDN-10, IDN-11 | Provider, session, tạo player | Viết lại |
| BAN-01 | Không đổi ngữ nghĩa | Thay "player aggregate" → `pa_profile`; precedence mã lỗi |
| PRIV-01 | Nhiều aggregate | `deleted` + epoch++ trong `pa_profile`; job purge mọi `pa_*` qua transaction runner |
| PLY-02, PLY-03 | Snapshot N aggregate, schemaVersion per aggregate | Câu chữ |
| ECO-03/04/05/06/08 | Transaction, outbox trong receipt, P0 | Viết lại |
| CMD-01..09 | Tầng A/B, ring, window | Viết lại CMD-03/04/05/07/08/09; CMD-01/06 chỉnh nhỏ |
| 8.4 | Battle aggregate | Viết lại |
| CFG-01..03 | File | Viết lại |
| IAP-01..03 | P0 / P0.5 | Viết lại |
| GFT-03, GFT-04, GFT-05 | Marker trong transaction, counter ngoài | Viết lại GFT-03/04; GFT-05 câu chữ |
| GM-02, GM-03 | Mapping key, rev per aggregate | Câu chữ |
| SEC-09 | Allowlist | Thêm `/v1/config/{version}/{file}` |
| SEC-14 | Audit qua receipt | Viết lại |
| LDB-01 (A), LDB-02, EVT-01, JOB-02, OPS-05 | Outbox = receipt | Câu chữ / viết lại JOB-02, OPS-05 |
| PERF-02, PERF-07, PERF-08 | Transaction expiration, N1QL sweeper | Viết lại PERF-02; PERF-07/08 câu chữ |
| DAT-03, DAT-04, DAT-05, DAT-07, DAT-08, DAT-09, DAT-10 | Toàn bộ | Viết lại DAT-04/09; chỉnh DAT-03/05/07/08/10 |
| 14.1 bảng | Bốn bản ghi | Viết lại |
| OBS-01 | Metric transaction | Thêm `txn_attempts_total`, `txn_retries_total`, `txn_ambiguous_total`, `txn_duration_seconds`, `txn_cleanup_lost_total`, `aggregate_size_bytes{aggregateId}`, `config_version_info` |
| OPS-07 | Runbook | Thêm "lost transaction cleanup", "config rollout skew" |
| MIG-01/02, 15.2, deliverable 9 | TBD-18 | Xoá |
| SDK-02 | Mã lỗi mới | Thêm `SESSION_REPLACED` (không auto re-login), `ACCOUNT_DELETED` |
| TST-01 | Transactions trong Testcontainers | Nhấn mạnh bật transactions + metadata collection |
| ACC-01/03/04/06/07/20/29/35/39/44/45/46/47 | Nhiều | Viết lại; thêm ACC-53 |
| Mục 20 TBD | 01, 02, 03, 04a, 06, 07, 11, 12, 13, 15, 17, 18 | Cập nhật trạng thái/mặc định |
| Mục 21 VERIFY | V5, V12, V13 xoá; V7 sửa; V15–V21 thêm | Cập nhật |
| Phụ lục B | ~12 dòng | Cập nhật |
| Phụ lục C | Số đếm | Sinh lại sau khi áp |
| PCR-01 | Nội dung tối thiểu | "danh sách thao tác được dùng transaction (DAT-09)" → "danh sách aggregate và bảng command → Touches/Reads (DAT-09)" |

---

## §I. ID → văn bản yêu cầu mới (sẵn sàng dán)

Loại: **sửa** / **thêm** / **xoá** / **chuyển phase**. Văn bản trong cột "Văn bản mới" thay toàn bộ ô "Yêu cầu" (hoặc đoạn văn) tương ứng.

### I.1. Mục 2 (phạm vi, quyết định nền, giả định, phân kỳ)

| ID / mục | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| 2.1 Identity | sửa | Local ID generate ở client cho guest; liên kết **Google Play Games Services** (Android) và **Apple Game Center** (iOS) và tìm lại cùng player sau khi cài lại app (IDN-11). Google Sign-In/Facebook: ngoài phạm vi baseline (adapter của game qua `IIdentityProviderAdapter`). | — | — |
| 2.1 Game config | sửa | Framework sở hữu cơ chế game config dạng **tập file đóng gói cùng deploy** có `configVersion` (CFG-01..03); không lưu config trong Couchbase. | — | — |
| 2.1 (thêm hàng) Sample commands | thêm | Framework giao kèm `SampleGame` với 5 command chuẩn mọi game đều có: Login, StartBattle, EndBattle, IAPurchase, BuyVirtualItem (SMP-01..05). | — | — |
| 2.2 Persistence | sửa | Couchbase Server ≥ 7.6 [VERIFY] V3 (**distributed transactions bắt buộc cho command pipeline — DAT-04**, scope/collection, RBAC mức collection); Couchbase .NET SDK 3.x pinned [VERIFY] V2; transaction metadata collection riêng `txn_meta` [VERIFY] V17; ma trận tương thích SDK/Server chạy trong CI (CI-01). | — | — |
| 2.2 Scheduler/outbox | sửa | Outbox nằm trong receipt document của command (DAT-04(b)); relay inline + sweeper; lease document cho job (JOB-01); không Hangfire/Quartz [VERIFY] V14; broker là adapter sau. | — | — |
| 2.3 D1 | sửa | **Nhiều player aggregate theo ranh giới nghiệp vụ** (owner chốt 2026-10-22): mỗi aggregate = một document trong collection `pa_<aggregateId>` (key `playerId`), có `rev` đơn riêng và ngân sách kích thước riêng (cảnh báo 32 KiB, cứng 64 KiB); game đăng ký aggregate qua `PlayerAggregateDescriptor`; framework bắt buộc có `profile`; tối đa 16 aggregate/player. Mọi command thay đổi trạng thái commit bằng **một Couchbase transaction** gồm các aggregate chạm (≤ 4) + receipt document (+ claim marker). Mọi ghi vào `pa_*` chỉ qua transaction runner. (DAT-09, DAT-04) `[MẶC ĐỊNH — chờ owner chốt, TBD-07]` cho các giá trị 32/64 KiB, 16, 4. | — | — |
| 2.3 D2 | sửa | Idempotency entry + receipt = **một document** `rcpt::{receiptId}` ghi (Insert) trong cùng transaction với aggregate; chứa `bodyHash`, `resultCompact` (≤ 512 B), `aggregates[{id, newRev}]`, `configVersion`, outbox entry (ledger/audit/score). Replay window **24 h** thực thi khi tra cứu; retention receipt 180 ngày bằng job (TBD-08); ledger/audit là projection từ outbox của receipt, không xoá. Invariant "một lần" = state riêng (claim marker Insert trong transaction; `activeBattle` trong `pa_battle`). `[MẶC ĐỊNH — chờ owner chốt, TBD-13]` cho 24 h, 4 KiB, 512 B. | — | — |
| 2.3 D3 | sửa | Per-aggregate `rev` đơn điệu lưu trong document (không dùng Couchbase CAS làm `rev`); section của API-10 ≡ aggregate (`sectionId = aggregateId`); response command chỉ chứa aggregate đã thay đổi, `full = true` ở P0; client áp dụng khi `full = true` hoặc `baseRev` khớp, ngược lại refetch aggregate. (TBD-12 chấp nhận; owner ghi chú "chỉ response info thay đổi" = chỉ aggregate đã đổi). | — | — |
| 2.3 D4 | sửa | Environment = cluster (Production) hoặc bucket riêng (Dev/Staging); game = Couchbase scope `g_<gameId>`; module = collection. **Một deployment phục vụ đúng một game**: `gameId` và `environmentId` là hằng trong deployment config; không từ claim, route hay body. (TBD-11 chốt 2026-10-22) | — | — |
| 2.4 G1 | xoá | (chuyển thành bullet 2.5 "Hosting nhiều game trong một deployment: ngoài phạm vi; scope `g_<gameId>` giữ để tái dùng cluster") | — | — |
| 2.4 G2 | sửa | Framework **không** phục vụ di dời game hiện hữu khỏi PlayFab (TBD-18 = Không, chốt 2026-10-22). Không có giá trị capacity/target nào lấy từ game hiện hữu; TBD-06 Mở cho tới khi game đầu tiên cung cấp số hoặc owner đặt giả định thiết kế. | — | — |
| 2.4 G7 | sửa | P0 dùng `SampleGame` do backend định nghĩa: 5 aggregate (`profile`, `progression`, `wallet`, `inventory`, `battle`), 2 resource (`gold`, `gem`) + stackable item, 5 command SMP-01..05, evidence battle = eligibility-only. TBD-01 (chốt về danh sách command) và TBD-02 chỉ còn chặn tích hợp game thật ở phần rule riêng. | — | — |
| 2.5 (bullet IAP) | sửa | IAP receipt validation (Google Play Developer API, Apple App Store Server API/JWS) thuộc baseline **P0** (IAP-01/02, SMP-04); refund/revoke qua S2S notification là P0.5 (IAP-03). Mọi grant từ nguồn ngoài qua ECO-08. | — | — |
| 2.5 (bullet di dời PlayFab) | sửa | Di dời dữ liệu từ backend hiện hữu: **ngoài phạm vi** (TBD-18 = Không). Không có yêu cầu MIG; không dự trữ dạng khoá `legacy:`. | — | — |
| 2.5 (bullet mới) | thêm | Hosting nhiều game trong một deployment: ngoài phạm vi (TBD-11). Google Sign-In, Facebook Login: ngoài phạm vi baseline; game thêm adapter qua `IIdentityProviderAdapter`. GM page sửa game config: ngoài phạm vi (config là file, TBD-17). | — | — |
| 2.6 P0 | sửa | Mục tiêu: `SampleGame` chạy end-to-end trên Development: guest login → snapshot N aggregate → StartBattle/EndBattle/BuyVirtualItem/IAPurchase (store verifier giả) → aggregate/receipt/ledger đúng qua transaction; GM backend permission + frontend tối thiểu; module mẫu + thay policy; Unity SDK tối thiểu; integration test Couchbase thật (transactions bật) trong CI. Kết thúc bằng ACC: 01 (guest), 02, 04, 05, 06, 07, 09, 10, 12, 13, 17, 18, 20, 21, 24, 25, 26, 27, 28, 32, 33, 34, 35, 36, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47 (verifier giả), 50, 51, 52, 53. | — | — |
| 2.6 P0.5 | sửa | PGS/Game Center link + conflict + recovery; IAP-03; Giftcode đầy đủ; GM SSO/CSRF/CORS/thu hồi quyền/Data Protection; GM frontend đầy đủ; maintenance/version gate, ban, account deletion; load test + restore drill + runbook. ACC-03, 08, 11, 16, 19, 22, 23, 29, 30, 31, 37, 47 (sandbox thật), 48, 49. | — | — |
| 3.1 Cột Phase | sửa | Bỏ quy ước "P0.5*/P1*". Giữ quy ước ô hai phase. | — | — |

### I.2. Thuật ngữ (3.2) và 4.1

| ID / mục | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| Identity | sửa | Một phương thức chứng minh quyền truy cập player: guest, Google Play Games Services, Apple Game Center (adapter khác do game thêm); mỗi identity là một mapping key → `playerId`. | — | — |
| Aggregate | sửa | Một document nghiệp vụ là đơn vị nhất quán nội tại có `rev` riêng. **Player aggregate**: document trong `pa_<aggregateId>` (key `playerId`) do game đăng ký qua `PlayerAggregateDescriptor` (ví dụ `profile`, `progression`, `wallet`, `inventory`, `battle`); nhiều player aggregate được ghi cùng lúc trong một transaction (DAT-04). Aggregate khác: giftcode definition, gate, gm_staff. | — | — |
| Section | sửa | Đơn vị đồng bộ trên wire của API-10; **section ≡ player aggregate có `ClientVisible = true`**, `sectionId = aggregateId`, `rev` là `rev` của aggregate. | — | — |
| Idempotency entry | sửa | Phần của receipt document (`bodyHash`, `resultCode`, `resultCompact`, `aggregates[]`) dùng để dedupe retry trong replay window 24 h (CMD-07); hết window thì retry xử lý như request mới. | — | — |
| Receipt | sửa | Document `rcpt::{receiptId}` trong `receipts`, Insert trong cùng transaction với thay đổi trạng thái (CMD-03): `receiptId`, `actorId`, operation, `requestId`, `bodyHash`, `resultCode`, `resultCompact` (≤ 512 B), `configVersion`, `schemaVersions`, `aggregates[{aggregateId, newRev}]`, `claimKeys[]`, `committedAtUtc`, `outbox[]`, `outboxPending`; ≤ 4 KiB; không dùng Couchbase expiry; purge bằng job sau 180 ngày (TBD-08); tra cứu bởi actor sở hữu và GM có quyền (CMD-09). | — | — |
| Claim marker | sửa | Document insert-only khoá tự nhiên của hiệu ứng "một lần" ngoài aggregate (`gclaim::{code}::{playerId}`, `extref::{sourceType}::{externalReference}`, `membership::{season}::{playerId}`), **Insert trong cùng transaction** với aggregate và receipt (DAT-04(a)); không có trạng thái pending; là state nghiệp vụ, không hết hạn. | — | — |
| Outbox | sửa | Mảng `outbox[]` trong receipt document (với command) hoặc progress document (với job P1), ghi trong cùng transaction với state; relay inline + sweeper chuyển thành ledger, audit, score… (at-least-once, consumer idempotent theo `entryId`); entry không bị xoá khỏi receipt, chỉ `outboxPending` đổi. | — | — |
| Config version | sửa | `configVersion = "{label}+{hash12}"` của tập file config hiệu lực (CFG-01), tính khi startup; ghi vào receipt (CFG-02). | — | — |
| 4.1 Platform.Couchbase | sửa | Kết nối, cấu hình, **transaction runner** (`ICommandRunner`, `IPlayerAggregateWriter` — điểm ghi duy nhất cho `pa_*` và `receipts`), CAS/Insert helper cho document ngoài transaction, lease, outbox relay/sweeper, index/collection provisioning, lazy upgrader pipeline. Không sở hữu schema nghiệp vụ. | — | — |

### I.3. ARC / API / IDN / BAN / PRIV

| ID | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| ARC-10 | sửa | `gameId` và `environmentId` được xác định **chỉ** từ deployment config (hằng của deployment); thiếu → startup fail (ARC-06). Không lấy từ claim, route hay request body. Mọi repository key và permission check nhận hai giá trị này từ actor context. (TBD-11 chốt) | P0 | arch test; ACC-02; ACC-10; ACC-26 |
| API-05 | sửa | HTTP status chỉ phân loại; ngữ nghĩa nghiệp vụ nằm ở `code`. Ánh xạ bắt buộc: 400 request sai; 401 chưa xác thực (`UNAUTHENTICATED`, `SESSION_REPLACED` (IDN-10), `ACCOUNT_DELETED`); 403 thiếu quyền; 409 conflict (`IDENTITY_CONFLICT` cho IDN-06, `IDEMPOTENCY_PAYLOAD_MISMATCH` cho CMD-02, `CONCURRENT_UPDATE` với `retryable = true` cho CMD-05); 413 quá dung lượng; 415 sai content type; 426 `UPDATE_REQUIRED` (GATE-02); 429 vượt giới hạn; 503 `MAINTENANCE` (GATE-01) và `COMMIT_UNKNOWN_RETRY` (CMD-04); 5xx khác là lỗi hạ tầng. Lỗi nghiệp vụ hợp lệ trả 200 với `code` (`INSUFFICIENT_FUNDS`, `UNKNOWN_ITEM`, `BATTLE_IN_PROGRESS`, `NO_ACTIVE_BATTLE`, `BATTLE_EXPIRED`, `ALREADY_GRANTED`, `INVALID_RECEIPT`, `GIFTCODE_QUOTA_EXCEEDED`, `ALREADY_CLAIMED`, `AGGREGATE_TOO_LARGE`). `COMMAND_IN_PROGRESS` bị xoá khỏi catalog. Bảng đầy đủ thuộc API-11. | P0 | ACC-40; Integration |
| API-10 | sửa | **Mô hình revision per-aggregate.** Section ≡ player aggregate `ClientVisible` (DAT-09); `sectionId = aggregateId`; mỗi aggregate có `rev` đơn điệu lưu trong document, tăng mỗi lần ghi; không dùng Couchbase CAS làm `rev`. P0 `SampleGame` có 5 section: `profile`, `progression`, `wallet`, `inventory`, `battle`; game thêm/bớt aggregate qua `PlayerAggregateDescriptor`. Snapshot = `repeated Section {id, rev, payload}` cho mọi aggregate `ClientVisible` (document chưa tồn tại → payload mặc định, `rev = 0`). Response command = `repeated SectionUpdate {id, baseRev, newRev, payload, full}` **chỉ chứa aggregate có thay đổi** (`newRev = rev` mới); một command có thể đổi nhiều aggregate và các `newRev` đó cùng được commit trong một transaction. P0 `full = true`. Quy tắc client: áp dụng khi `full = true` hoặc `baseRev == rev` đang giữ; ngược lại `GetSnapshot(sections=[id])`. Mỗi aggregate nhất quán nội tại tại `rev` của nó; snapshot đọc ngoài transaction nên không bảo đảm nhất quán chéo aggregate; danh sách aggregate và kích thước ghi trong PCR. Mục tiêu ≤ 32 KiB/aggregate (metric) `[MẶC ĐỊNH — chờ owner chốt, TBD-07]`. | P0 | ACC-34; PCR |
| IDN-01 | sửa | Server phải có `playerId` nội bộ ổn định, chuỗi opaque do server cấp; local ID và provider identity là mapping về player trong `identities` (key = mapping key, value `{playerId, linkedAt, providerAttributes}`). Tạo player lần đầu = **Insert mapping** (thua race → trả player thắng); player aggregate không cần tạo trước — document chưa tồn tại là trạng thái mặc định `rev = 0` (DAT-09). Link (IDN-05) = một transaction {Insert mapping mới, Set `pa_profile.identities[]`, Insert receipt}; mapping đã tồn tại → transaction rollback → 409 `IDENTITY_CONFLICT`. | P0 | ACC-01; ACC-02 |
| IDN-03 | sửa | Framework hỗ trợ adapter xác minh **Google Play Games Services v2** (client gửi server auth code; server đổi lấy access token tại Google OAuth với web client id/secret từ OPS-08, gọi `games/v1/players/me` lấy `playerId` [VERIFY] V18) và **Apple Game Center** (client gửi `publicKeyURL`, `signature`, `salt`, `timestamp`, `teamPlayerID`, `gamePlayerID`, `bundleId` từ `fetchItems(forIdentityVerificationSignature:)`; server kiểm host `publicKeyURL` thuộc `*.apple.com`, tải và xác minh chuỗi chứng chỉ tới Apple root (cache ≤ 24 h), `|now − timestamp| ≤ 10 phút, verify RSA-SHA256 trên `teamPlayerID ‖ bundleId ‖ timestamp(uint64 BE) ‖ salt` [VERIFY] V19, `bundleId` khớp cấu hình). Không tin raw provider id do client gửi. Adapter mới qua `IIdentityProviderAdapter`. `[MẶC ĐỊNH — chờ owner chốt, TBD-03]` cho 24 h và 10 phút. | P0.5 | ACC-01 (provider); ACC-29 |
| IDN-04 | sửa | Mapping key: `guest:{localId}`, `pgs:{playerId}` (id PGS trả về cho app này; `gamePlayerId`/`originalPlayerId` lưu trong value để đối chiếu), `gamecenter:{teamPlayerId}` (ổn định qua cài lại và xuyên game cùng team; `gamePlayerId` lưu trong value). Không cần app scope trong key vì một game/deployment và scope `g_<gameId>`. | P0.5 | ACC-03; Review |
| IDN-05 | sửa | Liên kết identity yêu cầu phiên player hợp lệ và chứng minh sở hữu identity mới qua adapter (IDN-03). Mapping duy nhất; race xuyên instance giải quyết bằng Insert mapping trong transaction của IDN-01 (chỉ một transaction commit). | P0.5 | ACC-03 |
| IDN-08 | sửa | Player session = opaque token 256-bit; `sessions` document theo hash token `{playerId, epoch, createdAt, lastTouchAt}`, TTL sliding 30 ngày / absolute 90 ngày ([VERIFY] V6), gia hạn lazy (`Touch` khi lần trước cách > 24 h). **Một session/player**: mỗi login tăng `sessionEpoch` trong `pa_profile` (transaction, IPlayerAggregateWriter) và session mới mang epoch mới; session cũ không hợp lệ vì `epoch < profile.sessionEpoch` (xoá best-effort, TTL dọn phần còn lại). Cache session và `pa_profile` in-memory ≤ 60 s per instance → thu hồi/takeover có hiệu lực ≤ 60 s trên mọi instance; trong cửa sổ đó hai phiên có thể cùng sống — chấp nhận `[MẶC ĐỊNH — chờ owner chốt, TBD-04a]`. Credential/token không ghi log. `[MẶC ĐỊNH, TBD-14]` cho TTL. | P0 | ACC-20 |
| IDN-10 | sửa | Token truyền trong `Authorization: Bearer`, không trong body. Refresh = re-login bằng credential; không refresh token riêng ở P0. **Mô hình session: single session + takeover** (TBD-04a chốt): login mới thu hồi phiên cũ; request bằng phiên cũ trả 401 `SESSION_REPLACED` (`retryable = false`); thứ tự kiểm tra: `deleted` → `ACCOUNT_DELETED`; ban login → `ACCOUNT_SUSPENDED`; epoch cũ → `SESSION_REPLACED`; token lạ/hết hạn → `UNAUTHENTICATED`. SDK không tự re-login khi nhận `SESSION_REPLACED` (SDK-02). | P0 | ACC-20 |
| IDN-11 | sửa | Login bằng provider identity (PGS/Game Center) đã map phải trả đúng player đã map, độc lập với local ID/guest credential của thiết bị (kể cả sau cài lại). Nếu thiết bị đang có session guest chưa link với player khác, trả `SWITCH_ACCOUNT`; không merge. | P0.5 | ACC-29 |
| BAN-01 | sửa | `pa_profile` có trường `ban` `{reason, until, scope, actor}`; kiểm tra tại login và trước mọi command trong scope (IDN-09). Ban scope `login` tăng `sessionEpoch` → session hiện có bị thu hồi ≤ 60 s, mã `ACCOUNT_SUSPENDED` có ưu tiên trước `SESSION_REPLACED`; ban scope command chỉ kiểm tại command. Phase như cũ. | P0 (trường, kiểm tra, mã); P0.5 (GM-07) | ACC-31 |
| PRIV-01 | sửa | Command xoá tài khoản do player khởi tạo: transaction {Set `pa_profile.deleted = {requestedAt, purgeAfter}`, `sessionEpoch++`, Insert receipt}; xoá/ẩn identity mapping; grace period 30 ngày (GM `player.read` xem, `player.ban` hủy); sau đó job (lease JOB-01) purge **mọi** `pa_*` document của player qua transaction runner và ẩn danh `playerId` trong receipt/ledger/audit (ghi đè trường). `[MẶC ĐỊNH, TBD-04c]` | P0.5 | ACC-37 |

### I.4. PLY / ECO / CMD / 8.4 / CFG / IAP

| ID | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| PLY-02 | sửa | Login/snapshot đọc dữ liệu đã commit từ active node (KV get song song mọi aggregate `ClientVisible`, ngoài transaction); consistency: nhất quán nội tại per aggregate tại `rev` của nó, không chéo aggregate (API-10); ghi PCR. | P0 | ACC-01; ACC-34; PCR |
| PLY-03 | sửa | Mỗi aggregate có `schemaVersion` riêng (DAT-02); game config có `configVersion` (CFG-01); receipt ghi `configVersion` và `schemaVersions` của các aggregate đã chạm. | P0 | ACC-35; ACC-50 |
| ECO-03 | sửa | Debit phải đọc số dư bằng `ICommandContext.GetAsync` **trong transaction** của command, kiểm tra và ghi trong cùng transaction; request đồng thời (kể cả nhiều instance) không được cùng tiêu một số dư — conflict do SDK transaction retry giải quyết, bên thua thấy số dư mới. Thiếu số dư → `INSUFFICIENT_FUNDS`, không ghi. | P0 | ACC-06 |
| ECO-04 | sửa | Resource delta, progression transition, claim marker và receipt của một command commit **nguyên tử trong một Couchbase transaction** (DAT-04(a)); command chạm tối đa 4 aggregate (DAT-09). Không có partial success nhìn thấy được bởi snapshot hay client. | P0 | ACC-07 |
| ECO-05 | sửa | Chỉ trả thành công sau khi transaction commit với durability ghi PCR (DAT-05; mặc định `Majority`). Timeout/`CommitAmbiguous` không đồng nghĩa chưa chạy (CMD-04). | P0 | ACC-07; ACC-16 |
| ECO-06 | sửa | Mọi delta resource có ledger entry. Entry sinh trong **cùng transaction** với state dưới dạng outbox entry của receipt document (DAT-04(b)); collection `ledger` là projection at-least-once, idempotent theo `entryId`, relay inline sau commit + sweeper. GM adjustment là command có reason, receipt, audit (GM-03). | P0 | ACC-06; ACC-45 |
| ECO-08 | sửa + chuyển phase | Mọi grant từ nguồn ngoài (IAP, ads, đối tác) đi qua ECO-01 với `sourceType` và `externalReference` duy nhất; chống trùng bằng claim marker `extref::{sourceType}::{externalReference}` **Insert trong cùng transaction** với grant và receipt (DAT-04(a)); tồn tại → `ALREADY_GRANTED`, không ghi. | **P0** | ACC-46 |
| CMD-01 | sửa | Idempotency key = (`gameId`, `environmentId`, `actorId`, operation, `requestId`) do server dựng; `receiptId` = hash xác định của (`actorId`, operation, `requestId`); game/env là scope nên không nằm trong key document. | P0 | ACC-05; Unit |
| CMD-03 | sửa | Receipt gắn nguyên tử với thay đổi trạng thái bằng **một cơ chế duy nhất**: receipt document `rcpt::{receiptId}` được `Insert` trong cùng Couchbase transaction với mọi aggregate/claim marker của command (DAT-04(a)). Không có receipt nhúng, không có tầng riêng cho command đa aggregate. Hai request cùng key chạy đồng thời trên hai instance: Insert receipt xung đột → transaction thua chạy lại lambda, thấy receipt đã commit → trả cùng kết quả (hoặc thực hiện nếu transaction kia rollback). GM batch/job chạy dài dùng lease JOB-01 với progress idempotent, không qua pipeline command. Không có đường ghi receipt thành công mà state chưa lưu. | P0 | ACC-04; ACC-07; ACC-44 |
| CMD-04 | sửa | Trạng thái commit chưa rõ (KV timeout, `TransactionCommitAmbiguousException`) → 503 `COMMIT_UNKNOWN_RETRY` + `retryAfter`; client retry cùng `requestId`; server chạy lại pipeline: `ctx.GetOptional(rcpt::{receiptId})` (đọc transaction-aware) có → trả **cùng `resultCode`, `receiptId`, `aggregates[].newRev`, `resultCompact`** kèm các aggregate đó ở trạng thái hiện hành (`full = true`, `rev` ≥ `newRev`); không có → thực hiện như mới (Insert receipt bảo đảm không hai hiệu ứng). Handler không được tạo side effect ngoài `ICommandContext` vì lambda có thể chạy lại. | P0 | ACC-04; ACC-07 |
| CMD-05 | sửa | Concurrency: document trong transaction (mọi `pa_*`, `receipts`, claim marker) được bảo vệ bởi transaction conflict detection + retry nội bộ của SDK trong `TransactionExpiration` (10 s `[MẶC ĐỊNH, TBD-19]`); hết hạn không ambiguous → 409 `CONCURRENT_UPDATE` (`retryable = true`); ambiguous → CMD-04. Document ngoài transaction (`sessions`, `identities`, `gate`, `gm_staff`, `giftcodes`, counter) ghi bằng CAS/Insert, retry ≤ 5 lần có jitter rồi 409. Repository không expose `Upsert`; **mọi ghi vào `pa_*` và `receipts` chỉ qua `ICommandRunner`/`IPlayerAggregateWriter`** (arch test). Khoá RAM một instance không đủ. | P0 | ACC-06; arch test |
| CMD-06 | sửa | Ordering per player: command của cùng một player serialize per instance (`PartitionedRateLimiter` theo `playerId` của aggregate mục tiêu — với GM command là player đích; concurrency 1, queue 2) và xuyên instance bằng transaction conflict; player khác nhau đồng thời; không khoá toàn hệ thống. Command chạm aggregate của nhiều player: ngoài phạm vi P0. `[MẶC ĐỊNH, TBD-07]` | P0 | ACC-06; Load |
| CMD-07 | sửa | Replay window = **24 h** theo `committedAtUtc` của receipt, thực thi khi tra cứu (cũ hơn → coi như không có) `[MẶC ĐỊNH — chờ owner chốt, TBD-13]`; ghi PCR `platform.md`. Trong window: cùng key trả cùng kết quả (CMD-02). Ngoài window: xử lý như request mới và bị invariant từ chối nếu đã thực hiện (`ALREADY_CLAIMED`, `NO_ACTIVE_BATTLE`…). Mọi hiệu ứng "một lần" có state bền vững riêng (claim marker, `activeBattle`), không dựa vào receipt. Không còn vế "64 command" vì không có ring. | P0 | ACC-42; PCR |
| CMD-08 | sửa | Ba bản ghi, vòng đời khác nhau (14.1): **receipt** (chứa idempotency entry) — document trong `receipts`, không dùng Couchbase expiry, purge bằng job sau 180 ngày; **ledger entry** và **audit record** — projection từ outbox của receipt, không expiry, không xoá. Retention theo TBD-08. `[MẶC ĐỊNH, TBD-08]` | P0 | ACC-23; ACC-42; Review |
| CMD-09 | sửa | Tra cứu receipt: (a) gửi lại command cùng `requestId` trong window → CMD-04; (b) `GetReceipt(receiptId)` KV get `rcpt::{receiptId}` cho actor sở hữu và GM `player.read`, trả cả `outbox[]` (ledger/audit đã sinh). `playerId`/`actorId` là trường có index (ẩn danh PRIV-01 = ghi đè trường). Ledger key `ledger::{entryId}`, audit `gm_audit::{entryId}`. | P0 | ACC-04; ACC-45 |
| 8.4 (đoạn 1) | sửa | Battle là aggregate riêng `pa_battle` với **một** `activeBattle` `{battleId, startedAtUtc, expiresAtUtc, configVersion, seed, mode}` (TBD-02 chốt: một thời điểm một battle). `StartBattle(requestId)` (Touches: `battle`): kiểm eligibility theo `IBattleStartPolicy`; nếu đang có `activeBattle` chưa hết hạn, policy `OnActiveBattle` mặc định **Replace** (battle cũ ghi `stats.abandoned++`, không thưởng) `[MẶC ĐỊNH — chờ owner chốt, TBD-02]`, policy `Reject` trả `BATTLE_IN_PROGRESS`; ghi `activeBattle` mới với `expiresAtUtc = now + 24 h` `[MẶC ĐỊNH, TBD-02]`, trả `battleId`. `EndBattle(battleId, evidence, requestId)` (Touches: `battle`, `progression`, `wallet`, + `inventory` nếu có drop): không có `activeBattle` hoặc `battleId` khác → `NO_ACTIVE_BATTLE`; quá `expiresAtUtc` theo `IClock` → `BATTLE_EXPIRED` và xoá `activeBattle`; hợp lệ → `IBattleResultPolicy` kiểm evidence, tính reward theo `configVersion` đã ghi trong `activeBattle`, cập nhật progression/resource, xoá `activeBattle`, outbox ledger — **một transaction** (DAT-04(a)). Hai `EndBattle` khác `requestId` cùng `battleId` không thể cùng thành công vì transaction thứ hai đọc `activeBattle = null`. Evidence lớn (replay/input log) là trường aggregate riêng `ClientVisible = false` của game, không thuộc P0. | P0 | ACC-04; ACC-53 |
| CFG-01 | sửa | Game config (reward, chi phí, cooldown, Resource Catalog, IAP catalog, shop…) là **tập file** trong thư mục `config/` của artifact deploy: `manifest.json` `{label, files[], clientVisible[]}` + file JSON (game có thể đăng ký `IConfigFileLoader` cho định dạng khác); overlay `config/base/` + `config/env/<deployment environment>/`. `configVersion = "{label}+{sha256(nội dung hiệu lực)[0..12]}"` tính một lần khi startup; thiếu/không parse → startup fail (ARC-06). Reload = restart (rolling deploy); không file-watch ở Staging/Production; Development **nên** hỗ trợ file-watch per instance. Trong rolling deploy hai `configVersion` có thể cùng phục vụ; mỗi receipt ghi version của instance xử lý (CFG-02); metric `config_version_info{version}` để thấy skew. Không lưu config trong Couchbase; không GM page config. `[MẶC ĐỊNH — chờ owner chốt, TBD-17]` | P0 | ACC-35 |
| CFG-02 | giữ | (không đổi) | P0 | ACC-35; ACC-23 |
| CFG-03 | sửa | Client nhận `configVersion` trong login snapshot và mọi response; tải file trong `clientVisible[]` qua endpoint Public `GET /v1/config/{configVersion}/{file}` (allowlist SEC-09) với cache header immutable — CDN phía trước là cấu hình deployment, không phải contract; SDK phát hiện đổi version và tải lại. Environment override nằm trong overlay của CFG-01. | P0 | ACC-35; ACC-18 |
| IAP-01 | sửa + chuyển phase | Module IAP xác minh server-to-server: **Google** qua Play Developer API `purchases.products.get(packageName, productId, purchaseToken)` bằng service account (OPS-08), hợp lệ khi `purchaseState = 0` [VERIFY] V20; **Apple** qua xác minh chữ ký JWS (`jwsRepresentation` StoreKit 2) với chuỗi x5c tới Apple root, kiểm `bundleId`, `environment` khớp deployment environment, `productId` [VERIFY] V21 `[MẶC ĐỊNH — chờ owner chốt, TBD-01]`. Không tin payload client tự khai; sản phẩm phải có trong catalog `iap` của config (CFG-01). Verify chạy **trước** transaction (outbound idempotent), không trong lambda. Adapter qua `IStoreReceiptVerifier`; CI dùng verifier giả. | **P0** | ACC-47 |
| IAP-02 | sửa + chuyển phase | Grant idempotent theo `externalReference`: Google = `purchaseToken` (`orderId` lưu trong ledger, có thể rỗng với test purchase), Apple = `transactionId` `[MẶC ĐỊNH, TBD-01]`; claim marker `extref::iap_{google|apple}::{externalReference}` Insert trong cùng transaction với grant và receipt (ECO-08); replay → `ALREADY_GRANTED`; receipt giả/không hợp lệ → `INVALID_RECEIPT`; grant qua Economy, ledger `sourceType = iap`. Acknowledge/consume do client thực hiện sau khi nhận thành công; server không gọi acknowledge ở P0 `[MẶC ĐỊNH, TBD-01]`. | **P0** | ACC-47 |
| IAP-03 | sửa + chuyển phase | Nhận S2S notification (Google RTDN/voided purchases; Apple App Store Server Notifications v2) và áp dụng refund/revoke bằng command hệ thống có receipt/ledger (`sourceType = iap_refund`), idempotent theo `notificationId`/`transactionId`; không âm hoá số dư ngoài rule PCR của IAP. | **P0.5** | Integration; ACC-47 |

### I.5. SampleGame (mục mới 8.7 "Sample commands", SMP-01..05)

| ID | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| SMP-01 | thêm | `SampleGame` dùng Login của framework (guest P0; PGS/Game Center P0.5): trả session token, snapshot mọi aggregate `ClientVisible` (`profile`, `progression`, `wallet`, `inventory`, `battle`) với `rev`, `configVersion`, `serverTimeUtc`. | P0 | ACC-01; ACC-34 |
| SMP-02 | thêm | `StartBattle` theo 8.4: Touches `battle`; policy mặc định Replace; kiểm eligibility (stage mở theo `progression`). | P0 | ACC-53 |
| SMP-03 | thêm | `EndBattle` theo 8.4: Touches `battle`, `progression`, `wallet`; reward theo bảng `battle` trong config tại `configVersion` của `activeBattle`; evidence eligibility-only. | P0 | ACC-04; ACC-07; ACC-53 |
| SMP-04 | thêm | `IAPurchase(store, productId, receiptPayload, requestId)`: verify qua `IStoreReceiptVerifier` (IAP-01) trước transaction; transaction: claim `extref`, grant theo catalog `iap` (`wallet`/`inventory`), ledger `sourceType = iap`, receipt (IAP-02). CI: verifier giả trả hợp lệ/đã dùng/giả. | P0 | ACC-46; ACC-47; ACC-53 |
| SMP-05 | thêm | `BuyVirtualItem(shopItemId, quantity, requestId)`: Touches `wallet`, `inventory`; giá và grant từ catalog `shop`; debit kiểm số dư (`INSUFFICIENT_FUNDS`), credit kiểm `maxQuantity` (ECO-02) — một transaction; hai ledger entry (debit, credit) cùng `receiptId`; thiếu tiền → không đổi state, không ledger. | P0 | ACC-06; ACC-21; ACC-53 |

### I.6. GFT / GM / SEC / LDB / JOB / PERF

| ID | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| GFT-03 | sửa | Redeem = (1) reserve quota bằng atomic counter (GFT-04) ngoài transaction; (2) **một transaction** {Insert `gclaim::{code}::{playerId}`, grant vào aggregate, Insert receipt với outbox ledger}; (3) transaction thất bại (không ambiguous) → `Decrement` counter best-effort; ambiguous → CMD-04 (retry tra receipt). Marker đã tồn tại → `ALREADY_CLAIMED`. Không có trạng thái báo nhận thành công mà thiếu quà. Reaper under-fill định kỳ: counter − số marker của code > ngưỡng → hoàn trả chênh lệch (job lease, P0.5). Tham số ghi PCR Giftcode. | P0.5 | ACC-08; ACC-07 |
| GFT-04 | sửa | Idempotency theo CMD-01..09. Quota tổng dùng atomic counter reserve-then-confirm (`Increment` server-side; `n > quota` → `Decrement` + `GIFTCODE_QUOTA_EXCEEDED`) — counter op không nằm trong transaction [VERIFY] V16; không bao giờ vượt quota; chấp nhận under-fill tạm thời (reaper GFT-03). Once-per-player bằng claim marker Insert trong transaction (DAT-04(a)). `[MẶC ĐỊNH, TBD-07b]` | P0.5 | ACC-08 |
| GFT-05 | sửa | Receipt claim lưu `rewardSnapshot` và `giftcodeConfigVersion` trong `resultCompact`/outbox; receipt purge theo TBD-08 (180 ngày), không theo window 24 h. Sửa/vô hiệu code không đổi receipt cũ; tra qua CMD-09. | P0.5 | ACC-23 |
| GM-02 | sửa | Tìm player theo `playerId`, mapping key (`guest:…`, `pgs:…`, `gamecenter:…`) hoặc display name; xem từng aggregate với `rev` và kích thước, receipt, ledger, ban/deleted; PII chỉ với `player.read.pii`. | P0 | ACC-45; ACC-10 |
| GM-03 | sửa | Điều chỉnh resource qua command Economy (Touches `wallet`/`inventory` của player đích) với validation, reason bắt buộc, idempotency CMD-01..09, receipt (actor GM), ledger và audit trong outbox của receipt (SEC-14); `progression.edit` tách khỏi `resource.adjust`. Player nhận qua resync aggregate. | P0 | ACC-27; ACC-45 |
| SEC-09 | sửa | Allowlist Public ghi PCR: login, provider callback, `/health/live`, `/health/ready`, `/v1/time`, `/v1/config/{configVersion}/{file}`. | P0 | ACC-12; PCR |
| SEC-14 | sửa | Mọi mutation actor = GM (kể cả quyền staff, giftcode definition, gate) đi qua pipeline command → audit record là outbox entry trong receipt của cùng transaction (DAT-04(a)/(b)); projection `gm_audit` qua relay/sweeper. 7 trường như cũ. Không có mutation GM nào ghi document trực tiếp ngoài pipeline. | P0 | ACC-27 |
| LDB-01 | sửa (một câu) | "...Backend A ghi score qua relay/sweeper của **outbox trong receipt** (ngoài request path)..." (phần còn lại giữ) | P1 | ACC-14; PCR |
| LDB-02 | sửa | Điểm lấy từ outbox entry `kind = score` trong receipt của command gameplay đã commit (ví dụ `EndBattle → score +x`) hoặc validator của game; không có API client tự khai score. | P1 | ACC-14; arch test |
| JOB-02 | sửa | Tác vụ liên module không được mất sau restart dùng outbox entry nằm trong document được commit cùng state (receipt với command; progress document với job P1) + relay inline sau commit + sweeper nền quét partial index `receipts.outboxPending = true`; at-least-once, consumer idempotent theo `entryId`. In-memory event chỉ khi mất được chấp nhận (PCR). Broker là adapter sau. | P0 (outbox ledger/audit); P1 (job consumer) | ACC-06; ACC-15 |
| PERF-02 | sửa | Truyền `CancellationToken` tới trước bước commit; bước commit = `Transactions.RunAsync` với `TransactionExpiration` = 10 s và `KvDurableTimeout` = 5 s set tường minh `[MẶC ĐỊNH, TBD-19]`, **không** dùng token request; client disconnect sau commit không mất receipt; vượt → CMD-04. | P0 | ACC-28; Review |
| PERF-07 | sửa | Hot path (login, command, snapshot) chỉ dùng KV và KV transaction với key xác định; N1QL chỉ cho GM search/list, báo cáo, job, sweeper outbox (partial index `outboxPending = true`) và materialize leaderboard; `scan_consistency` khai báo, covering index provisioning, không `SELECT *` trên `pa_*`. | P0 | arch test; Integration |
| PERF-08 | sửa | Cache đọc chung (session, `pa_profile` cho epoch/ban, permission, gate) có TTL ghi PCR (≤ 60 s; gate ≤ 10 s). Config là in-memory bất biến theo `configVersion` (CFG-01), không cache TTL. Không dùng cache để quyết định debit: số dư đọc trong transaction. | P0.5 | ACC-06; Review |

### I.7. DAT / 14.1 / OBS / OPS / MIG / SDK / TST

| ID | Loại | Văn bản mới | Phase | Kiểm bằng |
| --- | --- | --- | --- | --- |
| DAT-01 | sửa | Environment = cluster riêng Production, bucket riêng Dev/Staging; game = scope `g_<gameId>` (một game/deployment, TBD-11); module sở hữu collection: `pa_<aggregateId>` cho mỗi player aggregate đã đăng ký (P0 `SampleGame`: `pa_profile`, `pa_progression`, `pa_wallet`, `pa_inventory`, `pa_battle`), `identities`, `sessions`, `receipts`, `ledger`, `giftcodes`, `giftcode_claims`, `extref_claims`, `gate`, `gm_audit`, `gm_staff`, `gm_sessions`, `leases`, `txn_meta` (transaction metadata [VERIFY] V17); P1: `events`, `boards`, `scores`. Bỏ `config`, `command_pending`, `outbox_sweep`. `receipts` không `maxTTL`. Document mang `gameId`/`environmentId` làm defense-in-depth. | P0 | arch test; ACC-10; Review |
| DAT-03 | sửa | Couchbase expiry chỉ cho `sessions`, `gm_sessions` và cache document; `pa_*`, `receipts`, `ledger`, `gm_audit`, claim marker, `txn_meta` **không** expiry — retention là job tường minh (CMD-08/TBD-08). `maxTTL` làm lưới an toàn cho collection có expiry. [VERIFY] V6. | P0 | ACC-23; Review |
| DAT-04 | sửa | Mọi command thay đổi trạng thái chạy trong **một Couchbase transaction (a)** gồm: aggregate chạm (≤ 4, khai trong `CommandDescriptor.Touches`), receipt document (Insert), claim marker (Insert, ≤ 2); durability `Majority`; `TransactionExpiration` 10 s; `CommitAmbiguous` xử lý bằng tra receipt (CMD-04); handler thuần qua `ICommandContext`, không side effect ngoài (HTTP verify chạy trước transaction). **(b) Outbox trong receipt**: ledger/audit/score là entry trong receipt, relay inline + sweeper (JOB-02). **(c) Ngoài transaction** chỉ còn: atomic counter quota (reserve-then-confirm, GFT-04) và job dài với lease + progress idempotent (JOB-01). Không còn saga claim-marker với pending/reaper; không còn "tầng B". Bảng command → Touches/Reads và cơ chế ghi PCR của module. Transaction retry không được tạo lại external side effect. `[MẶC ĐỊNH — chờ owner chốt, TBD-07]` cho (a) là cơ chế duy nhất kể cả command một aggregate. | P0 | ACC-07; ACC-44; PCR |
| DAT-05 | sửa | Durability ghi PCR. Mặc định: transaction command (mọi `pa_*`, `receipts`, claim marker) = `Majority`; `identities`, `sessions`, `gm_staff`, `gate`, `giftcodes`, `leases`, ledger/audit projection = `Majority`; score = `None`/`Majority` theo board. Đọc active không stale. Dev single-node: replica = 0, transactions với `Majority` phải chạy được [VERIFY] V4. `[MẶC ĐỊNH, TBD-07, TBD-20]` | P0 | ACC-07; PCR |
| DAT-07 | sửa | Provisioning collection/index từ `ModuleDescriptor` **và** `PlayerAggregateDescriptor` (một collection `pa_<id>` per aggregate), `txn_meta`, partial index `receipts(outboxPending)`; chạy khi startup ở Dev, lệnh ops ở Production; RBAC least-privilege theo scope/collection [VERIFY] V3; backup/restore có tài liệu. | P0 (provisioning); P0.5 (backup/restore) | ACC-19; Runbook drill |
| DAT-08 | sửa | Khôi phục phải gồm **mọi** `pa_*`, `identities`, `receipts`, `ledger`, `gm_audit`, claim marker, `txn_meta` cùng điểm thời gian (transaction đang dở tại điểm backup được SDK cleanup resolve sau restore); đánh giá session epoch và receipt cũ hơn state sau rollback. | P0.5 | ACC-19 |
| DAT-09 | sửa | **Player aggregates.** Player state chính thức chia thành nhiều aggregate do game đăng ký qua `PlayerAggregateDescriptor {aggregateId, collection = pa_<id>, stateType, schemaVersion, clientVisible, warn 32 KiB, hard 64 KiB}`; framework bắt buộc `profile` (`sessionEpoch`, `ban`, `deleted`, `displayName`, `identities[]`). Mỗi aggregate = một document key `playerId`, `rev` đơn, `schemaVersion` + upgrader riêng; **document chưa tồn tại = trạng thái mặc định** (`IAggregateFactory.CreateDefault`, `rev = 0`), Insert ở lần ghi đầu. Tối đa 16 aggregate/player; command khai `Touches` ≤ 4 và `Reads`; vi phạm → startup fail (ARC-07). Command commit bằng **một transaction** (DAT-04(a)); `rev++` cho mỗi aggregate trong `Touches` thực sự đổi; vượt 64 KiB → `AGGREGATE_TOO_LARGE`, transaction rollback, state không đổi; metric `aggregate_size_bytes{aggregateId}` cảnh báo ≥ 32 KiB. **Mọi ghi vào `pa_*` chỉ qua `ICommandRunner` (command) hoặc `IPlayerAggregateWriter` (login epoch, purge)** — không có ghi CAS thường lên `pa_*` để tránh trộn ghi với transaction [VERIFY] V15; đọc snapshot ngoài transaction được phép. **Chi phí chuẩn một command** (đo ở ACC-16): 1 transaction (N aggregate + 1 receipt + k marker) ≈ 2(N+1+k)+3 KV op `Majority` (ước 10–30 ms server-side cho N = 2–3) + relay inline best-effort 0..n Insert ledger, 0..1 Insert `gm_audit`, 1 MutateIn receipt. Fast path "một aggregate + receipt nhúng" không thuộc P0; chỉ xem xét ở P1 nếu ACC-16 không đạt và V15 cho phép. Aggregate ngoài player (`giftcodes`, `gate`, `gm_staff`, P1 `events`, `boards`) có `rev` đơn và được GM sửa qua pipeline command (transaction với receipt), không có outbox nhúng. `[MẶC ĐỊNH — chờ owner chốt, TBD-07]` cho 32/64 KiB, 16, 4, 2. | P0 | ACC-04; ACC-07; ACC-39 |
| DAT-10 | sửa | Trường nền tảng của mọi document nghiệp vụ: entity ID, `gameId`, `environmentId`, `schemaVersion`, `rev` (đơn; với player aggregate là `rev` của section tương ứng), `createdAtUtc`, `updatedAtUtc`. Không có global revision. | P0 | arch test; Review |
| 14.1 (bảng) | sửa | Ba hàng: **Receipt** (chứa idempotency entry) — sinh bởi pipeline command trong cùng transaction với state (CMD-03); bắt buộc cho mọi command có `requestId` (Player hoặc GM); nội dung theo glossary Receipt; retention: không expiry, purge 180 ngày `[MẶC ĐỊNH, TBD-08]`; người đọc: actor sở hữu (CMD-09), GM `player.read`. **Ledger entry** — Economy, là outbox entry trong receipt, projection `ledger` qua relay/sweeper (ECO-06); nội dung như cũ; không expiry, không xoá. **Audit record** — pipeline GM, là outbox entry trong receipt (SEC-14), projection `gm_audit`; 7 trường; không expiry, không xoá. Bỏ hàng "Idempotency entry" riêng. | — | — |
| OBS-01 | sửa | Thêm: `txn_attempts_total`, `txn_retries_total`, `txn_ambiguous_total`, `txn_expired_total`, `txn_duration_seconds` (histogram theo operation), `txn_cleanup_lost_total`, `aggregate_size_bytes{aggregateId}`, `receipt_size_bytes`, `outbox_relay_inline_failures_total`, `outbox_pending_age_seconds`, `config_version_info{version}`, `session_replaced_total`. | P0 | ACC-41; ACC-16 |
| OPS-05 | sửa | Job phối hợp bằng lease (JOB-01). Sweeper outbox (P0): quét `receipts` có `outboxPending = true` qua partial index mỗi 30 s `[MẶC ĐỊNH, TBD-19]`, relay idempotent, đặt `outboxPending = false`. Lost-transaction cleanup của Couchbase SDK bật trên mọi instance (metric `txn_cleanup_lost_total`). | P0 (sweeper); P1 (job) | ACC-15; ACC-27 |
| OPS-07 | sửa | Thêm runbook: transaction ambiguous/lost cleanup (đọc ATR, không sửa tay document đang staged), config rollout skew (hai `configVersion` cùng phục vụ), takeover session hàng loạt. | P0.5 | ACC-19; Runbook drill |
| MIG-01, MIG-02, mục 15.2, deliverable 9 | xoá | — (TBD-18 = Không) | — | — |
| SDK-02 | sửa | ...xử lý `retryable`/`retryAfter`, `COMMIT_UNKNOWN_RETRY` (retry cùng bytes), `CONCURRENT_UPDATE`, `MAINTENANCE`, `UPDATE_REQUIRED`, `ACCOUNT_SUSPENDED`, `ACCOUNT_DELETED`, `SWITCH_ACCOUNT`, **`SESSION_REPLACED`** (phát sự kiện cho game, xoá token cục bộ, **không** tự re-login). Header `clientVersion`/`platform`/`buildId` như cũ. | P0 | ACC-04; ACC-18; ACC-20; ACC-30 |
| SDK-03 | sửa | SDK giữ `rev` per aggregate (section), áp dụng `SectionUpdate` theo API-10, refetch aggregate khi `baseRev` lệch hoặc `newRev` không liên tiếp; offset `serverTimeUtc`; theo dõi `configVersion` và tải file `clientVisible` qua CFG-03; map `ErrorCode`. | P0 | ACC-34; ACC-43 |
| TST-01 | sửa | ...Integration test trên Couchbase thật trong container (Testcontainers [VERIFY] V11: scope/collection, **transactions bật, `txn_meta` collection**) cho ACC-04..07, ACC-20, ACC-21, ACC-39, ACC-42, ACC-44, ACC-46, ACC-47 (verifier giả), ACC-50, ACC-53; tiêm lỗi/ambiguous vào transaction bằng hook của runner. | P0 | CI; ACC-04..07 |

### I.8. ACC

| ID | Loại | Văn bản mới (Kịch bản → Kết quả) | Phase | Kiểm cho |
| --- | --- | --- | --- | --- |
| ACC-01 | sửa | Login guest (P0) / PGS, Game Center (P0.5), logout, login lại → đúng player; snapshot mọi aggregate `ClientVisible` với `rev` (aggregate chưa có → mặc định, `rev = 0`); credential sai 401. | P0 (guest); P0.5 (provider) | IDN-01, 02, 03, 07, PLY-02, SMP-01 |
| ACC-03 | sửa | Link đồng thời cùng PGS/Game Center identity từ hai player trên hai instance → một transaction commit; bên thua 409 `IDENTITY_CONFLICT`; không chuyển quyền. | P0.5 | IDN-04, 05, 06 |
| ACC-04 | sửa | Gửi cùng command (cùng `requestId`, cùng bytes) nhiều lần, kể cả sau mất response, sau `COMMIT_UNKNOWN_RETRY` tiêm vào commit, và đồng thời trên hai instance → đúng một receipt, một hiệu ứng; retry trả cùng `resultCode`/`receiptId`/`aggregates[].newRev` kèm aggregate hiện hành `full = true`. | P0 | API-07, CMD-02, 03, 04, 09, DAT-04, DAT-09, PLY-01, 04, 05, PERF-06, SDK-02, TST-01 |
| ACC-06 | sửa | Hai BuyVirtualItem đồng thời cùng player trên hai instance với số dư chỉ đủ một → một thành công, một `INSUFFICIENT_FUNDS`; ledger projection khớp số dư `wallet` và `inventory`; player khác nhau không chặn nhau. | P0 | ECO-01, 03, 06, CMD-05, 06, PERF-08, 10, JOB-02, TST-02, SMP-05 |
| ACC-07 | sửa | Tiêm lỗi/timeout giữa bước tính và commit; tiêm `CommitAmbiguous` vào transaction EndBattle (3 aggregate) → không partial success: cả ba aggregate + receipt hoặc trước hoặc sau; xác định được qua CMD-04. | P0 | ECO-04, 05, CMD-03, 04, DAT-04, 05, 06, 09, GFT-03 (P0.5), SMP-03 |
| ACC-20 | sửa | Hết idle/absolute TTL; `sessionEpoch` tăng do ban; **login từ thiết bị B khi A đang có phiên**, trên 2 instance → 401 sau expiry; A nhận 401 `SESSION_REPLACED` ≤ 60 s trên cả hai instance; SDK của A không tự re-login; token không trong body. | P0 | IDN-08, IDN-10, PERF-10, TST-02, SDK-02 |
| ACC-29 | sửa | Cài lại app, login PGS/Game Center đã link; thiết bị có guest session khác chưa link → đúng player cũ; trường hợp hai `SWITCH_ACCOUNT`. | P0.5 | IDN-03, IDN-11 |
| ACC-35 | sửa | Deploy config mới (label/hash đổi) bằng rolling deploy khi có command đang chạy; xem receipt cũ/mới; client đang chạy → receipt cũ giữ `configVersion` cũ, receipt mới ghi version của instance xử lý; sau rollout mọi instance cùng version (metric); client nhận version mới và tải lại file `clientVisible`; config thiếu/hỏng → instance không ready. | P0 | CFG-01, 02, 03, PLY-03, PLY-05 |
| ACC-39 | sửa | Command làm một aggregate vượt 32 KiB rồi 64 KiB → metric cảnh báo ở 32 KiB; `AGGREGATE_TOO_LARGE` ở 64 KiB, transaction rollback, không aggregate nào trong Touches đổi, không receipt. | P0 | DAT-09 |
| ACC-44 | sửa + chuyển phase | Hai instance nhận cùng `requestId` cùng lúc; một transaction bị tiêm treo rồi hết `TransactionExpiration` → đúng một receipt, một hiệu ứng; bên thua trả cùng kết quả hoặc (nếu bên kia rollback) thực hiện đúng một lần; không bao giờ 2 ledger entry. | **P0** | CMD-03, CMD-05, DAT-04 |
| ACC-45 | sửa | GM lookup theo `playerId`, mapping key (`pgs:`/`gamecenter:`/`guest:`), display name → hiển thị từng aggregate với `rev`/kích thước, receipt, ledger; PII theo quyền; adjust tạo receipt + ledger + audit. | P0 | GM-01, 02, 03, ECO-06, CMD-09 |
| ACC-46 | chuyển phase | (giữ nội dung) | **P0** | ECO-08 |
| ACC-47 | sửa + chuyển phase | Gửi lại receipt IAP đã dùng; receipt giả; receipt sandbox khi deployment Production → `ALREADY_GRANTED` / `INVALID_RECEIPT` / `INVALID_RECEIPT`; không grant lại. P0 với verifier giả trong CI; P0.5 với sandbox Google/Apple thật (thủ công). | P0 (giả); P0.5 (sandbox) | IAP-01, IAP-02, SMP-04 |
| ACC-53 | thêm | `SampleGame` 5 command end-to-end trên 2 instance: StartBattle khi đang có battle → battle cũ abandoned (policy Replace) hoặc `BATTLE_IN_PROGRESS` (policy Reject); EndBattle với `battleId` sai → `NO_ACTIVE_BATTLE`; dịch `IClock` > 24 h → `BATTLE_EXPIRED` và `activeBattle` bị xoá; EndBattle hợp lệ → `battle`/`progression`/`wallet` đổi cùng lúc, 3 `SectionUpdate`; BuyVirtualItem đủ/thiếu tiền; IAPurchase hợp lệ/replay. | P0 | SMP-02..05, 8.4 |

### I.9. Mục 20 (TBD) — hàng thay đổi

| TBD | Nội dung mới | Mặc định nếu chưa chốt | Trạng thái |
| --- | --- | --- | --- |
| TBD-01 | Danh sách command chuẩn (chốt: Login, StartBattle, EndBattle, IAPurchase, BuyVirtualItem); chi tiết IAP (khoá `externalReference`, verify Apple offline JWS, server không acknowledge) | §E | **Chốt** (danh sách, 2026-10-22); Mặc định (chi tiết IAP) |
| TBD-02 | Một battle/thời điểm (chốt); policy khi StartBattle lúc đang có battle; hạn logic | Replace; 24 h; evidence eligibility-only | Chốt (một battle); Mặc định (policy, 24 h) |
| TBD-03 | Provider (chốt: PGS v2 + Game Center); tham số verify | `pgs:{playerId}`, `gamecenter:{teamPlayerId}`, cert cache 24 h, skew 10 phút | Chốt (provider); Mặc định (tham số) |
| TBD-04a | Single session + takeover (chốt); cửa sổ cache | Chấp nhận ≤ 60 s hai phiên; không strict check | Chốt (mô hình); Mặc định (cửa sổ) |
| TBD-06 | Workload/latency/topology — bảng 12.1 (không còn nguồn giá trị tạm) | Chưa có số | **Mở** |
| TBD-07 | Nhiều aggregate (chốt); cơ chế commit = transaction cho mọi command; giới hạn 32/64 KiB, 16 aggregate, 4 Touches, 2 marker | §A | Chốt (mô hình); Mặc định (cơ chế + số) |
| TBD-11 | Một game/deployment | D4 | **Chốt** (2026-10-22) |
| TBD-12 | Per-aggregate `rev`, full section, chỉ aggregate đổi | API-10 | Chốt (2026-10-22) |
| TBD-13 | Replay window 24 h; receipt ≤ 4 KiB, `resultCompact` ≤ 512 B; tra cứu resend + `GetReceipt` | CMD-07/09 | Mặc định (đổi từ min(64, 24 h)) |
| TBD-15 | Chủ sở hữu SDK; Unity LTS target | Backend sở hữu, client lead review; **Unity LTS mới nhất tại Sprint 0** `[MẶC ĐỊNH]` | Mặc định |
| TBD-17 | Config = file trong artifact (chốt); overlay env, reload restart, Dev file-watch | §F | Chốt (file); Mặc định (overlay/reload) |
| TBD-18 | Di dời PlayFab | Không | **Chốt: Không** (2026-10-22) |

Danh sách TBD Sprint 0 còn phải chốt mặc định: TBD-07 (cơ chế + số), TBD-13, TBD-04a (cửa sổ), TBD-02 (policy), TBD-17 (overlay/reload), TBD-15, TBD-20, TBD-22.

### I.10. Mục 21 (VERIFY) — thay đổi

| # | Loại | Fact | Ảnh hưởng |
| --- | --- | --- | --- |
| V5 | xoá | (MutateIn 16 path — không còn dùng) | — |
| V7 | sửa | Google.Protobuf ↔ **Unity LTS chọn ở TBD-15**: xung đột `System.Memory`…; `link.xml` với stripping High | API-09, SDK-04 |
| V12 | xoá | (Facebook Limited Login) | — |
| V13 | xoá | (kích thước state Sniper PVP) → thay bằng V22 | — |
| V15 | thêm | Couchbase transactions: hành vi khi document đang staged bị ghi non-transactional (lost update?) và khả năng nhìn thấy staged insert bằng KV get thường (shadow document/tombstone); xác nhận quy tắc "mọi ghi `pa_*` qua transaction" là đủ và cần | DAT-09, CMD-05 |
| V16 | thêm | Binary counter (`Increment/Decrement`) không dùng được trong transaction; giới hạn document binary trong transaction | GFT-04 |
| V17 | thêm | Cấu hình metadata collection cho ATR (`txn_meta`) trong SDK .NET; lost-cleanup xuyên instance; số document tối đa/transaction và kích thước staged | DAT-01, DAT-04 |
| V18 | thêm | PGS v2: endpoint đổi server auth code, scope cần thiết, `players/me` trả `playerId` scope game hay developer; trường `gamePlayerId`/`originalPlayerId` | IDN-03, IDN-04 |
| V19 | thêm | Game Center `fetchItems`: payload chữ ký dùng `teamPlayerID`; chuỗi chứng chỉ Apple; định dạng `timestamp` | IDN-03 |
| V20 | thêm | Play Developer API `purchases.products.get`: trường `purchaseState/orderId/purchaseType`, `orderId` rỗng với test purchase, quyền service account | IAP-01, IAP-02 |
| V21 | thêm | App Store: xác minh offline JWS `jwsRepresentation` (x5c tới Apple Root CA G3), trường `environment`, có cần gọi App Store Server API để chống replay xuyên app không | IAP-01, IAP-02 |
| V22 | thêm | Kích thước thực tế của từng aggregate `SampleGame` và ước lượng cho game đầu tiên (để chốt 32/64 KiB) | DAT-09, API-10 |

### I.11. Phụ lục B — dòng thay đổi

| Tham số | Giá trị khởi điểm | Yêu cầu | TBD |
| --- | --- | --- | --- |
| Timeout bước commit (`KvDurableTimeout`) / `TransactionExpiration` | 5 s / 10 s | PERF-02, DAT-04 | TBD-19 |
| Per-player ordering | concurrency 1, queue 2 | CMD-06 | TBD-07 |
| Receipt document / `resultCompact` tối đa | 4 KiB / 512 B | DAT-04, CMD-03 | TBD-13 |
| Aggregate size cảnh báo / cứng | 32 KiB / 64 KiB | DAT-09, API-10 | TBD-07 |
| Số aggregate/player; aggregate/command (`Touches`); marker/command | 16; 4; 2 | DAT-09 | TBD-07 |
| Session / player | 1 (takeover); cửa sổ cache ≤ 60 s | IDN-08, IDN-10 | TBD-04a, TBD-14 |
| Replay window | 24 h | CMD-07 | TBD-13 |
| Cache session / `pa_profile` / permission / gate | ≤ 60 s / ≤ 60 s / ≤ 60 s / ≤ 10 s | IDN-08, SEC-10, GATE-01 | TBD-14, TBD-05b |
| Battle hết hạn logic / số battle đang mở | 24 h / 1 | 8.4 | TBD-02 |
| CAS retry (document ngoài transaction) | ≤ 5 lần, jitter | CMD-05 | TBD-07 |
| Sweeper outbox chu kỳ | 30 s | OPS-05 | TBD-19 |
| Game Center cert cache / timestamp skew | 24 h / 10 phút | IDN-03 | TBD-03 |
| Xoá dòng | "Ring entry tối đa", "Section size (cảnh báo)", "Receipt tầng B Pending", "Receipt ring", "Cache … config ≤ 30 s" | — | — |

Phụ lục C: sinh lại sau khi áp (số ID đổi: xoá MIG-01/02; thêm SMP-01..05, ACC-53; IAP-01/02, ECO-08, ACC-44/46/47 đổi phase).

---

## §J. [MẶC ĐỊNH] mới cần owner chốt và [VERIFY] mới

**[MẶC ĐỊNH] mới (ngoài các giá trị đã có trong rev 2):**

| TBD | Giá trị tôi chọn | Lý do ngắn | Hệ quả nếu đổi |
| --- | --- | --- | --- |
| TBD-07 | Transaction là cơ chế commit duy nhất, **kể cả command một aggregate**; mọi ghi `pa_*` qua transaction runner | Một đường commit/tra cứu; loại hazard trộn ghi (V15) | Fast path một aggregate tiết kiệm ~6 KV op/StartBattle nhưng cần hai đường + V15 xác minh |
| TBD-07 | Aggregate cảnh báo 32 KiB / cứng 64 KiB; ≤ 16 aggregate/player; ≤ 4 Touches; ≤ 2 marker/command | Section nhỏ cho `full = true`; transaction ≤ 7 document | Tăng số là tăng latency transaction và payload wire |
| TBD-13 | Replay window 24 h (bỏ vế 64) ; receipt ≤ 4 KiB; `resultCompact` ≤ 512 B | Không còn ring nên bound theo số không còn ý nghĩa | Giảm xuống 1 h giảm nguy cơ "retry rất muộn"; tăng lên không có lợi |
| TBD-02 | StartBattle khi đang có battle: **Replace** (abandon cũ); hạn 24 h | Client mobile hay bị kill; vẫn chỉ một battle được thưởng | Reject cần command Abandon + UX |
| TBD-04a | Chấp nhận cửa sổ ≤ 60 s hai phiên cùng sống (cache); không strict check | Không thêm KV get mỗi request; hậu quả chỉ UX | Strict = +1 KV get/request, đá tức thì |
| TBD-03 | `pgs:{playerId}`; `gamecenter:{teamPlayerId}`; cert cache 24 h; skew 10 phút | Ổn định qua cài lại; `teamPlayerId` xuyên game cùng team | Dùng `gamePlayerId` thì không tìm lại được player nếu đổi app id… (cần V18/V19) |
| TBD-01 (IAP) | Google key = `purchaseToken` (orderId lưu); Apple = `transactionId`, verify JWS offline; server không acknowledge | `orderId` rỗng với test purchase; JWS offline không cần gọi Apple mỗi lần | Nếu Apple yêu cầu API call để chống replay xuyên app → thêm outbound |
| TBD-17 | Overlay `config/base` + `config/env/<env>` trong artifact; reload = restart; Dev file-watch SHOULD; endpoint `/v1/config/{version}/{file}` | Skew config = skew binary, đã có OPS-06 | Volume mount + watch cần điều phối version xuyên instance |
| TBD-15 | Unity LTS mới nhất tại Sprint 0 | Không còn nguồn Sniper PVP | — |
| TBD-19 | `TransactionExpiration` 10 s; sweeper 30 s | Lớn hơn `KvDurableTimeout` 5 s, nhỏ hơn timeout request GM | — |

**[VERIFY] mới:** V15–V22 (bảng I.10). V15, V16, V17 là **chặn thiết kế P0** (quyết định (a) phụ thuộc chúng); V18–V21 chặn P0.5/P0 IAP; V22 chặn chốt số TBD-07.

---

## §K. Rủi ro còn lại cần nói với owner

1. **Transaction trên hot path là lựa chọn có chủ đích của TBD-07, và chi phí thật** — ước ×3–5 latency, ×2–3 KV op so với rev 2; mọi command (kể cả StartBattle) trả phí. Nếu ACC-16 (khi có số TBD-06) không đạt, lối thoát là fast path một aggregate (P1) — nhưng chỉ sau khi V15 được xác minh. Owner cần biết rằng "nhiều aggregate" và "rẻ như một CAS" không thể có cùng lúc.
2. **Phụ thuộc vận hành mới**: Majority durability trở thành bắt buộc cứng (transactions), lost-transaction cleanup phải chạy trên mọi instance, `txn_meta` phải được provisioning và backup cùng điểm thời gian; Dev single-node phải chứng minh transactions chạy với replica = 0 (V4) trước khi viết integration test.
3. **Replay window đổi ngữ nghĩa nhẹ**: min(64, 24 h) → 24 h. Không còn "64": một client gửi lại `requestId` cũ sau > 24 h sẽ bị xử lý như request mới; command không có invariant tự nhiên (BuyVirtualItem) sẽ chạy lần hai. Điều này đã đúng ở rev 2 (sau 64 command); SDK không retry quá vài phút nên rủi ro thực tế thấp — cần owner xác nhận.
4. **P0 phình**: IAP (2 store adapter + verifier giả + catalog) và 5 sample command vào P0, cộng transaction runner thay cho CAS helper. Ước thêm ≈ 15–20 % effort P0 so với rev 2; ACC-47 sandbox thật chỉ làm được thủ công ở P0.5. Provider PGS/Game Center ở P0.5 không làm được integration test tự động (cần tài khoản sandbox) — ACC-01/03/29 provider là thủ công.
5. **Single session + cửa sổ 60 s**: hai thiết bị có thể cùng gửi command trong ≤ 60 s sau takeover; state vẫn đúng nhưng người chơi trên thiết bị cũ có thể thấy "thành công" rồi bị đá. Nếu game có kịch bản share account/đổi thiết bị thường xuyên, owner cân nhắc strict check (+1 KV get/request). Ngoài ra `SESSION_REPLACED` không tự re-login nên game phải có UI cho trường hợp này ở P0.
6. **TBD-06 trống hoàn toàn**: không còn giá trị tạm nào cho capacity; ACC-16 và kích thước aggregate (V22) không có mốc. Đề nghị owner đặt một "giả định thiết kế" (ví dụ 300 RPS peak, 30 login/s burst, 5 000 CCU) để load test có mục tiêu, thay vì để Mở tới P0.5.
7. **Config trong artifact**: LiveOps không thể đổi reward/giá mà không deploy; `gate` (maintenance/minClientVersion) vẫn đổi được không deploy. Nếu LiveOps cần đổi config hằng ngày, mô hình này sẽ bị bypass bằng cách… deploy nhiều — cần owner xác nhận chấp nhận.
8. **Game Center `teamPlayerId` vs `gamePlayerId`** (V19/V18): chọn sai id = không tìm lại được player sau cài lại hoặc trùng player giữa hai game cùng team trên cùng cluster (scope `g_<gameId>` tách nên không trùng dữ liệu, nhưng ý nghĩa identity khác nhau). Phải xác minh trước P0.5.
