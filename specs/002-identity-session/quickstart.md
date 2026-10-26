# Quickstart / validation — 002 identity-session

```powershell
$dotnet = "$env:USERPROFILE\.dotnet10\dotnet.exe"
& $dotnet test --solution GameServer.slnx -c Release
```

| # | Kịch bản | Kỳ vọng | ACC |
| --- | --- | --- | --- |
| 1 | Login guest mới → command → login lại | cùng `player_id`, snapshot có dữ liệu | ACC-01 |
| 2 | Sai secret | 401 `UNAUTHENTICATED`, cùng thông điệp | ACC-02 |
| 3 | Hai login đồng thời lần đầu cùng credential | một player | IDN-01 |
| 4 | Login lần 2 trên instance khác → token cũ | `SESSION_REPLACED` ≤ 60 s trên cả hai instance | ACC-20 |
| 5 | Ban login / ban nhóm `counter` / deleted | `ACCOUNT_SUSPENDED` (+until) / chặn chỉ counter / `ACCOUNT_DELETED` | ACC-31 (phần P0) |
| 6 | 11 login/phút cùng localId; 61 thất bại/phút cùng IP; 100 localId khác cùng IP | 429 / 429 / không chặn | ACC-52 |
| 7 | Token player gọi GM; GM gọi Player | 401 | ACC-09 |
| 8 | Logout rồi dùng token | 401 | ACC-01 |
| 9 | Session hết hạn trượt (FakeClock +31 ngày) / tuyệt đối (+91 ngày dù touch) | 401 | IDN-08 |
| 10 | Scan log sau bộ test tìm token/secret | không có | ACC-25 (phần identity) |
