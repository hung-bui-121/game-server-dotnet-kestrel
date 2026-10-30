<#
.SYNOPSIS
  Starts N SampleHost instances on one local Couchbase (store Couchbase), each on its own port and instance id.
.DESCRIPTION
  The Couchbase password is read from the environment variable named by -PasswordVariable (default COUCHBASE_PW,
  process or user scope) and handed to each host as Couchbase__Password. It is never written to a file (OPS-08), and
  every variable this script sets is restored in the caller's session afterwards, even on failure.
  Hosts run from a copy of the build output (<StateDir>\app) so `dotnet build/test` keeps working while they run.
  PIDs and start times go to <StateDir>\hosts.json (written after each start) for stop-hosts.ps1.
.EXAMPLE
  ./tools/local/start-hosts.ps1 -Count 2
  ./tools/local/start-hosts.ps1 -Count 1 -Bucket gameserver-dev -BasePort 5080 -Otlp http://localhost:4317
#>
param(
    [int]$Count = 2,
    [int]$BasePort = 5080,
    [string]$Bucket = "gameserver-dev",
    [string]$ConnectionString = "couchbase://localhost",
    [string]$Username = "Administrator",
    [string]$PasswordVariable = "COUCHBASE_PW",
    [string]$Otlp = "",
    [string]$Dotnet = (Join-Path $HOME ".dotnet10\dotnet.exe"),
    [string]$StateDir = (Join-Path ([IO.Path]::GetTempPath()) "gameserver-local"),
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$hostDir = Join-Path $repo "samples\SampleHost"
$appDir = Join-Path $StateDir "app"
$dll = Join-Path $appDir "SampleHost.dll"
$state = Join-Path $StateDir "hosts.json"

$password = [Environment]::GetEnvironmentVariable($PasswordVariable, "Process")
if (-not $password) { $password = [Environment]::GetEnvironmentVariable($PasswordVariable, "User") }
if (-not $password) { throw "Environment variable $PasswordVariable is not set (Couchbase password)." }

New-Item -ItemType Directory -Force $StateDir | Out-Null
if (Test-Path $state) { throw "Hosts already running (see $state); run stop-hosts.ps1 first." }

if (-not $NoBuild) {
    & $Dotnet build (Join-Path $hostDir "SampleHost.csproj") -c Release -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SampleHost build failed." }
}

if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
Copy-Item -Recurse (Join-Path $hostDir "bin\Release\net10.0") $appDir

# Child processes inherit this session's environment: set per host, then restore whatever the caller had.
$names = "ASPNETCORE_ENVIRONMENT", "GameServer__Store", "Couchbase__ConnectionString", "Couchbase__Username", "Couchbase__Password",
    "Couchbase__BucketName", "Couchbase__ProvisionOnStartup", "Observability__InstanceId", "Observability__Otlp__Endpoint"
$saved = @{}
foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n, "Process") }
$hosts = @()
try {
    for ($i = 0; $i -lt $Count; $i++) {
        $port = $BasePort + $i
        $id = [string][char]([int][char]'a' + $i)
        # Dev flavor (appsettings.Dev.json); the variables below still override its Couchbase settings (FR-033).
        $env:ASPNETCORE_ENVIRONMENT = "Dev"
        $env:GameServer__Store = "Couchbase"
        $env:Couchbase__ConnectionString = $ConnectionString
        $env:Couchbase__Username = $Username
        $env:Couchbase__Password = $password
        $env:Couchbase__BucketName = $Bucket
        $env:Couchbase__ProvisionOnStartup = "true"
        $env:Observability__InstanceId = $id
        $env:Observability__Otlp__Endpoint = $Otlp
        $out = Join-Path $StateDir "host-$id.out"
        $err = Join-Path $StateDir "host-$id.err"
        $p = Start-Process -FilePath $Dotnet -ArgumentList "`"$dll`"", "--urls", "http://localhost:$port" -WorkingDirectory $hostDir `
            -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -WindowStyle Hidden
        $hosts += [pscustomobject]@{ Id = $id; Pid = $p.Id; StartTime = $p.StartTime.ToString("o"); Url = "http://localhost:$port/"; Out = $out; Err = $err }
        ConvertTo-Json -InputObject @($hosts) | Set-Content -Encoding utf8 $state
    }
}
finally {
    foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n], "Process") }
}

foreach ($h in $hosts) {
    $ready = $false
    for ($t = 0; $t -lt 120 -and -not $ready; $t++) {
        Start-Sleep -Milliseconds 500
        if ($null -eq (Get-Process -Id $h.Pid -ErrorAction SilentlyContinue)) { throw "Host $($h.Id) exited. See $($h.Err)" }
        try { $ready = (Invoke-WebRequest -UseBasicParsing "$($h.Url)health/ready" -TimeoutSec 2).StatusCode -eq 200 } catch { }
    }
    if (-not $ready) { throw "Host $($h.Id) not ready after 60 s. See $($h.Out)" }
    Write-Host "host $($h.Id) ready: $($h.Url) (pid $($h.Pid), logs $hostDir\logs)"
}
Write-Host "URLs: $(($hosts | ForEach-Object Url) -join ',')"
