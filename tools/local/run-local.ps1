<#
.SYNOPSIS
  Builds and runs the sample game server on the local flavor (file store, no Couchbase) in one command.
.DESCRIPTION
  Builds samples/SampleHost (Debug), starts it with the launch profile "local" from samples/SampleHost, waits until
  GET /health/ready answers 200 (at most 60 s), prints the URLs and keeps streaming the host output. Ctrl+C stops the
  host gracefully (readiness 503, drain, commits complete); the script waits up to 35 s, then kills the process tree.
  No path leaves a host running after the script exits. Contract: specs/011-local-env/contracts/operations-cli.md section 4.

  Exit codes: 0 stopped by Ctrl+C after a graceful shutdown; 2 invalid argument; 3 .NET SDK of global.json not found;
  4 port in use; 5 build failed; 6 host exited before ready (its own message is above); 7 readiness timeout;
  other = host exit code after it was ready.
.PARAMETER Port
  Port to listen on (default 5080).
.PARAMETER Reset
  Start with an empty store: the host deletes the configured store folder content after locking it (FileStore:ResetOnStart).
.PARAMETER Lan
  Listen on all interfaces (http://0.0.0.0:<port>) so a phone on the same network can connect. Development tools (test
  authentication, cheats, the GM staff seed) become reachable from the network.
.EXAMPLE
  ./tools/local/run-local.ps1
  ./tools/local/run-local.ps1 -Port 5090 -Reset
#>
param(
    [string]$Port = "5080",
    [switch]$Reset,
    [switch]$Lan
)

# Windows PowerShell 5.1 and PowerShell 7. Keep this file ASCII only: 5.1 reads a BOM-less file as ANSI.
# "Continue": in 5.1, stderr of a native command (dotnet, taskkill) would otherwise become a terminating error.
$ErrorActionPreference = "Continue"

$ExitInvalidArgument = 2
$ExitSdkMissing = 3
$ExitPortInUse = 4
$ExitBuildFailed = 5
$ExitHostExited = 6
$ExitReadyTimeout = 7
$ReadyTimeoutSeconds = 60
$ReadyPollMilliseconds = 500
$ProbeTimeoutMilliseconds = 2000
$WaitPollMilliseconds = 200
$StopWaitSeconds = 35
$LogTailLines = 20
$SdkVersion = "10.0.403"

function Stop-WithError([int]$Code, [string]$Message) {
    [Console]::Error.WriteLine("run-local: " + $Message)
    exit $Code
}

$usage = "Usage: tools/local/run-local.ps1 [-Port <1-65535>] [-Reset] [-Lan]"
if ($args.Count -gt 0) {
    Stop-WithError $ExitInvalidArgument ("unknown argument(s): " + ($args -join " ") + ". " + $usage)
}

$portNumber = 0
if (-not [int]::TryParse($Port, [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535) {
    Stop-WithError $ExitInvalidArgument ("-Port must be a number 1-65535, got '" + $Port + "'. " + $usage)
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$hostDir = Join-Path $repo "samples\SampleHost"
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

# 1. SDK: dotnet on PATH, else ~/.dotnet10 (repository convention). "dotnet --version" in the repository honours global.json.
$candidates = @()
$onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $onPath) { $candidates += $onPath.Source }
$exe = "dotnet"
if ($onWindows) { $exe = "dotnet.exe" }
$candidates += (Join-Path (Join-Path $HOME ".dotnet10") $exe)
$dotnet = $null
Push-Location $repo
try {
    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        $null = & $candidate --version 2>&1
        if ($LASTEXITCODE -eq 0) { $dotnet = $candidate; break }
    }
}
finally {
    Pop-Location
}
if ($null -eq $dotnet) {
    Stop-WithError $ExitSdkMissing (".NET SDK " + $SdkVersion + " (global.json) not found. Looked at dotnet on PATH and ~/.dotnet10. " +
        "Fix: install it from https://dotnet.microsoft.com/download/dotnet/10.0 or into ~/.dotnet10.")
}

# 2. Refuse a busy port before building (the host binds localhost = both loopback addresses; -Lan binds all interfaces).
$probeAddresses = @([Net.IPAddress]::Loopback)
if ([Net.Sockets.Socket]::OSSupportsIPv6) { $probeAddresses += [Net.IPAddress]::IPv6Loopback }
if ($Lan) { $probeAddresses += [Net.IPAddress]::Any }
foreach ($address in $probeAddresses) {
    $listener = New-Object Net.Sockets.TcpListener($address, $portNumber)
    $busy = $false
    try { $listener.Start() } catch { $busy = $true } finally { $listener.Stop() }
    if ($busy) {
        Stop-WithError $ExitPortInUse ("port " + $portNumber + " is in use (" + $address + "). Fix: stop the other server or pick another port with -Port <n>.")
    }
}

# 3. Build (output visible).
& $dotnet build (Join-Path $hostDir "SampleHost.csproj") -c Debug
if ($LASTEXITCODE -ne 0) {
    Stop-WithError $ExitBuildFailed "build of samples/SampleHost failed (see the compiler output above)."
}

# 4. Start the host from its project folder (content root) with the local launch profile; --urls overrides the profile URL.
$listenHost = "localhost"
if ($Lan) { $listenHost = "0.0.0.0" }
$runArgs = @("run", "--no-build", "--launch-profile", "local", "--", "--urls", ("http://" + $listenHost + ":" + $portNumber))
if ($Reset) { $runArgs += "--FileStore:ResetOnStart=true" }

$baseUrl = "http://localhost:" + $portNumber + "/"
# Probe IPv4 loopback: with -Lan the host binds 0.0.0.0 only, and "localhost" would try ::1 first (refused connections are
# retried by Windows for about 2 s, longer than the probe timeout).
$readyUrl = "http://127.0.0.1:" + $portNumber + "/health/ready"
$process = $null
$exitCode = 0
$ready = $false

function Test-Ready([string]$Url) {
    try {
        $request = [Net.WebRequest]::Create($Url)
        $request.Timeout = $ProbeTimeoutMilliseconds
        $request.Proxy = $null
        $response = $request.GetResponse()
        $status = [int]$response.StatusCode
        $response.Close()
        return $status -eq 200
    }
    catch {
        return $false
    }
}

function Stop-HostTree([Diagnostics.Process]$Target) {
    if ($null -eq $Target -or $Target.HasExited) { return }
    if ($onWindows) {
        & taskkill.exe /PID $Target.Id /T /F 2>&1 | Out-Null
    }
    else {
        $Target.Kill($true)
    }
    $Target.WaitForExit()
}

function Show-LogTail {
    $logs = Join-Path $hostDir "logs"
    if (-not (Test-Path $logs)) { return }
    $newest = Get-ChildItem $logs -File | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($null -eq $newest) { return }
    Write-Host ("Last " + $LogTailLines + " lines of " + $newest.FullName + ":")
    Get-Content $newest.FullName -Tail $LogTailLines | ForEach-Object { Write-Host ("  " + $_) }
}

try {
    $process = Start-Process -FilePath $dotnet -ArgumentList $runArgs -WorkingDirectory $hostDir -NoNewWindow -PassThru -ErrorAction Stop
    $null = $process.Handle # Windows PowerShell 5.1 reports no ExitCode unless the handle is opened while the process runs.

    # 5. Readiness.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $ready) {
        if ($process.HasExited) {
            $process.WaitForExit()
            Stop-WithError $ExitHostExited ("the host exited with code " + $process.ExitCode + " before it was ready; its own message is above " +
                "(data folder locked by another host, malformed file, invalid configuration).")
        }
        if (Test-Ready $readyUrl) { $ready = $true; break }
        if ($watch.Elapsed.TotalSeconds -ge $ReadyTimeoutSeconds) {
            Show-LogTail
            Stop-HostTree $process
            Stop-WithError $ExitReadyTimeout ("the host was not ready after " + $ReadyTimeoutSeconds + " s (GET " + $readyUrl + ").")
        }
        Start-Sleep -Milliseconds $ReadyPollMilliseconds
    }

    # 6. URLs.
    $folder = "(see appsettings.Local.json)"
    try { $folder = (Get-Content -Raw (Join-Path $hostDir "appsettings.Local.json") | ConvertFrom-Json).FileStore.Folder } catch { }
    $resetNote = ""
    if ($Reset) { $resetNote = ", started empty" }
    Write-Host ""
    Write-Host ("Local game server is ready (flavor Local, data " + $folder + $resetNote + ")") -ForegroundColor Green
    Write-Host ("  Player API   " + $baseUrl + "          (routes /api/v1/..., /v1/time)")
    Write-Host ("  Health       " + $baseUrl + "health/ready")
    Write-Host ("  GM UI        " + $baseUrl + "gm/v1/ui  (staff admin / dev-only-change-me)")
    Write-Host ("  GM OpenAPI   " + $baseUrl + "gm/v1/openapi/gm.json")
    if ($Lan) {
        Write-Host ""
        Write-Host "WARNING: the server listens on all network interfaces (-Lan)." -ForegroundColor Red
        Write-Host "  The GM UI with the development staff seed, test authentication and cheats are reachable from the network." -ForegroundColor Red
        Write-Host "  Use -Lan only on a trusted network, and stop the server when the device test is done." -ForegroundColor Red
        $lanAddresses = [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) |
            Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($_) }
        foreach ($address in $lanAddresses) {
            Write-Host ("  From a device: http://" + $address + ":" + $portNumber + "/") -ForegroundColor Red
        }
    }
    Write-Host "Press Ctrl+C to stop."
    Write-Host ""

    # 7. Wait. Ctrl+C reaches the host through the shared console and interrupts this loop; "finally" waits for the drain.
    while (-not $process.HasExited) {
        Start-Sleep -Milliseconds $WaitPollMilliseconds
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        if (-not $process.WaitForExit($StopWaitSeconds * 1000)) {
            [Console]::Error.WriteLine("run-local: the host did not stop within " + $StopWaitSeconds + " s; killing the process tree.")
            Stop-HostTree $process
        }
        $exitCode = $process.ExitCode
    }
}

exit $exitCode
