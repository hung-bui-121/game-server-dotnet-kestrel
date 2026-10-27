<#
.SYNOPSIS
  Stops the SampleHost instances started by start-hosts.ps1 (hard stop). For the graceful-shutdown drill (ACC-28) use
  send-ctrl-c.ps1 instead - see docs/guides/local-real-env-testing.md.
#>
param([string]$StateDir = (Join-Path ([IO.Path]::GetTempPath()) "gameserver-local"))
$file = Join-Path $StateDir "hosts.json"
if (-not (Test-Path $file)) { Write-Host "No hosts.json in $StateDir."; return }
$list = Get-Content $file -Raw | ConvertFrom-Json  # Windows PowerShell 5.1 emits the array as one object: enumerate it here
foreach ($h in $list) {
    $p = Get-Process -Id $h.Pid -ErrorAction SilentlyContinue
    # Same PID and same start time: never stop an unrelated process that reused the PID.
    if ($p -and $h.StartTime -and $p.StartTime.ToString("o") -eq $h.StartTime) {
        Stop-Process -Id $h.Pid -Force
        Write-Host "stopped host $($h.Id) (pid $($h.Pid))"
    }
    else {
        Write-Host "host $($h.Id) (pid $($h.Pid)) is not running"
    }
}
Remove-Item $file
