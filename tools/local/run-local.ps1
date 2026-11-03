<#
.SYNOPSIS
  Builds and runs a game server host on its local flavor in one command: the framework sample by default, or a game's host.
.DESCRIPTION
  Resolves the host with tools/GameServer.LocalRun (launch profile, flavor, port, data and log folders, governing
  global.json; host files are only read), builds the host (Debug), starts it with the launch profile from its project
  folder, waits until GET /health/ready answers 200 (at most 60 s), prints the URLs and keeps streaming the host output.
  Ctrl+C stops the host gracefully (readiness 503, drain, commits complete); the script waits up to 35 s, then kills the
  process tree. No path leaves a host running after the script exits.
  Contract: specs/013-consumer-readiness/contracts/run-local-cli.md (supersedes section 4 of the slice 011 operations
  contract). Without -HostProject, -LaunchProfile and -Name it behaves as the slice 011 script (samples/SampleHost,
  profile local, port 5080).

  Exit codes: 0 stopped by Ctrl+C after a graceful shutdown; 2 invalid argument or host input (project, launch profile,
  settings, global.json); 3 .NET SDK of global.json not found; 4 port in use; 5 build failed (host or resolver); 6 host
  exited before ready (its own message is above); 7 readiness timeout; other = host exit code after it was ready.
  A game's wrapper may use 8 and above for its own checks.
.PARAMETER HostProject
  Host project file, or a folder with exactly one *.csproj (relative to the current directory). Default: the framework's
  samples/SampleHost/SampleHost.csproj.
.PARAMETER LaunchProfile
  Launch profile of the host's Properties/launchSettings.json (flavor and default port). Default: local.
.PARAMETER Name
  Banner title. Default: "Local game server" for the sample host, else the project name.
.PARAMETER Port
  Port to listen on. Default: the first http:// URL port of the launch profile (5080 when it has none).
.PARAMETER Reset
  Start with an empty store: the host deletes its configured store folder content after locking it (FileStore:ResetOnStart).
.PARAMETER Lan
  Listen on all interfaces (http://0.0.0.0:<port>) so a phone on the same network can connect. The development tools of
  the host (test authentication, cheats, staff seed when configured) become reachable from the network.
.EXAMPLE
  ./tools/local/run-local.ps1
  ./tools/local/run-local.ps1 -Port 5090 -Reset
  framework/tools/local/run-local.ps1 -HostProject src/ZhPvp.Host -LaunchProfile local -Name "ZhPvp"
#>
param(
    [string]$HostProject = "",
    [string]$LaunchProfile = "local",
    [string]$Name = "",
    [string]$Port = "",
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
$SampleName = "Local game server"

function Stop-WithError([int]$Code, [string]$Message) {
    [Console]::Error.WriteLine("run-local: " + $Message)
    exit $Code
}

$usage = "Usage: tools/local/run-local.ps1 [-HostProject <path>] [-LaunchProfile <name>] [-Name <text>] [-Port <1-65535>] [-Reset] [-Lan]"
if ($args.Count -gt 0) {
    Stop-WithError $ExitInvalidArgument ("unknown argument(s): " + ($args -join " ") + ". " + $usage)
}

$portNumber = 0
if ($Port -ne "" -and (-not [int]::TryParse($Port, [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535)) {
    Stop-WithError $ExitInvalidArgument ("-Port must be a number 1-65535, got '" + $Port + "'. " + $usage)
}
if ($LaunchProfile.Trim() -eq "") {
    Stop-WithError $ExitInvalidArgument ("-LaunchProfile must not be empty. " + $usage)
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ($HostProject -eq "") { $HostProject = Join-Path $repo "samples\SampleHost\SampleHost.csproj" }
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

# Runs a native command with captured output (Windows PowerShell 5.1 turns redirected stderr into error records).
function Invoke-Captured([string]$FileName, [string[]]$Arguments) {
    $quoted = foreach ($argument in $Arguments) { '"' + ($argument -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    $info = New-Object Diagnostics.ProcessStartInfo($FileName, ($quoted -join " "))
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $info.WorkingDirectory = (Get-Location).Path
    $child = [Diagnostics.Process]::Start($info)
    $errorText = $child.StandardError.ReadToEndAsync()
    $outputText = $child.StandardOutput.ReadToEnd()
    $child.WaitForExit()
    return @{ ExitCode = $child.ExitCode; Output = $outputText; Error = $errorText.Result }
}

# 1. SDK: dotnet on PATH, else ~/.dotnet10 (repository convention), accepted when "dotnet --version" succeeds in the host
# folder (it honours the governing global.json). The version named in the message is read from that global.json.
$sdkFolder = (Get-Location).Path
if (Test-Path -LiteralPath $HostProject -PathType Container) { $sdkFolder = (Resolve-Path -LiteralPath $HostProject).Path }
elseif (Test-Path -LiteralPath $HostProject -PathType Leaf) { $sdkFolder = Split-Path -Parent (Resolve-Path -LiteralPath $HostProject).Path }
$candidates = @()
$onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $onPath) { $candidates += $onPath.Source }
$exe = "dotnet"
if ($onWindows) { $exe = "dotnet.exe" }
$candidates += (Join-Path (Join-Path $HOME ".dotnet10") $exe)
$dotnet = $null
Push-Location $sdkFolder
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
    $sdkVersion = "(no global.json found)"
    $globalJson = ""
    for ($folder = $sdkFolder; $folder; $folder = Split-Path -Parent $folder) {
        $candidateJson = Join-Path $folder "global.json"
        if (Test-Path -LiteralPath $candidateJson) {
            $globalJson = $candidateJson
            $sdkVersion = "(see global.json)"
            try { $sdkVersion = (Get-Content -Raw -LiteralPath $candidateJson | ConvertFrom-Json).sdk.version } catch { }
            break
        }
    }
    Stop-WithError $ExitSdkMissing (".NET SDK " + $sdkVersion + " (" + $globalJson + ") not found. Looked at dotnet on PATH and ~/.dotnet10. " +
        "Fix: install it from https://dotnet.microsoft.com/download/dotnet or into ~/.dotnet10.")
}

# 2. Describe the host (tools/GameServer.LocalRun; its build output stays in the framework, git-ignored).
$resolver = Join-Path $repo "tools\GameServer.LocalRun"
$resolverBuild = Invoke-Captured $dotnet @("build", (Join-Path $resolver "GameServer.LocalRun.csproj"), "-c", "Debug", "-nologo", "-v", "q")
if ($resolverBuild.ExitCode -ne 0) {
    Write-Host ($resolverBuild.Output + $resolverBuild.Error)
    Stop-WithError $ExitBuildFailed "build of the host resolver tools/GameServer.LocalRun failed (see the compiler output above)."
}
$described = Invoke-Captured $dotnet @((Join-Path $resolver "bin\Debug\net10.0\GameServer.LocalRun.dll"), "describe", "--host-project", $HostProject, "--launch-profile", $LaunchProfile)
if ($described.ExitCode -eq 2) {
    Stop-WithError $ExitInvalidArgument ($described.Error.Trim())
}
if ($described.ExitCode -ne 0) {
    Stop-WithError $ExitBuildFailed ("the host resolver tools/GameServer.LocalRun failed with exit code " + $described.ExitCode + ": " + ($described.Output + $described.Error).Trim())
}
$hostInfo = @{}
foreach ($line in ($described.Output -split "\r?\n")) {
    $separator = $line.IndexOf("=")
    if ($separator -gt 0) { $hostInfo[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
}
$project = $hostInfo["project"]
$hostDir = $hostInfo["projectDir"]
$isSample = $hostInfo["isSample"] -eq "true"
if ($Name -eq "") {
    if ($isSample) { $Name = $SampleName } else { $Name = $hostInfo["projectName"] }
}
$portNote = ""
if ($Port -eq "") {
    $portNumber = [int]$hostInfo["port"]
    $portNote = $hostInfo["portNote"]
}
Write-Host ("run-local: host project " + $project + " (profile " + $LaunchProfile + ", flavor " + $hostInfo["flavor"] + ")")

# 3. Refuse a busy port before building (the host binds localhost = both loopback addresses; -Lan binds all interfaces).
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

# 4. Build (output visible).
& $dotnet build $project -c Debug
if ($LASTEXITCODE -ne 0) {
    Stop-WithError $ExitBuildFailed ("build of " + $project + " failed (see the compiler output above).")
}

# 5. Start the host from its project folder (content root) with the launch profile; --urls overrides the profile URL.
$listenHost = "localhost"
if ($Lan) { $listenHost = "0.0.0.0" }
$runArgs = @("run", "--no-build", "--launch-profile", ('"' + $LaunchProfile + '"'), "--", "--urls", ("http://" + $listenHost + ":" + $portNumber))
if ($Reset) {
    $runArgs += "--FileStore:ResetOnStart=true"
    Write-Host ("run-local: -Reset: the host empties its store folder " + $hostInfo["dataFolder"] + " after locking it.")
}

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
    $logs = $hostInfo["logFolder"]
    if (-not (Test-Path -LiteralPath $logs)) { return }
    $newest = Get-ChildItem -LiteralPath $logs -File | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($null -eq $newest) { return }
    Write-Host ("Last " + $LogTailLines + " lines of " + $newest.FullName + ":")
    Get-Content -LiteralPath $newest.FullName -Tail $LogTailLines | ForEach-Object { Write-Host ("  " + $_) }
}

try {
    $process = Start-Process -FilePath $dotnet -ArgumentList $runArgs -WorkingDirectory $hostDir -NoNewWindow -PassThru -ErrorAction Stop
    $null = $process.Handle # Windows PowerShell 5.1 reports no ExitCode unless the handle is opened while the process runs.

    # 6. Readiness.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $ready) {
        if ($process.HasExited) {
            $process.WaitForExit()
            Show-LogTail
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

    # 7. URLs. The sample host keeps its slice 011 banner; other hosts get host-independent lines only.
    $resetNote = ""
    if ($Reset) { $resetNote = ", started empty" }
    $folder = $hostInfo["dataFolder"]
    if ($isSample) { $folder = $hostInfo["dataFolderSetting"] }
    if ($folder -eq "") { $folder = "not configured" }
    Write-Host ""
    Write-Host ($Name + " is ready (flavor " + $hostInfo["flavor"] + ", data " + $folder + $resetNote + ")") -ForegroundColor Green
    if ($isSample) {
        Write-Host ("  Player API   " + $baseUrl + "          (routes /api/v1/..., /v1/time)")
        Write-Host ("  Health       " + $baseUrl + "health/ready")
        Write-Host ("  GM UI        " + $baseUrl + "gm/v1/ui  (staff admin / dev-only-change-me)")
        Write-Host ("  GM OpenAPI   " + $baseUrl + "gm/v1/openapi/gm.json")
    }
    else {
        Write-Host ("  Project      " + $project + " (profile " + $LaunchProfile + ")")
        Write-Host ("  Base URL     " + $baseUrl)
        Write-Host ("  Health       " + $baseUrl + "health/ready")
    }
    if ($portNote -ne "") {
        Write-Host ("  Note         " + $portNote)
    }
    if ($Lan) {
        Write-Host ""
        Write-Host "WARNING: the server listens on all network interfaces (-Lan)." -ForegroundColor Red
        Write-Host "  The development tools of this host (test authentication, cheats, staff seed when configured) are reachable from the network." -ForegroundColor Red
        Write-Host "  Use -Lan only on a trusted network, and stop the server when the device test is done." -ForegroundColor Red
        $lanAddresses = [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) |
            Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($_) }
        foreach ($address in $lanAddresses) {
            Write-Host ("  From a device: http://" + $address + ":" + $portNumber + "/") -ForegroundColor Red
        }
    }
    Write-Host "Press Ctrl+C to stop."
    Write-Host ""

    # 8. Wait. Ctrl+C reaches the host through the shared console and interrupts this loop; "finally" waits for the drain.
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
