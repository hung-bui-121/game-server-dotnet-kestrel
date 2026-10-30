# PCR — iap

Module framework `iap` (slice 003, version 0.1.0; slice 012: event analytics `payment`). Nguồn: IAP-01, IAP-02, ECO-08, SMP-04, ANA-01.

## Luồng

`POST /api/v1/iap/purchase` (Player, `X-Request-Id`) → adapter gọi `IStoreReceiptVerifier` **trước** transaction →
command `iap.purchase` (Touches `wallet`, `inventory`; 1 claim marker) cấp quà theo `iap.json`.

| Store | External reference | Claim key | Ledger |
| --- | --- | --- | --- |
| google | `purchaseToken` | `extref::iap_google::{token}`; token > 226 byte → `extrefh::iap_google::{sha256 hex}` | `sourceType = iap`, `orderId` |
| apple | `transactionId` | `extref::iap_apple::{transactionId}` | `sourceType = iap`, `orderId = transactionId` |

Claim key: PCR economy "Claim key" (giới hạn 246 byte của key Couchbase, đo 2026-10-31).

## Catalog `iap.json`

`{products: {"{store}:{productId}": {grants: [{resource, quantity}]}}}` — không client-visible.

## Lỗi

| Code | HTTP | Khi |
| --- | --- | --- |
| `INVALID_RECEIPT` (12001) | 200 | verifier từ chối, sai môi trường, sản phẩm không có trong catalog |
| `ALREADY_GRANTED` (11005) | 200 | external reference đã được cấp |
| `STORE_UNAVAILABLE` (12002) | 503, retryable | store không trả lời / lỗi tạm thời |

## Verifier

| Verifier | Khi dùng | Ghi chú |
| --- | --- | --- |
| Fake | Development/Test (mặc định); bị chặn ở môi trường khác | `valid:{ref}` (order `GPA.{ref}`) / `noorder:{ref}` (hợp lệ, không order id — slice 012) / `unavailable` / khác = invalid; **mọi** purchase hợp lệ là test (`IsTest = true`), không có giờ store |
| Apple JWS | Staging/Production | ES256, chuỗi `x5c` tới `Iap:Apple:RootCertificateBase64` (Apple Root CA G3), OID Apple, `bundleId`, `productId`, `environment` (Production chỉ nhận Production), không `revocationDate` — [VERIFY] V21 |
| Google Play | Staging/Production | service account JWT → token (cache), `purchases.products.get`, `purchaseState = 0`, test purchase bị từ chối ở Production — [VERIFY] V20; timeout `Iap:StoreTimeoutSeconds` = 10 s |

## Kiểm khi khởi động (slice 011, T055)

`IapOptionsValidator` (`ValidateOnStart`, gộp vào báo cáo khởi động duy nhất; chế độ `--provision` không chạy):

| Điều kiện | Lỗi |
| --- | --- |
| ngoài Development/Test và `Iap:UseFakeVerifier=true` | `[module iap] Iap:UseFakeVerifier is true in '<env>'. …` |
| dùng verifier thật (mặc định ngoài Development/Test, hoặc `Iap:UseFakeVerifier=false`) và thiếu `Iap:Google:PackageName`, `Iap:Google:ServiceAccountEmail`, `Iap:Google:ServiceAccountPrivateKeyPem`, `Iap:Apple:BundleId` hoặc `Iap:Apple:RootCertificateBase64` | một lỗi mỗi key: `[module iap] <key> is not set. Fix: set the environment variable <Key__Name> (IAP-01).` |
| PEM không import được bằng `RSA.ImportFromPem` hoặc không ký được (ví dụ public key) | `[module iap] Iap:Google:ServiceAccountPrivateKeyPem is not a PEM RSA private key. …` |
| root Apple không phải base64 của certificate DER | `[module iap] Iap:Apple:RootCertificateBase64 is not a base64 DER X.509 certificate. …` |

Thông báo nêu key, không bao giờ nêu giá trị. Trước T055 verifier được tạo khi cần: thiếu khoá chỉ lộ ra ở lần mua đầu tiên
(`UseFakeVerifier=true` ở Production → 500 mỗi request). `Iap:Apple:RootCertificateBase64` (Apple Root CA G3) giờ là bắt buộc
khi dùng verifier thật.

Server không acknowledge/consume ở P0 (client làm sau khi nhận thành công). Refund/revoke (IAP-03) là P0.5.

## Event analytics `payment` (slice 012 — ANA-01, FR-007..FR-014)

Hợp đồng record, file và dedupe: PCR platform mục 18 và `docs/guides/analytics.md`. Phần của module iap:

- Handler `iap.purchase` gọi `ctx.AddAnalyticsEvent(IapAnalytics.Payment, IapAnalytics.Fields(purchase))` **sau** các grant, trong
  cùng transaction (entry outbox kind `analytics`, index = số grant). Chỉ dùng API Core (ARC-01). Replay, `ALREADY_GRANTED`,
  `INVALID_RECEIPT`, sản phẩm lạ, store lỗi → không có entry (từ chối thì rollback cả outbox).
- `transactionID` (`IapAnalytics.TransactionId`): `apple:{transactionId}`; `google:{orderId}`; Google không có `orderId` →
  `google:h:{32 hex}` = 32 hex đầu của SHA-256(UTF-8(purchase token)). **Không bao giờ là purchase token** (LOG-05); SHA-256 thường
  (token entropy cao, ổn định qua instance/môi trường, không secret, không xoay vòng).
- Field theo store: `isTest` (Google `purchaseType = 0`; Apple `environment ≠ Production` = Sandbox/Xcode/LocalTesting; verifier giả
  luôn `true`), `purchaseCategory` (Google `purchaseType` 0/1/2 → `test`/`promo`/`rewarded`; không có = purchase thường),
  `storePurchaseTimeUtc` (Google `purchaseTimeMillis` — int64 dạng chuỗi; Apple `purchaseDate` — ms; thiếu/không parse được → bỏ).
  [VERIFY] V5/V6 đã đối chiếu tài liệu Google Play Developer API v3 (`purchases.products`, cập nhật 2026-09-28) và App Store
  Server API (`JWSTransactionDecodedPayload`) ngày 2026-10-31.
- `chargeTime` = `committedAtUtc` của receipt (giờ cấp của server), không phải giờ store.

`VerifiedPurchase` (semantic với verifier của game — tương thích source/binary): thêm init-only `IsTest`, `PurchasedAt`
(`DateTimeOffset?`), `PurchaseCategory` (`string?`); constructor, `Deconstruct`, `Invalid` không đổi; equality của record gồm
các property mới. **Verifier tự viết không đặt `IsTest` sẽ báo mọi purchase là thật.**

Kích thước receipt (DAT-04, 4 KiB): entry analytics ≤ 512 B, kể cả trường hợp xấu nhất (product id 128 ký tự, `orderId`,
category và giờ store — test). Mỗi entry ledger lặp lại purchase token, nên purchase nhiều grant với token dài
vốn đã gần giới hạn trước slice này: 3 grant + token 300 ký tự + product id 128 ký tự vẫn < 4 KiB (test, in-memory và
Couchbase). **Ngân sách 4 KiB tăng tuyến tính theo số grant × độ dài token**: mỗi grant thêm một entry ledger mang bản sao
purchase token (`externalReference`), nên product nhiều grant với token rất dài có thể vượt 4 KiB — giữ số grant mỗi product
nhỏ. Claim key không còn giới hạn độ dài token: token dài hơn 226 byte dùng claim băm `extrefh::` (review T066; trước đó
token > 226 byte làm purchase lỗi `INTERNAL` trên Couchbase sau khi người chơi đã trả tiền).

**Cho slice iap-notifications (refund/void):** purchase Google không có `orderId` được ghi `transactionID = google:h:{hash}`;
record refund/void sau này mang `orderId` sẽ **không khớp** `transactionID` đó. Slice đó phải ghép theo hash của purchase
token (hoặc theo `receiptId`/ledger), không theo `orderId`.
