<#
.SYNOPSIS
  ACC-16 outage drill: runs the load tool against the local hosts at base rate and stops the Couchbase Windows service
  in the middle of it, then starts it again. Stopping a service needs administrator rights: one UAC prompt appears for
  the elevated stop -> wait -> start step; the load itself runs unelevated in this window.
.DESCRIPTION
  Watch the per-window timeline: during the outage commands must fail fast with an ErrorEnvelope (503/504 codes, no
  hang past the request timeout; client timeouts are counted in the percentiles), /health/ready must turn not-ready,
  and both must recover on their own after Couchbase is back. The run ends with the ledger = wallet check.
.EXAMPLE
  ./tools/local/start-hosts.ps1 -Count 2
  ./tools/local/couchbase-outage.ps1 -StopAfterSeconds 40 -DownSeconds 30
#>
param(
    [string]$Urls = "http://localhost:5080/,http://localhost:5081/",
    [int]$StopAfterSeconds = 40,
    [int]$DownSeconds = 30,
    [string]$Service = "CouchbaseServer",
    [string]$Dotnet = (Join-Path $HOME ".dotnet10\dotnet.exe")
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repo "tools\GameServer.LoadTest"
& $Dotnet build $project -c Release -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "GameServer.LoadTest build failed." }
$tool = Join-Path $project "bin\Release\net10.0\GameServer.LoadTest.dll"
if ($null -eq (Get-Service $Service -ErrorAction SilentlyContinue)) { throw "Service '$Service' not found." }

$ready = Join-Path ([IO.Path]::GetTempPath()) ("gs-outage-ready-" + [Guid]::NewGuid().ToString("N"))
$total = $StopAfterSeconds + $DownSeconds + 120   # base rate the whole time, long enough to see recovery
$load = Start-Process -FilePath $Dotnet -NoNewWindow -PassThru -ArgumentList "`"$tool`"", "burst", "--urls", $Urls,
    "--warmup-s", "$total", "--burst-s", "0", "--cooldown-s", "0", "--players", "100", "--verify-wait-s", "120",
    "--ready-file", "`"$ready`""
$null = $load.Handle   # Windows PowerShell 5.1: without a cached handle ExitCode is empty after exit

while (-not (Test-Path $ready)) {
    if ($load.HasExited) { throw "Load tool exited during setup (exit $($load.ExitCode))." }
    Start-Sleep -Milliseconds 200
}
Remove-Item $ready
Start-Sleep -Seconds $StopAfterSeconds

$elevated = @"
`$ErrorActionPreference = 'Stop'
try {
    Stop-Service $Service -Force
    Write-Host 'stopped'
    Start-Sleep $DownSeconds
    Start-Service $Service
    Write-Host 'started'
    exit 0
} catch { Write-Host `$_; Start-Sleep 5; exit 1 }
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevated))   # quotes survive Start-Process
Write-Host ">>> stopping $Service for $DownSeconds s (UAC prompt)"
$admin = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile", "-EncodedCommand", $encoded
if ($admin.ExitCode -ne 0) { Write-Warning "Elevated stop/start failed (exit $($admin.ExitCode)); the drill did not happen as planned." }
else { Write-Host ">>> $Service started again; waiting for the load run to finish" }
$load.WaitForExit()
Write-Host "load tool exit code: $($load.ExitCode)"
