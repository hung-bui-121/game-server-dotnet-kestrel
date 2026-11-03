using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform.Observability;
using GameServer.Platform.Web.Requests;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>
/// Slice 013 T044 (FR-085, FR-087, FR-088, US3-5, US3-6, SC-008, edge case "stdout redirected"): the console sink writes one
/// line per request through its own bounded, dropping queue; a stalled console never blocks a request; the file is unchanged.
/// The console goes to an internal writer (<see cref="ConsoleOutputOverride"/>), never to the process stdout.
/// </summary>
public sealed class ConsoleSinkTests
{
    private const int Requests = 2000;

    [Fact]
    public async Task One_console_line_per_request()
    {
        var console = new LockedWriter();
        var requestIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("D")).ToList();
        await using (var observed = await ObservedHost.StartAsync(
            settings: ConsoleOn,
            services: s => s.AddSingleton(new ConsoleOutputOverride(console))))
        {
            foreach (var requestId in requestIds)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/time");
                request.Headers.Add("X-Request-Id", requestId);
                (await observed.Host.Client().SendAsync(request)).EnsureSuccessStatusCode();
            }
        }

        var lines = console.ToString().Split(Environment.NewLine);
        foreach (var requestId in requestIds)
        {
            lines.Count(l => l.Contains("req=" + requestId[..8], StringComparison.Ordinal)).ShouldBe(1);
        }

        var line = lines.First(l => l.Contains("req=" + requestIds[0][..8], StringComparison.Ordinal));
        line.ShouldMatch(@"^\d\d:\d\d:\d\d\.\d{3} INF GET /v1/time req=[0-9a-f]{8} -> 200 OK \d+\.\d ms$");
    }

    [Fact]
    public async Task A_blocked_console_never_blocks_requests_and_drops_are_counted()
    {
        const int bufferSize = 10;
        using var console = new BlockingWriter();
        var dropped = LogSinkMonitor.Instance.DroppedCount;
        await using var observed = await ObservedHost.StartAsync(
            settings: s =>
            {
                ConsoleOn(s);
                s["Observability:Logging:Console:BufferSize"] = bufferSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            },
            services: s => s.AddSingleton(new ConsoleOutputOverride(console)));
        var client = observed.Host.Client();
        try
        {
            var watch = Stopwatch.StartNew();
            var statuses = new ConcurrentBag<int>();
            await Parallel.ForEachAsync(Enumerable.Range(0, Requests), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (_, ct) =>
                statuses.Add((int)(await client.GetAsync("/v1/time", ct)).StatusCode));
            watch.Stop();

            statuses.Count.ShouldBe(Requests);
            statuses.ShouldAllBe(status => status == 200);
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            console.Entered.ShouldBeGreaterThan(0);

            // Bounded memory: the worker holds one event in the blocked write and the queue at most bufferSize, so every other
            // request event was dropped (other tests of the process only add drops; process memory is not measured here because
            // parallel tests allocate too — the opt-in RequestLogPerformanceTests measures it alone).
            (LogSinkMonitor.Instance.DroppedCount - dropped).ShouldBeGreaterThanOrEqualTo(Requests - bufferSize - 1);
        }
        finally
        {
            // Disposing the host drains the queue: let the writer go first.
            console.Release();
        }
    }

    [Fact]
    public async Task With_the_console_on_the_log_file_is_unchanged()
    {
        var off = await FileEventsAsync(consoleOn: false);
        var on = await FileEventsAsync(consoleOn: true);

        on.Count.ShouldBeGreaterThan(0);
        on.ShouldBe(off, ignoreOrder: true);
        on.ShouldContain(e => e.StartsWith(RequestLog.Category, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Operational_logs_reach_no_other_provider()
    {
        var console = new LockedWriter();
        await using var observed = await ObservedHost.StartAsync(settings: ConsoleOn, services: s => s.AddSingleton(new ConsoleOutputOverride(console)));

        (await observed.Host.Client().GetAsync("/v1/time")).EnsureSuccessStatusCode();
        (await observed.Host.Client().GetAsync("/api/v1/nope")).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);

        // The kit's capturing provider sits in builder.Logging: Serilog (writeToProviders: false) never forwards to it.
        observed.Host.LogEntries.ShouldNotContain(e => e.Category != typeof(GameServer.Platform.Testing.FileDocumentStore).FullName);
    }

    private static void ConsoleOn(Dictionary<string, string?> settings) => settings["Observability:Logging:Console:Enabled"] = "true";

    /// <summary>Every file event as <c>SourceContext|@l|@mt</c> (+ the stable request fields); timestamps and ids left out.</summary>
    private static async Task<List<string>> FileEventsAsync(bool consoleOn)
    {
        using var logFolder = new TempLogDirectory();
        var console = new LockedWriter();
        await using (var observed = await ObservedHost.StartAsync(
            logFolder.Path,
            settings: s =>
            {
                s["Observability:Logging:MinimumLevel"] = "Information";
                s["Observability:Logging:Console:Enabled"] = consoleOn ? "true" : "false";
            },
            services: s => s.AddSingleton(new ConsoleOutputOverride(console))))
        {
            var client = observed.Host.Client();
            (await client.GetAsync("/v1/time")).EnsureSuccessStatusCode();
            (await client.GetAsync("/api/v1/nope")).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
            (await client.GetAsync(ObservedHost.ProbeRoute + "?value=x")).EnsureSuccessStatusCode();
        }

        (console.ToString().Length > 0).ShouldBe(consoleOn);
        return ObservedHost.ReadAllLogs(logFolder.Path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!)
            .Select(e => string.Join(
                '|',
                e["SourceContext"]?.GetValue<string>(),
                e["@l"]?.GetValue<string>(),
                e["@mt"]!.GetValue<string>(),
                e["Route"]?.GetValue<string>(),
                e["ResultCode"]?.GetValue<string>()))
            .ToList();
    }

    /// <summary>A writer the async worker and the test may share.</summary>
    private sealed class LockedWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }

        public override string ToString()
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>A stalled console (paused terminal, unread pipe): every write waits until released.</summary>
    private sealed class BlockingWriter : TextWriter
    {
        private readonly ManualResetEventSlim _released = new();
        private int _entered;

        public int Entered => Volatile.Read(ref _entered);

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Block();

        public override void Write(string? value) => Block();

        public override void Write(ReadOnlySpan<char> buffer) => Block();

        public void Release() => _released.Set();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _released.Set();
                _released.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Block()
        {
            Interlocked.Increment(ref _entered);
            _released.Wait();
        }
    }
}
