using GameServer.Platform.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Commands;

/// <summary>
/// Runs the <c>afterExecute</c> hook of a command endpoint (cache refresh, metric, log) after the command committed or was
/// replayed. The commit already happened, so a failing hook is logged and the response still reports it (review minor 6):
/// answering 5xx would make the client retry a command that succeeded.
/// </summary>
internal static class AfterExecuteHook
{
    private const string LogCategory = "GameServer.Platform.Web.Commands.AfterExecuteHook";

    public static async Task RunAsync(Func<CommandExecution, HttpContext, Task> hook, CommandExecution execution, HttpContext http, string operation)
    {
        try
        {
            await hook(execution, http);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested))
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory).LogError(
                ex,
                "After-execute hook of {Operation} failed for committed receipt {ReceiptId}; the response still reports the commit.",
                operation,
                execution.ReceiptId);
        }
    }
}
