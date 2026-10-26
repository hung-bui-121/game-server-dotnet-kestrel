# PCR — iap

Module framework `iap` (slice 003, version 0.1.0). Nguồn: IAP-01, IAP-02, ECO-08, SMP-04.

## Luồng

`POST /api/v1/iap/purchase` (Player, `X-Request-Id`) → adapter gọi `IStoreReceiptVerifier` **trước** transaction →
command `iap.purchase` (Touches `wallet`, `inventory`; 1 claim marker) cấp quà theo `iap.json`.

| Store | External reference | Claim key | Ledger |
| --- | --- | --- | --- |
| google | `purchaseToken` | `extref::iap_google::{token}` | `sourceType = iap`, `orderId` |
| apple | `transactionId` | `extref::iap_apple::{transactionId}` | `sourceType = iap`, `orderId = transactionId` |

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
| Fake | Development/Test (mặc định); bị chặn ở môi trường khác | `valid:{ref}` / `unavailable` / khác = invalid |
| Apple JWS | Staging/Production | ES256, chuỗi `x5c` tới `Iap:Apple:RootCertificateBase64` (Apple Root CA G3), OID Apple, `bundleId`, `productId`, `environment` (Production chỉ nhận Production), không `revocationDate` — [VERIFY] V21 |
| Google Play | Staging/Production | service account JWT → token (cache), `purchases.products.get`, `purchaseState = 0`, test purchase bị từ chối ở Production — [VERIFY] V20; timeout `Iap:StoreTimeoutSeconds` = 10 s |

Server không acknowledge/consume ở P0 (client làm sau khi nhận thành công). Refund/revoke (IAP-03) là P0.5.
