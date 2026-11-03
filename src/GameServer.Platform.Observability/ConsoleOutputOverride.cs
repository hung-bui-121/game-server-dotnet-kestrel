using Serilog.Core;
using Serilog.Events;

namespace GameServer.Platform.Observability;

/// <summary>
/// Test seam of the console sink (research V6): registered in the host's services, the console sink writes to this writer instead
/// of stdout, through the same bounded, dropping queue. Never <c>Console.SetOut</c>: it is process-wide and unsafe in parallel tests.
/// Serilog.Sinks.Console 6.1.1 takes no writer and Serilog 4.3 core has no TextWriter sink, hence the sink below.
/// </summary>
internal sealed class ConsoleOutputOverride(TextWriter writer)
{
    public ILogEventSink CreateSink() => new WriterSink(writer);

    /// <summary>Runs on the single worker of the async wrapper, so the writer needs no lock.</summary>
    private sealed class WriterSink(TextWriter writer) : ILogEventSink
    {
        private readonly ConsoleLineFormatter _formatter = new(colours: false);

        public void Emit(LogEvent logEvent) => _formatter.Format(logEvent, writer);
    }
}
