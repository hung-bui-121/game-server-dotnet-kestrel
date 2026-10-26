# Research: SampleGame, Economy, Config, IAP (003)

## R1. Economy là thư viện trên `ICommandContext`

- **Decision**: module `economy` sở hữu aggregate `wallet`/`inventory` và catalog; module khác thay đổi resource bằng
  `Economy.Debit/Grant(ctx, …)` trong handler của chính command (cùng transaction). Command phải khai `wallet`/`inventory`
  trong `Touches`.
- **Rationale**: ECO-04 (một transaction), ECO-01 (một đường ghi resource) mà không cần service gọi chéo trong lambda.

## R2. Kết quả "commit nhưng mã nghiệp vụ"

- **Decision**: `CommandOutcome<T>.Commit(result, code)`; runner ghi state + receipt với `resultCode = code.Name`; HTTP 200.
  Dùng cho `BATTLE_EXPIRED` (xoá `activeBattle` nhưng không thưởng).

## R3. Id xác định

- **Decision**: `ctx.NewId(purpose)` = base32(SHA-256(receiptId|purpose|n))[0..20]; ổn định giữa các attempt và giữa các
  retry trong replay window (receiptId xác định). Dùng cho `battleId`, seed (64-bit từ cùng hash).

## R4. Bảng thưởng chụp lúc start

- **Decision**: `activeBattle.reward` lưu bảng thưởng stage tại StartBattle; EndBattle dùng bản chụp (PLY-05 "kết quả cố
  định"; CFG-02 không cần lưu lịch sử config).

## R5. IAP verify trước transaction

- **Decision**: adapter gọi `IStoreReceiptVerifier` rồi đưa `VerifiedPurchase` vào domain request; handler chỉ kiểm
  `valid`, catalog, claim. Retry cùng `requestId` sẽ verify lại (outbound idempotent) rồi replay receipt.
- **Apple**: JWS compact; header `alg = ES256`, `x5c` 3 cert; build chain tới Apple Root CA G3 (cấu hình PEM, không tải
  runtime), kiểm OID Apple trên leaf; verify chữ ký ES256 trên `header.payload`; payload `bundleId`, `environment`
  (`Production`/`Sandbox` khớp deployment), `productId`, `transactionId` = externalReference. [VERIFY] V21.
- **Google**: OAuth2 service account (JWT RS256 → token endpoint), `GET androidpublisher/v3/applications/{pkg}/purchases/
  products/{productId}/tokens/{token}`; hợp lệ khi `purchaseState = 0`; `orderId` lưu ledger. [VERIFY] V20, V18.
- Lỗi mạng/5xx/timeout → `StoreUnavailableException` → 503 `STORE_UNAVAILABLE` retryable.

## R6. GetConfig

- **Decision**: core endpoint `POST /api/v1/config/get` (Player); bundle = các file `clientVisible` (bytes nguyên văn sau
  overlay); `not_modified` khi version trùng; > `Limits:ConfigBundleBytes` (1 MiB) → 413.

## R7. Cheat

- **Decision**: module riêng `economy.cheat` (`CheatModule`) đăng ký endpoint profile `Dev`; host chỉ `AddModule` khi
  `AllowsDevelopmentTools`; validator đã chặn `Dev` ở Production.
