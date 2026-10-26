# Feature Specification: Unity Client SDK (P0 slice 006)

**Feature Branch**: `006-unity-sdk`

**Created**: 2026-10-26

**Status**: Draft

**Input**: "006 Unity SDK — SDK client do framework sở hữu: transport Protobuf, session, header gate, requestId + retry gửi lại đúng
bytes, xử lý mã lỗi, giữ rev từng section, offset thời gian, theo dõi configVersion + GetConfig; gói UPM + link.xml + Google.Protobuf
pin. Nguồn: SDK-01..04, API-09/10, IDN-10, GATE-02, TIME-01, CFG-03, ACC-04/18/20/34/35/43."

## Scope note

- Thiết bị thật Android/iOS, IL2CPP + stripping High (SDK-04, ACC-18) và CI IL2CPP smoke build (CI-02) cần Unity LTS mới nhất + module
  Android/iOS + thiết bị: **không làm được trên máy hiện tại** (chỉ có Unity 2021.3/2022.3, không có module build mobile đã xác nhận).
  Slice này giao: lõi SDK chạy được trên .NET (kiểm bằng test với server thật trong process), adapter `UnityWebRequest`, gói UPM có
  `link.xml`, client mẫu; nghiệm thu thiết bị là việc thủ công ghi trong quickstart.
- Header gate (`X-Client-Version`, `X-Platform`, `X-Build-Id`) SDK luôn gửi; server so `minClientVersion` ở P0.5 (GATE-02).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Game đăng nhập và đồng bộ trạng thái (Priority: P1)

Dev game gọi SDK để đăng nhập guest, lấy snapshot; SDK giữ token (không ghi ra body), giữ `rev` từng section, tính offset giờ server.

**Acceptance Scenarios**:

1. **Given** server chạy, **When** `LoginGuest`, **Then** SDK có token, section có `rev`, `ServerNow` lệch giờ server ≤ 1 s (ACC-43).
2. **Given** command thành công, **When** response có `SectionUpdate`, **Then** SDK áp dụng và `rev` bằng `newRev` (API-10).
3. **Given** update không `full` và `baseRev` ≠ rev cục bộ, **When** nhận, **Then** SDK refetch đúng section đó (ACC-34).

### User Story 2 - Retry an toàn (Priority: P1)

Mỗi command có một `requestId`; lỗi mạng, mất response, `COMMIT_UNKNOWN_RETRY`, `CONCURRENT_UPDATE`, 429/503 retryable → SDK gửi
lại **đúng bytes** cùng `requestId`, backoff mũ + jitter, tôn trọng `retryAfter`; hết số lần → báo lỗi cho game.

**Acceptance Scenarios**:

1. **Given** response đầu bị mất, **When** SDK retry, **Then** cùng bytes + requestId, server replay, chỉ một receipt (ACC-04).
2. **Given** `COMMIT_UNKNOWN_RETRY`, **When** retry, **Then** cùng bytes; kết quả OK.
3. **Given** 429 `retryAfterMs = 300`, **When** retry, **Then** chờ ≥ 300 ms.
4. **Given** lỗi không retryable (`INSUFFICIENT_FUNDS` nghiệp vụ, 400), **When** nhận, **Then** không retry.

### User Story 3 - Session và trạng thái tài khoản (Priority: P1)

**Acceptance Scenarios**:

1. **Given** thiết bị B đăng nhập cùng tài khoản, **When** A gửi command, **Then** A nhận sự kiện `SessionReplaced`, token bị xoá, SDK không tự đăng nhập lại (ACC-20).
2. **Given** `ACCOUNT_SUSPENDED`/`ACCOUNT_DELETED`/`SWITCH_ACCOUNT`/`MAINTENANCE`/`UPDATE_REQUIRED`, **When** nhận, **Then** SDK phát sự kiện tương ứng và trả lỗi có kiểu.

### User Story 4 - Config (Priority: P1)

**Acceptance Scenarios**:

1. **Given** response mang `configVersion` khác bản cache, **When** nhận, **Then** SDK đánh dấu cần tải lại và phát `ConfigVersionChanged`.
2. **When** `RefreshConfig`, **Then** gọi `GetConfig` với version đang có; `not_modified` không tải lại (CFG-03, ACC-35).

### User Story 5 - Gói Unity (Priority: P2)

Gói UPM `com.gameserver.client` (version lockstep với server) chứa code SDK, code sinh từ `.proto` của core + module framework,
`Google.Protobuf.dll` đúng version server pin, `link.xml`, asmdef, adapter `UnityWebRequest`, sample client.

## Requirements *(mandatory)*

- **FR-001**: Transport trừu tượng; adapter .NET `HttpClient` (bot/test) và Unity `UnityWebRequest`; `application/x-protobuf` (SDK-02).
- **FR-002**: Header: `Authorization: Bearer`, `X-Request-Id`, `X-Client-Version`, `X-Platform`, `X-Build-Id` (IDN-10, GATE-02).
- **FR-003**: Retry ≤ N lần (mặc định 4) với cùng bytes/requestId cho lỗi mạng và mã `retryable`; backoff 200 ms ×2^n ≤ 5 s, jitter
  ±20 %, ≥ `retryAfterMs` (SDK-02).
- **FR-004**: Mã lỗi → `GameServerError {code, codeNumber, retryable, retryAfterMs, details}`; sự kiện cho SESSION_REPLACED (xoá
  token, không re-login), ACCOUNT_*, SWITCH_ACCOUNT, MAINTENANCE, UPDATE_REQUIRED (SDK-02).
- **FR-005**: Section store `{id → (rev, payload)}`; áp `SectionUpdate`; refetch section khi lệch (SDK-03, API-10).
- **FR-006**: `ServerNow` = giờ máy + offset (trung điểm RTT) từ mọi response có `server_time_utc_ms` (TIME-01).
- **FR-007**: Theo dõi `configVersion`; `RefreshConfig` qua `GetConfig`; cache file theo version (CFG-03).
- **FR-008**: Gói UPM + `link.xml` preserve `Google.Protobuf` và assembly SDK; Google.Protobuf pin = server (API-09, SDK-01).

## Success Criteria *(mandatory)*

- **SC-001**: Mọi kịch bản retry chỉ tạo một receipt.
- **SC-002**: SDK không bao giờ tự đăng nhập lại sau `SESSION_REPLACED`.
- **SC-003**: Lõi SDK build cho `netstandard2.1` với C# 9 (khớp Unity 2021.3+).

## Assumptions

- Unity target theo TBD-15 = LTS mới nhất tại Sprint 0; SDK giữ ở C# 9 / .NET Standard 2.1 để chạy trên 2021.3 trở lên.
- Lưu token/config bền vững (PlayerPrefs, file) là adapter của game; SDK cung cấp interface + bản in-memory.
