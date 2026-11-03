<#
.SYNOPSIS
  Builds the whole framework inside a hostile consumer layout and fails when a consumer build file leaks in (FG-003, FG-004).
.DESCRIPTION
  Copies the framework's files (git-tracked plus untracked-not-ignored, never samples/**/Library) into
  %TEMP%\gs-seal-<guid>\tests\consumer\framework\, writes a hostile Directory.Build.props and Directory.Build.targets into
  ...\tests\consumer\ (each defines a target that fails the build with a marker), then runs
  "dotnet build GameServer.slnx -c Release" there. The parent folder named "tests" checks the relative test-project rule:
  a framework project wrongly treated as a test project loses its analyzers, and a test project wrongly treated as
  framework code fails on its banned APIs.

  Prints the error and warning counts and exits 0 only with 0 errors, 0 warnings and no marker; 1 otherwise; 3 when no
  .NET SDK is found. The temp folder is deleted unless -Keep. Manual check (slice 013 SC-004) and the future CI step; the
  fast evaluation-only variant runs in every test run (tests/GameServer.Architecture.Tests/BuildSealingTests.cs).
.PARAMETER Keep
  Keep the temp layout and the build log for inspection.
.EXAMPLE
  ./tools/local/check-build-seal.ps1
#>
param(
    [switch]$Keep
)

# Windows PowerShell 5.1 and PowerShell 7. Keep this file ASCII only: 5.1 reads a BOM-less file as ANSI.
$ErrorActionPreference = "Continue"

$ExitFailed = 1
$ExitSdkMissing = 3
$Marker = "GAMESERVER_HOSTILE_MARKER"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
# Short name: obj paths of the longest test project already pass 200 characters below the copy.
$root = Join-Path ([IO.Path]::GetTempPath()) ("gs-seal-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$consumer = Join-Path $root "tests\consumer"
$framework = Join-Path $consumer "framework"
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

# SDK: dotnet on PATH, else ~/.dotnet10 (repository convention); "dotnet --version" in the repository honours global.json.
$exe = "dotnet"
if ($onWindows) { $exe = "dotnet.exe" }
$candidates = @()
$onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $onPath) { $candidates += $onPath.Source }
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
    [Console]::Error.WriteLine("check-build-seal: no .NET SDK matching " + (Join-Path $repo "global.json") + " (dotnet on PATH, ~/.dotnet10).")
    exit $ExitSdkMissing
}

$exitCode = $ExitFailed
try {
    $files = & git -C $repo ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in $repo" }
    $copied = 0
    foreach ($file in $files) {
        if ($file -match "(^|/)Library/" -and $file.StartsWith("samples/")) { continue }
        $source = Join-Path $repo $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $target = Join-Path $framework $file
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)
        Copy-Item -LiteralPath $source -Destination $target
        $copied++
    }

    # InitialTargets: a leaked file names itself before any compile error it may cause.
    $hostile = "<Project InitialTargets=""GameServerHostile{0}"">`n" +
        "  <PropertyGroup><IsTestProject>true</IsTestProject><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>`n" +
        "  <Target Name=""GameServerHostile{0}""><Error Text=""" + $Marker + ": consumer {1} reached `$(MSBuildProjectName)"" /></Target>`n" +
        "</Project>`n"
    [IO.File]::WriteAllText((Join-Path $consumer "Directory.Build.props"), ($hostile -f "Props", "Directory.Build.props"))
    [IO.File]::WriteAllText((Join-Path $consumer "Directory.Build.targets"), ($hostile -f "Targets", "Directory.Build.targets"))
    Write-Host ("check-build-seal: " + $copied + " files copied to " + $framework)

    $log = Join-Path $root "build.log"
    Push-Location $framework
    try {
        & $dotnet build GameServer.slnx -c Release -nologo -tl:off -nodeReuse:false 2>&1 | Tee-Object -FilePath $log | Out-Host
        $buildExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    $text = Get-Content -Raw $log
    $warnings = -1
    $errors = -1
    if ($text -match "(?m)^\s*(\d+) Warning\(s\)") { $warnings = [int]$Matches[1] }
    if ($text -match "(?m)^\s*(\d+) Error\(s\)") { $errors = [int]$Matches[1] }
    $leaked = $text.Contains($Marker)

    Write-Host ""
    Write-Host ("check-build-seal: exit " + $buildExit + ", " + $errors + " error(s), " + $warnings + " warning(s), hostile marker " +
        $(if ($leaked) { "FOUND" } else { "absent" }))
    if ($buildExit -eq 0 -and $errors -eq 0 -and $warnings -eq 0 -and -not $leaked) {
        Write-Host "check-build-seal: sealed." -ForegroundColor Green
        $exitCode = 0
    }
    else {
        Write-Host "check-build-seal: FAILED (see the build output above)." -ForegroundColor Red
    }
}
finally {
    if ($Keep) {
        Write-Host ("check-build-seal: layout kept at " + $root)
    }
    elseif (Test-Path $root) {
        # Windows PowerShell 5.1 cannot delete paths over 260 characters without the extended-length prefix.
        $deletePath = $root
        if ($onWindows) { $deletePath = "\\?\" + $root }
        [IO.Directory]::Delete($deletePath, $true)
    }
}

exit $exitCode
