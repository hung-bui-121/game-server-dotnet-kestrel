# game-server-dotnet-kestrel

Framework backend dùng chung cho nhiều game mobile: C# / .NET, ASP.NET Core + Kestrel, Couchbase.
Client Unity giao tiếp qua HTTPS + Protobuf; GM tool web qua HTTPS + JSON. Server giữ trạng thái
progression/resource chính thức (server-authoritative); client gửi action, server quyết định và trả kết quả.

