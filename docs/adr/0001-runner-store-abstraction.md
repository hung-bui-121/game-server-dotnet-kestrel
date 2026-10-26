# ADR-0001: Command runner viết trên abstraction store; Couchbase là một implementation

- Trạng thái: Accepted (2026-10-26, slice 001)
- Yêu cầu liên quan: DAT-04, DAT-09, DAT-11, CMD-03..05, TST-01, mục 4.1 tài liệu yêu cầu

## Bối cảnh

Tài liệu yêu cầu (4.1) đặt transaction runner (`ICommandRunner`, `IPlayerAggregateWriter`) trong
Platform.Couchbase. Toàn bộ ngữ nghĩa idempotency (replay, payload mismatch, commit ambiguous, race giữa hai
instance, replay window) nằm trong runner. Máy phát triển hiện không có Couchbase/Docker (owner chọn
26/10/2026: unit test dùng store in-memory, integration test chạy khi có kết nối).

## Quyết định

- Runner, receipt, aggregate serializer, outbox relay/sweeper nằm trong `GameServer.Platform`, viết trên
  `ITransactionalDocumentStore` / `ITransactionContext` (get bundle, insert, replace, chạy transaction; lỗi chuẩn
  hoá: ambiguous, expired, failed, document exists, stale read).
- `GameServer.Platform.Couchbase` là nơi **duy nhất** gọi Couchbase SDK và implement store bằng distributed
  transactions. `GameServer.Platform.Testing` implement cùng contract in-memory với tiêm lỗi.
- Một bộ store conformance test chạy trên cả hai implementation.
- Arch test: chỉ Platform/Platform.Couchbase/Platform.Testing được phụ thuộc `ITransactionContext`; module không
  phụ thuộc store; chỉ Platform.Couchbase phụ thuộc SDK.

## Hệ quả

- Ngữ nghĩa ACC-04/05/07/44 được kiểm bằng unit test chạy mọi nơi.
- Hành vi Couchbase không có trong in-memory (ví dụ insert race báo "document exists" thay vì retry; `GetMulti`
  không trả handle để replace) được runner xử lý tổng quát (chạy lại tối đa 3 vòng) và được ghi ở PCR platform.
- Lệch câu chữ với 4.1: trách nhiệm giữ nguyên, chỉ vị trí project khác. Cập nhật tài liệu yêu cầu khi owner
  duyệt rev kế tiếp.
