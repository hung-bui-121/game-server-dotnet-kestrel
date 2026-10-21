# game-server-dotnet-kestrel

Framework backend dùng chung cho nhiều game mobile: C# / .NET, ASP.NET Core + Kestrel, Couchbase.
Client Unity giao tiếp qua HTTPS + Protobuf; GM tool web qua HTTPS + JSON. Server giữ trạng thái
progression/resource chính thức (server-authoritative); client gửi action, server quyết định và trả kết quả.

## Trạng thái

Giai đoạn grooming — chưa có code. Tài liệu yêu cầu đang được soát và nâng lên v1.1.

## Cấu trúc

| Đường dẫn | Nội dung |
| --- | --- |
| `AgentMemories/` | Ghi chú nghiên cứu, quyết định và handoff giữa các phiên làm việc. Đọc `AgentMemories/README.md` trước. |
| `AgentMemories/features/req-review/` | Kết quả review tài liệu yêu cầu v1.0 (chất lượng yêu cầu + độ phù hợp kỹ thuật). |

Tài liệu yêu cầu gốc không nằm trong repo.
