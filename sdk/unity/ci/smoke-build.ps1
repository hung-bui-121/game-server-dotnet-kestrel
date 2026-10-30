<#
  CI-02: Unity IL2CPP smoke build (Android arm64, managed stripping High) of the client SDK through the KestrelDemo sample
  client (samples/SampleGame/KestrelDemo, Unity 6), which references the SDK package from disk.

  Usage:
    ./smoke-build.ps1 -Unity "<path>\Unity.exe"                 # Android smoke build -> KestrelDemo/Build/KestrelDemo.apk
    ./smoke-build.ps1 -Unity "<path>\Unity.exe" -CompileOnly    # import + compile only
    ./smoke-build.ps1 -Unity "<path>\Unity.exe" -Setup          # (re)create the demo scene and settings
    ./smoke-build.ps1 -Unity "<path>\Unity.exe" -E2E [-ServerUrl http://localhost:5080/]   # flow against a live SampleHost
  Exit code 0 = success. Logs: <LogDir>\*.log. The project must not be open in an Editor.
  Windows: keep the project path short; IL2CPP/Gradle paths under Library exceed MAX_PATH otherwise.
#>
param(
    [Parameter(Mandatory = $true)][string]$Unity,
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\..\..\samples\SampleGame\KestrelDemo"),
    [string]$LogDir = (Join-Path ([IO.Path]::GetTempPath()) "kestreldemo-ci"),
    [switch]$CompileOnly,
    [switch]$Setup,
    [switch]$E2E,
    [string]$ServerUrl = "http://localhost:5080/"
)

$ErrorActionPreference = "Stop"
$project = (Resolve-Path $ProjectPath).Path
New-Item -ItemType Directory -Force $LogDir | Out-Null

if ($E2E) {
    # Sample flow over UnityWebRequest against a live SampleHost (start it first: dotnet run --project samples/SampleHost).
    $env:GS_SERVER_URL = $ServerUrl
    & $Unity -batchmode -nographics -projectPath $project -executeMethod KestrelDemoBuild.EndToEnd -logFile (Join-Path $LogDir "e2e.log") | Out-Null
} elseif ($Setup) {
    & $Unity -batchmode -nographics -projectPath $project -executeMethod KestrelDemoBuild.SetupDemo -quit -logFile (Join-Path $LogDir "setup.log") | Out-Null
} elseif ($CompileOnly) {
    & $Unity -batchmode -nographics -projectPath $project -quit -logFile (Join-Path $LogDir "compile.log") | Out-Null
} else {
    & $Unity -batchmode -nographics -projectPath $project -buildTarget Android -executeMethod KestrelDemoBuild.BuildAndroid -logFile (Join-Path $LogDir "build.log") | Out-Null
}
exit $LASTEXITCODE
