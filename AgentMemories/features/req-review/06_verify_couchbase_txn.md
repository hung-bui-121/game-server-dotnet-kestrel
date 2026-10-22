# 06 — Xác minh Couchbase .NET SDK 3.x distributed ACID transactions (V15, V16, V17, V23, V4 + bổ sung)

Ngày xác minh: 2026-10-22. Nguồn chính: docs.couchbase.com (dotnet-sdk nhánh `release/3.10`, java-sdk current, server 8.0), API reference `couchbase-net-client` (3.7.2 / 3.8.0 / latest), mã nguồn GitHub `couchbase/couchbase-net-client` (master, `src/Couchbase/Client/Transactions/*`), NuGet. Phiên bản SDK mới nhất tại thời điểm xác minh: **CouchbaseNetClient 3.10.0 (08-10-2026)**.

Quy ước kết luận: **XÁC NHẬN** / **BÁC BỎ** / **XÁC NHẬN MỘT PHẦN** / **KHÔNG XÁC MINH ĐƯỢC**. Trích dẫn tiếng Anh giữ nguyên văn.

Lưu ý nguồn: các thread forum Couchbase (www.couchbase.com/forums) trả 403 khi fetch tự động; chỉ dùng được qua tóm tắt của công cụ tìm kiếm → đánh dấu "(forum, gián tiếp)" và không dùng làm bằng chứng chính.

---

## V15 — Trộn ghi transaction / non-transaction trên cùng document

### Câu hỏi gốc (bảng I.10)
Hành vi khi document đang staged bị ghi non-transactional (lost update?); staged insert có nhìn thấy bằng KV get thường không (shadow document/tombstone); quy tắc "mọi ghi `pa_*` qua transaction" có đủ và cần không.

### Kết luận
**XÁC NHẬN — quy tắc "mọi writer phải dùng transaction" là khuyến nghị chính thức và là CẦN (không chỉ đủ).** Couchbase tuyên bố rõ: nếu ghi non-transactional (kể cả có CAS) đồng thời với ghi transactional trên cùng document thì **hành vi là undefined, bên nào cũng có thể "thắng"**. Không có cơ chế bảo vệ được cam kết. Staged insert là **tombstone** (document "đã xoá" chỉ có xattr) nên **KV get thường không nhìn thấy** (trả DocumentNotFound). Đọc non-transactional đồng thời thì an toàn, ở mức Read Committed.

### Bằng chứng
1. .NET SDK concept doc (nhánh 3.10) — https://docs.couchbase.com/dotnet-sdk/current/concept-docs/transactions.html, mục "Concurrency with Non-Transactional Writes" (nội dung giống Java concept doc https://docs.couchbase.com/java-sdk/current/concept-docs/transactions.html, đã đối chiếu bản `.md`):
   > "the application should ensure that non-transactional writes are never done concurrently with transactional writes, on the same document."
   > "If two such writes **do** conflict then the behaviour is undefined: either write may 'win', overwriting the other. **This still applies if the non-transactional write is using CAS.**"
   > "Note this only applies to _writes_. Any non-transactional _reads_ concurrent with transactions are fine, and are at a Read Committed level."
2. Couchbase Server 8.0 docs — https://docs.couchbase.com/server/current/learn/data/transactions.html, mục Performance/Notes:
   > "Non-transactional updates should not be made to any document involved in a transaction while the transaction is itself in progress. In certain cases, the interaction can interfere with the integrity of the transactional updates; in other cases the interaction can cause the non-transactional update to be overwritten."
3. Java how-to (ngữ nghĩa chung của spec transactions) — https://docs.couchbase.com/java-sdk/current/howtos/distributed-acid-transactions-from-the-sdk.html, mục "Non-Transactional Writes":
   > "applications should **never** perform non-transactional _writes_ concurrently with transactional ones, on the same document."
   > "`IllegalDocumentStateEvent` events ... are raised when a non-transactional write has been detected and overridden."
   (→ SDK *có thể* phát hiện một số trường hợp và **ghi đè** bản non-transactional; không có cam kết phát hiện mọi trường hợp. .NET không tài liệu hoá event tương đương — KHÔNG XÁC MINH ĐƯỢC cho .NET.)
4. Cơ chế trong mã nguồn .NET (GitHub master, `src/Couchbase/Client/Transactions/AttemptContext.cs` và `DataAccess/DocumentRepository.cs`, đọc 2026-10-22):
   - Staging dùng CAS của bản đã đọc; unstage (commit) dùng CAS ghi lại lúc staging. Khi gặp `FailCasMismatch` ở bước commit, `HandleDocChangedDuringCommit` **reset CAS về 0 và retry** (comment: "Same txn/attempt, so let's just retry with the new cas") → ghi non-transactional xen vào giữa staging và commit **bị ghi đè**, không được báo lỗi. Release note 3.9.4 (NCBC-4036): "a retryable CAS mismatch at commit is now retried instead of being surfaced as a non-retryable [error]".
   - Write-write conflict giữa **hai transaction** được xử lý: `CheckWriteWriteConflict` đọc xattr `txn` của document; nếu thuộc transaction khác còn sống thì backoff tới `WriteWriteConflictTimeLimit` (1 s) rồi `RetryTransaction()`. Cơ chế này chỉ dựa vào xattr `txn` — writer non-transactional không ghi xattr đó nên không được "nhìn thấy".
   - Ghi non-transactional `Replace`/`Upsert` toàn document **không bảo toàn xattr `txn`** là hành vi KV chuẩn (xattr đi theo document body khi replace không qua subdoc) — KHÔNG XÁC MINH ĐƯỢC từ docs chính thức cho trường hợp cụ thể này; chỉ suy ra từ "either write may win".
5. Staged insert = tombstone — mã nguồn `DocumentRepository.MutateStagedInsert`:
   > `GetMutateInOptions(StoreSemantics.Insert, collection).AccessDeleted(true).CreateAsDeleted(true)` — toàn bộ nội dung ghi vào xattr (`StagedData` / `BinStagedData`), **không ghi body**.
   Commit: `ReplaceBodyWithXattr` + `StoreSemantics.AccessDeleted` + `ReviveDocument(true)` (fallback: `collection.InsertAsync`). Docs .NET how-to: "Other transactions, and other non-transactional actors, will not be able to see this staged insert yet." Forum (gián tiếp, nhân viên Couchbase): "Inserts are staged before the commit with a tombstone ... As it's a tombstone it's invisible to the rest of the Couchbase data platform" (https://www.couchbase.com/forums/t/insert-in-transactions-moving-from-staged-to-new-document-creation-how-it-works/32660).
6. Lưu ý thêm: yêu cầu Server tối thiểu cho .NET transactions là **6.6.2** (how-to .NET: "Couchbase Server (6.6.2 or above)") — chính là bản đưa vào subdoc flag `CreateAsDeleted`.

### Hệ quả cho thiết kế
- Quy tắc 0.3 / DAT-09 ("mọi ghi `pa_*` chỉ qua `ICommandRunner` hoặc `IPlayerAggregateWriter` chạy transaction") là **bắt buộc**, không phải phòng xa. Fast path "CAS thường cho command một aggregate" (escape hatch P1, §G) **không thể** mở trên cùng collection `pa_*` mà transaction cũng ghi — phải loại bỏ hoàn toàn khỏi lộ trình, hoặc chỉ cho phép với aggregate/collection mà transaction **không bao giờ** chạm.
- Đọc snapshot ngoài transaction (`GetAsync` thường) cho `pa_*` là an toàn (Read Committed); staged insert (receipt, claim marker) không rò rỉ ra KV get thường → tra receipt bằng KV get thường sau `CommitAmbiguous` (CMD-04) là hợp lệ: thấy receipt ⇒ đã commit (hoặc đã unstage).
- Cần test thực nghiệm (xem cuối file) vì docs chỉ nói "undefined".

---

## V16 — Atomic counter (`Increment`/`Decrement`) ngoài transaction

### Câu hỏi gốc
Binary counter không dùng được trong transaction; giới hạn document binary trong transaction.

### Kết luận
**XÁC NHẬN — không có API counter trong transaction; counter chạy ngoài transaction và KHÔNG rollback theo transaction.** `AttemptContext` (API ref 3.10) chỉ có `GetAsync/GetOptionalAsync/GetReplicaFromPreferredServerGroup/GetMulti*/InsertAsync/ReplaceAsync/RemoveAsync/QueryAsync` — không có `Increment/Decrement/Append/Prepend`. Document **binary** thì từ 3.8.0 được hỗ trợ (đọc/ghi nguyên blob qua `ctx.ReplaceAsync` với transcoder), nhưng đó là replace toàn giá trị, không phải op counter atomic. Việc "counter không rollback khi transaction rollback" không được docs nói thẳng — là hệ quả hiển nhiên vì op nằm ngoài lambda (docs .NET: thao tác `Collection` trong lambda "will not be performed transactionally" và lambda có thể chạy lại nhiều lần).

### Bằng chứng
1. API ref `AttemptContext` — https://docs.couchbase.com/sdk-api/couchbase-net-client/api/Couchbase.Client.Transactions.AttemptContext.html (latest) và bản 3.7.2 / 3.8.0: không có method Increment/Decrement. Mã nguồn `AttemptContext.cs` cũng không có.
2. Release notes .NET — https://docs.couchbase.com/dotnet-sdk/current/project-docs/sdk-release-notes.html:
   - 3.8.0 (20-08-2025) NCBC-3778: "Added support for Binary objects in Transactions."
   - 3.10.0 (08-10-2026) NCBC-4261: "Transactions now commit a document with the user flags recorded when it was staged" (binary giữ đúng flags sau cleanup/ambiguous commit → trước 3.10 có bug với binary doc).
3. KV how-to .NET — https://docs.couchbase.com/dotnet-sdk/current/howtos/kv-operations.html, mục Atomic Counters: "can be increased or decreased atomically using `Binary.Increment()` and `Binary.Decrement()`"; "Increment & Decrement are considered part of the 'binary' API and as such may still be subject to change." Không có câu nào về transaction.
4. .NET transactions how-to — https://docs.couchbase.com/dotnet-sdk/current/howtos/distributed-acid-transactions-from-the-sdk.html: thao tác `Collection` trực tiếp trong lambda "may be performed multiple times, and will not be performed transactionally."

### Hệ quả cho thiết kế
- GFT-04 reserve-then-confirm (Increment → kiểm quota → Decrement nếu vượt) ngoài transaction là đúng mô hình; phải chấp nhận **under-fill tạm thời** (đã ghi trong GFT-03 reaper) và counter op **không được đặt trong lambda** (lambda retry sẽ tăng nhiều lần).
- Thứ tự an toàn: `Increment` **trước** transaction; nếu transaction fail/rollback → `Decrement` bù (best-effort) + reaper đối chiếu `giftcode_claims` để sửa counter.
- Nếu muốn counter nằm trong ACID: dùng document JSON `{count}` và `ctx.ReplaceAsync` (CAS/W-W conflict → retry) — hot key sẽ xung đột cao, không phù hợp giftcode hot.

---

## V17 — Metadata collection (`txn_meta`), ATR, lost cleanup

### Câu hỏi gốc
Cấu hình metadata collection cho ATR trong SDK .NET; lost-cleanup xuyên instance; số document tối đa/transaction và kích thước staged.

### Kết luận
**XÁC NHẬN (có hỗ trợ `MetadataCollection`); một phần KHÔNG XÁC MINH ĐƯỢC (số ATR, TTL, giới hạn document).**
- Mặc định ATR (`_txn:atr-*`) và `_txn:client-record` nằm ở **`_default._default` của bucket chứa document mutate đầu tiên**. Cấu hình tập trung: `TransactionsConfigBuilder.Create().MetadataCollection(new Keyspace(bucket, scope, collection))` → `ClusterOptions.TransactionsConfig`. Collection **phải tồn tại trước**, app cần **RBAC data read + write** trên nó, **không được xoá** khi còn transaction.
- Lost cleanup: chạy **trên mọi process/Cluster có dùng transactions** (3.9.6: cleanup registration "owned per `Cluster`"; 3.9.4: app không dùng transactions sẽ không khởi động cleanup). Mọi client chia nhau tập ATR qua `_txn:client-record`. **Không có app chạy ⇒ không có cleanup.** Khi cấu hình `MetadataCollection`, cleanup của client đó **chỉ quét collection đó** → mọi instance phải dùng **cùng** `txn_meta`.
- `TransactionCleanupConfig`: `CleanupWindow` mặc định **60 000 ms**, `CleanupLostAttempts = true`, `CleanupClientAttempts = true`, `CollectionsList` (thêm collection vào cleanup set).
- ATR: tài liệu nói "created and maintained automatically", entry được xoá khi transaction xong/cleanup; **không** có TTL/expiry trên ATR document; số lượng ATR cố định (1024/collection theo blog + forum, gián tiếp) — KHÔNG XÁC MINH ĐƯỢC từ docs chính thức.
- Giới hạn: Server docs "Use transactions only on documents less than 10 MB in size"; số document/transaction: "across any number of documents" — **không có giới hạn cứng được tài liệu hoá**; ràng buộc thực tế là `ExpirationTime` (15 s mặc định).
- Backup: docs **không** có hướng dẫn backup riêng cho metadata; XDCR: "transaction metadata documents ... are never replicated".

### Bằng chứng
1. .NET concept doc — https://docs.couchbase.com/dotnet-sdk/current/concept-docs/transactions.html, "Custom Metadata Collections":
   > "By default, these are created in the default collection of the bucket of the first mutated document in the transaction."
   > code: `var metadataCollection = new Keyspace("bucket", "scope", "collection"); ... TransactionsConfigBuilder.Create().MetadataCollection(metadataCollection).Build();`
   > "The asynchronous cleanup started by this `Transactions` object will be looking for expired transactions only in this collection."
   > "You need to ensure that this application has RBAC data read and write privileges to it, and should not delete the collection subsequently as it can interfere with existing transactions."
2. API ref `TransactionsConfigBuilder` — https://docs.couchbase.com/sdk-api/couchbase-net-client/api/Couchbase.Client.Transactions.Config.TransactionsConfigBuilder.html: `MetadataCollection(Keyspace)`, `CleanupConfig(TransactionCleanupConfig)`, `DurabilityLevel`, `ExpirationTime`, `KeyValueTimeout`, `QueryConfig`, `LoggerFactory`. Migration guide (https://docs.couchbase.com/dotnet-sdk/current/project-docs/distributed-acid-transactions-migration-guide.html): "`TransactionsConfigBuilder.MetadataCollection` takes a `Keyspace` instead of an `ICouchbaseCollection`"; "`TransactionOptions` now allows a `MetadataCollection` parameter" (per-transaction override).
3. API ref `TransactionCleanupConfig` — https://docs.couchbase.com/sdk-api/couchbase-net-client/api/Couchbase.Client.Transactions.Config.TransactionCleanupConfig.html: `DefaultCleanupWindowMilliseconds = 60000`, `DefaultCleanupLostAttempts = true`, `DefaultCleanupClientAttempts = true`, `CollectionsList: List<Keyspace>` "Collections to cleanup lost/abandoned transactions attempts from this or other clients".
4. .NET cleanup doc — https://docs.couchbase.com/dotnet-sdk/current/concept-docs/transactions-cleanup.html:
   > "All applications connected to the same cluster and running transactions will share in the cleanup, via a low-touch communication protocol on the `_txn:client-record` metadata document that will be created in each collection in the cluster involved with transaction metadata."
   > "All ATRs will be distributed between all cleanup clients, so increasing the number of applications will not increase the reads required for scanning."
   > "if an application is not running, then cleanup is not running."
   > "with default settings it will generally find expired transactions within 60 seconds, and use less than 20 reads per second, per collection of metadata documents being checked."
5. Release notes .NET: 3.9.6 (03-09-2026) NCBC-4291 "cleanup registrations are now owned per `Cluster` rather than shared across the process"; 3.9.4 (16-07-2026) NCBC-4218 "Applications that do not use transactions no longer eagerly start the transaction cleanup machinery", NCBC-4009 "Lost-transaction cleanup now processes ATRs in adaptive batches", NCBC-4219 cleanup "resolves its collections lazily, per cleanup cycle"; 3.10.0 NCBC-4233 "An exception in the lost-transaction cleanup timer can no longer terminate the application."
6. Server docs — https://docs.couchbase.com/server/current/learn/data/transactions.html: "Key-Value _insert_, _update_, and _delete_ operations, across any number of documents"; "Use transactions only on documents less than 10 MB in size"; "a custom metadata collection with appropriate RBAC permissions can be used to control visibility. You can also use a custom metadata collection if you wish to remove the default collection."; "To execute a key-value operation within a transaction, users must have the relevant _Administrative_ or _Data_ RBAC roles, and permissions on the relevant buckets, scopes and collections."; XDCR: "transaction metadata documents that are never replicated."
7. Mã nguồn `AttemptContext.cs`: "Default: `atrCollection` is the default collection of the bucket containing the first document being modified. Override: if `_config.MetadataCollection` is set, it is used instead." ATR id từ `AtrIds.GetAtrId(id)` theo vBucket; `AtrIds.NumAtrs` (giá trị không đọc được trong trích đoạn).
8. 1024 ATR: chỉ từ blog Couchbase "XDCR and transactions" và forum (gián tiếp, 403 khi fetch) → KHÔNG XÁC MINH ĐƯỢC từ docs chính thức.

### Hệ quả cho thiết kế
- `txn_meta` (DAT-01) hợp lệ: tạo collection **trước** khi app khởi động (provisioning DAT-07 phải tạo `g_<gameId>.txn_meta` trước mọi transaction), cấp role Data Reader + Data Writer trên nó, cấu hình **giống nhau trên mọi instance** (kể cả job/sweeper nếu chúng dùng transaction) — nếu lệch, cleanup không quét ATR của instance khác.
- Không có TTL: `txn_meta` không expiry là đúng (DAT-03); ATR document rỗng tồn tại vĩnh viễn, dung lượng nhỏ.
- Backup cùng điểm thời gian với `pa_*` (DAT-08) vẫn là chính sách của ta; docs không có hướng dẫn chuyên biệt → ghi rõ trong runbook là transaction dở sau restore sẽ được cleanup resolve (theo entry ATR + xattr), đã khớp với docs error-handling ("the asynchronous cleanup process will instead attempt to roll it back at some point in the future").
- Giới hạn số document: không cần quy tắc cứng từ Couchbase; giới hạn `Touches ≤ 4 + receipt + ≤ 2 marker` là do ta chọn (thời gian trong 10 s ExpirationTime).

---

## V23 — Get song song trong một transaction attempt; bulk get ngoài transaction

### Câu hỏi
Có được phát nhiều `ctx.GetAsync` đồng thời (`Task.WhenAll`) trong một attempt không? Có `GetMulti` trong transaction không (bản nào)? Ngoài transaction: cách bulk get.

### Kết luận
- **Song song `ctx.GetAsync` trong .NET: BÁC BỎ (không được khuyến nghị / không được tài liệu hoá là an toàn).** Docs .NET: "operations inside an individual transaction should be kept in-order and executed using `await` immediately. Do not use fire-and-forget tasks under any circumstances." (Java reactive API thì cho phép concurrent — là đặc thù Java, không áp cho .NET.) Release notes 3.6.5 có "`ExtThreadSafety` implementation into integrated transactions library" nhưng không có tài liệu công khai về ngữ nghĩa → KHÔNG XÁC MINH ĐƯỢC rằng `Task.WhenAll` trên một `AttemptContext` được hỗ trợ.
- **`GetMulti` trong transaction: XÁC NHẬN — có từ .NET SDK 3.8.0** (`AttemptContext.GetMulti(List<TransactionGetMultiSpec>, TransactionGetMultiOptionsBuilder?)` và `GetMultiReplicaFromPreferredServerGroup`), đánh dấu **volatile** (API có thể đổi). Có `TransactionGetMultiMode { DisableReadSkewDetection, PrioritizeLatency, PrioritizeReadSkewDetection }`. Không xuất hiện trong release notes (NCBC) — chỉ thấy qua API ref 3.8.0 so với 3.7.2.
- **Bulk get ngoài transaction: XÁC NHẬN — chuẩn là `GetAsync` song song + `Task.WhenAll`**; không có `GetMulti` KV ngoài transaction trong .NET SDK. Docs nói rõ đây là để gom kết quả, **không giảm số round-trip** ("This will not be more efficient — for Cluster or for network"); async client đã pipeline sẵn trên kết nối ("Asynchronous clients inherently batch operations").

### Bằng chứng
1. .NET how-to — https://docs.couchbase.com/dotnet-sdk/current/howtos/distributed-acid-transactions-from-the-sdk.html: "operations inside an individual transaction should be kept in-order and executed using `await` immediately."; "Do not use fire-and-forget tasks under any circumstances."
2. Java how-to (để đối chiếu) — https://docs.couchbase.com/java-sdk/current/howtos/distributed-acid-transactions-from-the-sdk.html, "Concurrent Operations": "The reactive API allows operations to be performed concurrently inside a transaction, which can assist performance." (chỉ Java reactive.)
3. API ref 3.7.2 vs 3.8.0 (`https://docs.couchbase.com/sdk-api/couchbase-net-client-3.7.2/api/Couchbase.Client.Transactions.AttemptContext.html` và `...-3.8.0/...`): 3.7.2 không có `GetMulti`; 3.8.0 có `GetMulti`, `GetMultiReplicaFromPreferredServerGroup`, `GetReplicaFromPreferredServerGroup`. Latest API ref: `Task<TransactionGetMultiResult> GetMulti(List<TransactionGetMultiSpec> specs, TransactionGetMultiOptionsBuilder? options = null)` (volatile). `TransactionGetMultiResult.ContentAs<T>(int index)` theo thứ tự spec. `TransactionGetMultiMode` — https://docs.couchbase.com/sdk-api/couchbase-net-client/api/Couchbase.Client.Transactions.Config.TransactionGetMultiMode.html.
4. Mã nguồn `AttemptContext.cs`: `_attemptLock = new SemaphoreSlim(1,1)` ("works like a mutex") dùng cho `InitAtrIfNeeded`; `_opWrapper.WaitOnTasksThenPerformUnderLockAsync` cho commit/rollback; unstage song song giới hạn `_unstagingConcurrency = 100`. Không có comment nào tuyên bố app được gọi KV op song song.
5. Forum (gián tiếp, 403): "Batch insert using transactions" (https://www.couchbase.com/forums/t/batch-insert-using-transactions/31345) — người dùng gọi `InsertAsync` song song trên một `AttemptContext` gặp "Collection was modified; enumeration operation may not execute" (bản cũ; không có kết luận chính thức).
6. Bulk ngoài transaction — https://docs.couchbase.com/dotnet-sdk/current/howtos/concurrent-async-apis.html: "Batching may be done with Task.WhenAll."; "This will not be more efficient — for Cluster or for network —"; "Asynchronous clients inherently batch operations".

### Hệ quả cho thiết kế
- Trong `ICommandRunner`: đọc N aggregate bằng **`ctx.GetMulti`** (SDK ≥ 3.8.0, pin ≥ 3.9.x) với mode `PrioritizeLatency` hoặc `DisableReadSkewDetection` (ta đã tự khoá bằng CAS/W-W conflict lúc replace); **không** dùng `Task.WhenAll` trên `ctx.GetAsync`. Vì API volatile → bọc trong adapter riêng + integration test, fallback là `GetAsync` tuần tự (N ≤ 4 → +N−1 RTT, chấp nhận được).
- `Reads` (aggregate chỉ đọc, không ghi) có thể lấy **ngoài transaction** bằng `GetAsync` song song + `Task.WhenAll` trước khi vào lambda (Read Committed), giảm số op trong attempt; nhưng dữ liệu đó không được bảo vệ khỏi read skew — chỉ dùng cho dữ liệu "tham chiếu" (config, giftcode định nghĩa), không cho state quyết định commit.

---

## V4 + bổ sung — Package, Server tối thiểu, durability single-node, expiration, giới hạn

### Package / phiên bản
- **XÁC NHẬN**: transactions **tích hợp trong `CouchbaseNetClient`** từ 3.2.8 (02-03-2022, NCBC-3103 "Integrate Transactions into couchbase-net-client repo"); namespace `Couchbase.Client.Transactions` (đổi tên từ `Couchbase.Integrated.Transactions` ở 3.6.5, NCBC-3920); cấu hình qua `ClusterOptions.TransactionsConfig`, dùng `cluster.Transactions.RunAsync(async ctx => ...)`. `ctx.CommitAsync()/RollbackAsync()` đã bỏ khỏi public API (commit khi lambda xong; throw để rollback).
- Package riêng **`Couchbase.Transactions` đã deprecated** trên NuGet: "This package has been deprecated as it is **legacy** and is no longer maintained" (bản cuối 3.9.0, 26-02-2026; https://www.nuget.org/packages/Couchbase.Transactions). Migration guide: "new transaction features will only be added to the SDK and it is recommended that all users migrate."
- SDK hiện tại: **3.10.0 (08-10-2026)**; hỗ trợ .NET 8 (3.5–3.10) và .NET 10 LTS (3.9–3.10); .NET 6 chỉ tới 3.6. Ma trận tương thích: Server 7.0–7.2, 7.6, 8.0 đều ✔ với SDK 3.8–3.10 (https://docs.couchbase.com/dotnet-sdk/current/project-docs/compatibility.html). Server < 7.0 là EOL. 3.9.1 "Resolved Transaction issues with older (7.2) servers" → nên pin Server ≥ 7.6 và SDK ≥ 3.9.1.
- Server tối thiểu cho transactions (how-to .NET): "Couchbase Server (6.6.2 or above)"; "NTP should be configured so nodes of the Couchbase cluster are in sync with time."

### V4 — `DurabilityLevel.Majority` trên Dev single-node
- **XÁC NHẬN có điều kiện: chạy được khi bucket có replica = 0; THẤT BẠI khi replica = 1 (mặc định bucket mới).**
- .NET how-to, "Single Node Cluster": "the default number of replicas for a newly created bucket is 1. If left at this default, all key-value writes performed with durability will fail with a `DurabilityImpossibleException`. In turn, this will cause all transactions (which perform all key-value writes durably) to fail." → sửa bằng đặt replicas = 0 (UI/CLI), "If the bucket already exists, then the server needs to be rebalanced for the setting to take effect."
- Server docs durability — https://docs.couchbase.com/server/current/learn/data/durability.html, bảng Majority: `0 replicas → 1 node required`, `1 → 2`, `2 → 2`, `3+ → Not supported` (EDurabilityImpossible). ⇒ với replica = 0, Majority = active node, transaction chạy bình thường trên một node.
- Durability mặc định của transactions = `Majority`; "A level of `None` is present but its use is discouraged and unsupported. If durability is set to `None`, then ACID semantics are not guaranteed." → không dùng `None` kể cả Dev.

### TransactionExpiration mặc định
- **XÁC NHẬN 15 s**: `TransactionsConfig.DefaultExpirationMilliseconds = 15000`; error-handling doc: "transaction timeouts (which default to 15 seconds)"; Server docs: "The default value is 15 seconds." Cấu hình `TransactionsConfigBuilder.ExpirationTime(TimeSpan)`. Lưu ý: `KeyValueTimeout` "has been removed from `TransactionsConfig` and `TransactionOptions`" theo migration guide (API ref latest vẫn liệt kê `KeyValueTimeout(TimeSpan)` — mâu thuẫn docs, KHÔNG XÁC MINH ĐƯỢC; không dựa vào nó).
- Ngữ nghĩa lỗi (error-handling doc): `TransactionFailedException` = "definitely did not reach the commit point"; `TransactionExpiredException` = "retries were made until the timeout was reached"; `TransactionCommitAmbiguousException` = "may or may not have reached the commit point ... should be rare".

### Giới hạn document/transaction
- KHÔNG có giới hạn số document được tài liệu hoá ("across any number of documents"); giới hạn kích thước: "< 10 MB". Thực tế bị chặn bởi 15 s (hoặc 10 s của ta).

---

## Bảng tóm tắt

| # | Câu hỏi | Kết luận | Nguồn chính |
| --- | --- | --- | --- |
| V15 | Trộn ghi txn/non-txn cùng document | **XÁC NHẬN**: undefined, "either write may win", kể cả CAS; SDK không cam kết phát hiện; staged insert = tombstone, KV get thường không thấy; đọc non-txn an toàn (Read Committed) | .NET/Java concept doc; Server 8.0 docs; mã nguồn DocumentRepository (CreateAsDeleted) |
| V16 | Counter ngoài txn | **XÁC NHẬN**: không có Increment/Decrement trong `AttemptContext`; binary doc hỗ trợ từ 3.8.0 (replace toàn giá trị); counter không rollback (hệ quả, docs không nói thẳng) | API ref AttemptContext; release notes 3.8.0/3.10.0 |
| V17 | `txn_meta`, cleanup | **XÁC NHẬN** `MetadataCollection(Keyspace)`, collection phải tồn tại, RBAC data R/W, cleanup chỉ quét collection đó; cleanup chạy per `Cluster` trên mọi app, `CleanupWindow` 60 s; **KHÔNG XÁC MINH ĐƯỢC**: 1024 ATR, TTL ATR, giới hạn document | .NET concept/cleanup doc; API ref TransactionCleanupConfig; release notes 3.9.4/3.9.6 |
| V23 | Get song song trong attempt | **BÁC BỎ** `Task.WhenAll` trên `ctx.GetAsync` (docs: in-order, await ngay); **XÁC NHẬN** `ctx.GetMulti` từ 3.8.0 (volatile, có read-skew mode); ngoài txn: `GetAsync`+`Task.WhenAll` là chuẩn, không giảm RTT | .NET how-to; API ref 3.7.2 vs 3.8.0; concurrent-async-apis doc |
| V4 | Majority trên single-node | **XÁC NHẬN có điều kiện**: replica = 0 ⇒ chạy; replica = 1 (mặc định) ⇒ `DurabilityImpossibleException`; `None` không hỗ trợ | .NET how-to; Server durability table |
| bổ sung | Package/Server/expiry | Tích hợp trong CouchbaseNetClient từ 3.2.8, namespace `Couchbase.Client.Transactions`; `Couchbase.Transactions` deprecated; Server ≥ 6.6.2 (khuyến nghị ≥ 7.6, SDK ≥ 3.9.1); expiration mặc định 15 s; không giới hạn số document, < 10 MB/document | release notes; NuGet; compatibility; error-handling doc |

## Điều gì làm thay đổi thiết kế đã chọn (transaction cho mọi command)
1. **Không thay đổi quyết định (a)** — V15 củng cố: trộn ghi là undefined ngay cả với CAS, nên "transaction cho mọi ghi `pa_*`" là bắt buộc. **Escape hatch P1 "fast path một aggregate + CAS thường" phải bị loại bỏ** (không chỉ hoãn), hoặc viết lại thành "fast path = transaction một document" (vẫn trả phí ATR).
2. DAT-07: tạo `txn_meta` **trước** khi app chạy transaction đầu tiên; cấu hình `MetadataCollection` **giống hệt** ở mọi instance/process (API, sweeper, job) — nếu không, lost cleanup không bao phủ.
3. CMD runner: đọc aggregates bằng `ctx.GetMulti` (pin SDK ≥ 3.9.1, bọc adapter vì volatile), không `Task.WhenAll` trong attempt.
4. DAT-05 Dev: bucket Dev phải tạo với **replicas = 0** (ghi vào script provisioning Dev + CI), không hạ durability xuống `None`.
5. GFT-04: counter `Increment` phải nằm **ngoài** lambda, trước transaction; `Decrement` bù + reaper.

## Cần test thực nghiệm (docs không nói)
1. **V15-T1**: ghi `Replace(cas)` thường vào document đang staged (giữa staging và commit của transaction khác) — kết quả cuối là gì (bản nào thắng), transaction có retry hay commit đè, xattr `txn` còn hay mất, cleanup có làm gì với document đó không. Lặp cho `MutateIn` (subdoc giữ xattr) và `Upsert` toàn document.
2. **V15-T2**: `Insert` thường vào key đang là staged insert (tombstone) của transaction khác — `DocumentExistsException` hay thành công (tombstone bị revive?).
3. **V15-T3**: KV `GetAsync` thường trên staged insert → xác nhận `DocumentNotFoundException`; `ExistsAsync` trả gì; N1QL với `request_plus` có thấy không.
4. **V23-T1**: `Task.WhenAll` 4 × `ctx.GetAsync` trên cùng `AttemptContext` ở 3.9.x/3.10 — có lỗi hay không (để quyết định có cần adapter fallback), và `ctx.GetMulti` với 4 spec có đúng 1 RTT/node không (đo bằng tracing).
5. **V17-T1**: đếm số ATR thực tế trong `txn_meta` sau khi chạy ≥ 10k transaction (xác minh 1024 và không có TTL); đo thời gian cleanup resolve transaction bị kill giữa chừng (kỳ vọng ≤ 60 s + ExpirationTime).
6. **V17-T2**: hai instance với `MetadataCollection` khác nhau — xác nhận instance A không cleanup ATR của B (để chốt yêu cầu "cấu hình giống nhau").
7. **V4-T1**: single-node, bucket replicas = 0, SDK 3.10 — transaction với `Majority` commit OK; đổi replicas = 1 không rebalance → `DurabilityImpossibleException`.
8. **V16-T1**: `Increment` trước lambda, lambda throw → counter không đổi lại (xác nhận cần Decrement bù); lambda retry 3 lần không làm counter tăng thêm (vì nằm ngoài).
