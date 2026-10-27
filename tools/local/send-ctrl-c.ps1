<#
.SYNOPSIS
  Asks a console process to shut down gracefully (ACC-28 drill on a host started by start-hosts.ps1).
.DESCRIPTION
  Sends Ctrl+Break by default: .NET maps it to SIGQUIT and the ASP.NET Core console lifetime stops the host the same way
  as SIGTERM in a container. Ctrl+C (-Signal C) is ignored by processes that inherited "ignore Ctrl+C" from a
  non-interactive parent, which is the case for hosts started from automation. Runs in a child PowerShell because
  attaching to another console detaches the caller's own.
.EXAMPLE
  ./tools/local/send-ctrl-c.ps1 -ProcessId 12345
#>
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [ValidateSet("Break", "C")][string]$Signal = "Break"
)
$ctrlEvent = if ($Signal -eq "C") { 0 } else { 1 }
$script = @"
`$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CtrlSender {
    private delegate bool Handler(uint ctrlType);
    private static readonly Handler Swallow = _ => true; // keep this helper alive when the event reaches it too
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    public static int Send(uint pid, uint ev) {
        FreeConsole();
        if (!AttachConsole(pid)) return 2;
        SetConsoleCtrlHandler(Swallow, true);
        return GenerateConsoleCtrlEvent(ev, 0) ? 0 : 3;
    }
}
'@
`$code = [CtrlSender]::Send($ProcessId, $ctrlEvent)
Start-Sleep -Milliseconds 500
exit `$code
"@
# -EncodedCommand: Windows PowerShell 5.1 joins -ArgumentList without quoting, which would strip the quotes in the C# code.
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
$child = Start-Process powershell -ArgumentList "-NoProfile", "-NonInteractive", "-EncodedCommand", $encoded -WindowStyle Hidden -PassThru -Wait
if ($child.ExitCode -ne 0) { throw "Could not signal $ProcessId (exit $($child.ExitCode))." }
Write-Host "Ctrl+$Signal sent to $ProcessId at $([DateTime]::UtcNow.ToString('HH:mm:ss.fff'))Z"
