using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameServer.Commands;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Web.Commands;
using GameServer.Protocol.Core.V1;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T049 (FR-030, FR-031, SC-005, US4-1..US4-3, BC-11): a result or receipt over its limit is the HTTP 200 rejection
/// <c>COMMAND_OUTPUT_TOO_LARGE</c> with <c>part</c>/<c>bytes</c>/<c>limit</c> in <c>CommandResponse.details</c> (and the GM response),
/// nothing written, one Error line and one count; exactly at the limit commits; a module rejection's details reach the client.
/// </summary>
public sealed class CommandOutputLimitTests
{
    private const int ResultLimit = 512;
    private const int ReceiptLimit = 4096;

    /// <summary>A wire result carrying a note of n characters is 1 tag byte + 2 length bytes + n bytes for 128 ≤ n &lt; 16384.</summary>
    private const int ResultFraming = 3;

    [Theory]
    [InlineData(ResultLimit, false)]
    [InlineData(ResultLimit + 1, true)]
    public async Task A_result_over_the_limit_is_refused_and_one_at_the_limit_commits(int resultBytes, bool refused)
    {
        await using var host = await OutputLimitHost.StartAsync();
        using var counts = new OutputMetrics(host);
        var player = NewPlayerId();

        var response = await CommandAsync(host, player, OutputLimitHost.ResultRoute, new Wire.IncrementRequest { By = resultBytes - ResultFraming });

        if (!refused)
        {
            response.Code.ShouldBe("OK");
            response.Details.ShouldBeEmpty();
            Counter(host, player).ShouldBe(1);
            return;
        }

        ShouldBeOutputTooLarge(response, "result", resultBytes, ResultLimit);
        Counter(host, player).ShouldBe(0);
        host.Store.Count("receipts").ShouldBe(0);
        counts.Of(OutputModule.ResultOperation, "result").ShouldBe(1);
        ErrorLines(host).ShouldBe(
        [
            $"Command {OutputModule.ResultOperation} refused with COMMAND_OUTPUT_TOO_LARGE: result is {resultBytes} bytes, limit {ResultLimit}. " +
            "Fix: return less or raise GameServer:Commands:ResultCompactMaxBytes (PCR platform §22).",
        ]);
    }

    [Fact]
    public async Task A_receipt_one_byte_over_the_limit_is_refused_and_one_exactly_at_the_limit_commits()
    {
        await using var host = await OutputLimitHost.StartAsync();
        using var counts = new OutputMetrics(host);
        const int Probe = 4000;

        // Many outbox entries plus one padding entry; the probe gives the receipt size, every other input has a fixed length.
        var probe = await CommandAsync(host, NewPlayerId(), OutputLimitHost.FloodRoute, Flood(Probe));
        var probeBytes = int.Parse(probe.Details["bytes"], CultureInfo.InvariantCulture);
        var atLimit = Probe - (probeBytes - ReceiptLimit);
        var exact = NewPlayerId();
        var over = NewPlayerId();

        var committed = await CommandAsync(host, exact, OutputLimitHost.FloodRoute, Flood(atLimit));
        var refused = await CommandAsync(host, over, OutputLimitHost.FloodRoute, Flood(atLimit + 1));

        committed.Code.ShouldBe("OK");
        committed.Details.ShouldBeEmpty();
        Counter(host, exact).ShouldBe(1);
        host.Store.Peek(ReceiptDocument.KeyFor(committed.ReceiptId)).ShouldNotBeNull();
        ShouldBeOutputTooLarge(refused, "receipt", ReceiptLimit + 1, ReceiptLimit);
        Counter(host, over).ShouldBe(0);
        host.Store.Count("receipts").ShouldBe(1);
        counts.Of(OutputModule.FloodOperation, "receipt").ShouldBe(2);
        ErrorLines(host).ShouldContain(
            $"Command {OutputModule.FloodOperation} refused with COMMAND_OUTPUT_TOO_LARGE: receipt is {ReceiptLimit + 1} bytes, limit {ReceiptLimit}. " +
            "Fix: return less or raise GameServer:Commands:ReceiptMaxBytes (PCR platform §22).");
    }

    [Fact]
    public async Task A_raised_receipt_limit_commits_the_same_command()
    {
        await using var host = await OutputLimitHost.StartAsync(s => s["GameServer:Commands:ReceiptMaxBytes"] = "16384");
        var player = NewPlayerId();

        var response = await CommandAsync(host, player, OutputLimitHost.FloodRoute, Flood(ReceiptLimit));

        response.Code.ShouldBe("OK");
        response.Details.ShouldBeEmpty();
        Counter(host, player).ShouldBe(1);
        ErrorLines(host).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_gm_command_over_the_limit_answers_200_with_the_code_and_details()
    {
        await using var host = await OutputLimitHost.StartAsync();
        var player = NewPlayerId();
        var request = new HttpRequestMessage(HttpMethod.Post, OutputLimitHost.GmFloodRoute.Template.Replace("{playerId}", player, StringComparison.Ordinal))
        {
            Content = JsonContent.Create(new { count = 20, length = 5000, reason = "support ticket" }),
        };
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));

        var response = await host.GmClient("staff", OutputModule.Permission).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().ShouldBe("COMMAND_OUTPUT_TOO_LARGE");
        body.GetProperty("codeNumber").GetInt32().ShouldBe(23);
        body.GetProperty("receiptId").ValueKind.ShouldBe(JsonValueKind.Null);
        var details = body.GetProperty("details");
        details.GetProperty("part").GetString().ShouldBe("receipt");
        details.GetProperty("limit").GetString().ShouldBe("4096");
        int.Parse(details.GetProperty("bytes").GetString()!, CultureInfo.InvariantCulture).ShouldBeGreaterThan(5000);
        Counter(host, player).ShouldBe(0);
        host.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task A_module_rejection_sends_its_details_in_the_command_response()
    {
        await using var host = await OutputLimitHost.StartAsync();

        var response = await CommandAsync(host, NewPlayerId(), OutputLimitHost.RejectRoute, new Wire.IncrementRequest { Note = "sold_out" });

        response.Code.ShouldBe(OutputModule.Rejected.Name);
        response.CodeNumber.ShouldBe(OutputModule.Rejected.Number);
        response.ReceiptId.ShouldBeEmpty();
        response.Details.ShouldBe(new Dictionary<string, string> { ["reason"] = "sold_out" });
        ErrorLines(host).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_creating_login_whose_receipt_is_over_the_limit_answers_internal_with_the_creation_rule_line()
    {
        await using var host = await SampleTestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule(new StartingStateModule()),
            services: s => s.AddSingleton(StartingStateBehaviour.FloodOutbox(40)));

        var refused = await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await refused.ReadErrorAsync()).Code.ShouldBe("INTERNAL");
        var line = host.LogEntries.Where(e => e.Category == AccountWriter.LogCategory && e.Level == LogLevel.Error).ShouldHaveSingleItem().Message;
        line.ShouldContain($"Command 'identity.create_account (initializer {StartingStateModule.InitializerName})' receipt would be ");
        line.ShouldContain($"ReceiptMaxBytes is {ReceiptLimit}: the account writer refuses the creation and the login answers INTERNAL.");
    }

    private static void ShouldBeOutputTooLarge(CommandResponse response, string part, int bytes, int limit)
    {
        response.Code.ShouldBe("COMMAND_OUTPUT_TOO_LARGE");
        response.CodeNumber.ShouldBe(23);
        response.ReceiptId.ShouldBeEmpty();
        response.Result.IsEmpty.ShouldBeTrue();
        response.Updates.ShouldBeEmpty();
        response.Details.ShouldBe(new Dictionary<string, string>
        {
            ["part"] = part,
            ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        });
    }

    private static Wire.IncrementRequest Flood(int paddingLength) => new() { By = paddingLength };

    private static async Task<CommandResponse> CommandAsync(TestHost host, string playerId, RouteDeclaration route, Wire.IncrementRequest request)
    {
        var response = await host.PlayerClient(playerId).PostProtobufAsync(route.Template, request, ProtobufHttp.NewRequestId());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadProtobufAsync<CommandResponse>();
    }

    private static long Counter(TestHost host, string playerId) =>
        host.Store.Peek(new Persistence.DocumentKey("pa_counter", playerId))?["state"]?["value"]?.GetValue<long>() ?? 0;

    private static string[] ErrorLines(TestHost host) =>
        host.LogEntries.Where(e => e.Category == CommandRunner.LogCategory && e.Level == LogLevel.Error).Select(e => e.Message).ToArray();

    /// <summary>Same length for every player, so the receipt size depends on the padding only.</summary>
    private static string NewPlayerId() => "p_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary><c>command_output_too_large_total</c> of one host's meter (parallel classes share the meter name).</summary>
    private sealed class OutputMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Operation, string Part)> _counts = new();

        public OutputMetrics(TestHost host)
        {
            var meter = host.Services.GetRequiredService<PlatformMetrics>().Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "command_output_too_large_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? operation = null;
                string? part = null;
                foreach (var tag in tags)
                {
                    operation = tag.Key == "operation" ? (string?)tag.Value : operation;
                    part = tag.Key == "part" ? (string?)tag.Value : part;
                }

                _counts.Enqueue((operation!, part!));
            });
            _listener.Start();
        }

        public int Of(string operation, string part) => _counts.Count(c => c.Operation == operation && c.Part == part);

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>Request of the output-limit commands: padding/result length and the reject reason.</summary>
public sealed record OutputRun(int Length, string Reason);

/// <summary>GM body of <c>output.gm_flood</c>.</summary>
public sealed record OutputGmBody(int Count, int Length, string? Reason) : IGmCommandBody;

/// <summary>
/// Commands whose result or receipt size a test chooses: <c>output.result</c> returns a note of the given length,
/// <c>output.flood</c> (and the GM <c>output.gm_flood</c>) adds 20 small outbox entries plus one padding entry, and
/// <c>output.reject</c> rejects with a business code and details. Each touches the counter.
/// </summary>
public sealed class OutputModule : IGameModule
{
    public const string ResultOperation = "output.result";
    public const string FloodOperation = "output.flood";
    public const string Permission = "output.edit";
    public const int FloodEntries = 20;

    public static readonly GameServer.Errors.ErrorCode Rejected = new(21150, "OUTPUT_REJECTED", 200, false, "Rejected.");

    public static CommandDescriptor ResultDescriptor { get; } = new(ResultOperation, touches: [CounterAggregates.Counter]);

    public static CommandDescriptor FloodDescriptor { get; } = new(FloodOperation, touches: [CounterAggregates.Counter]);

    public static CommandDescriptor GmFloodDescriptor { get; } =
        new("output.gm_flood", touches: [CounterAggregates.Counter], profile: GameServer.Actors.SecurityProfile.Gm, permission: Permission);

    public static CommandDescriptor RejectDescriptor { get; } = new("output.reject", touches: [CounterAggregates.Counter]);

    public ModuleDescriptor Describe() => new()
    {
        Name = "output",
        DependsOn = ["sample.counter"],
        Commands =
        [
            Command.Define<OutputRun, string>(ResultDescriptor).HandledBy<ResultHandler>(),
            Command.Define<OutputRun, string>(FloodDescriptor).HandledBy<FloodHandler>(),
            Command.Define<OutputRun, string>(GmFloodDescriptor).HandledBy<FloodHandler>(),
            Command.Define<OutputRun, string>(RejectDescriptor).HandledBy<RejectHandler>(),
        ],
        Permissions = [Permission],
        ErrorCodes = new GameServer.Errors.ErrorCodeSet(21150, 21159, Rejected),
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }

    private static void Increment(ICommandContext context)
    {
        var counter = context.Get<CounterState>(CounterAggregates.Counter);
        context.Set(CounterAggregates.Counter, counter with { Value = counter.Value + 1 });
    }

    public sealed class ResultHandler : ICommandHandler<OutputRun, string>
    {
        public ValueTask<CommandOutcome<string>> HandleAsync(OutputRun request, ICommandContext context)
        {
            Increment(context);
            return ValueTask.FromResult(CommandOutcome<string>.Ok(new string('r', request.Length)));
        }
    }

    public sealed class FloodHandler : ICommandHandler<OutputRun, string>
    {
        public ValueTask<CommandOutcome<string>> HandleAsync(OutputRun request, ICommandContext context)
        {
            Increment(context);
            for (var i = 0; i < FloodEntries; i++)
            {
                context.AddOutbox("output_log", new { n = i % 10 });
            }

            context.AddOutbox("output_log", new { text = new string('o', request.Length) });
            if (context.Actor.ActorType == GameServer.Actors.ActorType.Gm)
            {
                context.AddAudit(Permission, GmFloodDescriptor.Operation, "test");
            }

            return ValueTask.FromResult(CommandOutcome<string>.Ok(string.Empty));
        }
    }

    public sealed class RejectHandler : ICommandHandler<OutputRun, string>
    {
        public ValueTask<CommandOutcome<string>> HandleAsync(OutputRun request, ICommandContext context)
        {
            Increment(context);
            return ValueTask.FromResult(CommandOutcome<string>.Reject(Rejected, new Dictionary<string, string> { ["reason"] = request.Reason }));
        }
    }
}

/// <summary>A sample host (Identity + Sample.Counter) with <see cref="OutputModule"/> mapped as Player and GM commands.</summary>
public static class OutputLimitHost
{
    public static readonly RouteDeclaration ResultRoute = RouteDeclaration.Command(OutputModule.ResultDescriptor, "/api/v1/test/output/result");
    public static readonly RouteDeclaration FloodRoute = RouteDeclaration.Command(OutputModule.FloodDescriptor, "/api/v1/test/output/flood");
    public static readonly RouteDeclaration RejectRoute = RouteDeclaration.Command(OutputModule.RejectDescriptor, "/api/v1/test/output/reject");
    public static readonly RouteDeclaration GmFloodRoute = RouteDeclaration.Command(OutputModule.GmFloodDescriptor, "/gm/v1/players/{playerId}/test/output/flood");

    public static Task<TestHost> StartAsync(Action<Dictionary<string, string?>>? settings = null) =>
        SampleTestHost.StartAsync(
            modules: b =>
            {
                SampleTestHost.DefaultModules(b);
                b.AddModule(new OutputModule());
                b.DeclareRoutes(ResultRoute, FloodRoute, RejectRoute, GmFloodRoute);
            },
            settings: settings,
            afterUseGameServer: app =>
            {
                foreach (var route in new[] { ResultRoute, FloodRoute, RejectRoute })
                {
                    app.MapPlayerCommand<Wire.IncrementRequest, OutputRun, string>(
                        route,
                        request => new OutputRun((int)request.By, request.Note),
                        result => new Wire.IncrementRequest { Note = result });
                }

                app.MapGmCommand<OutputGmBody, OutputRun, string>(GmFloodRoute, body => new OutputRun(body.Length, string.Empty));
            });
}
