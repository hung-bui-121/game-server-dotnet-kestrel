using System.Text;
using GameServer.Actors;
using GameServer.Config;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Hosting;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sample.Counter.Adapters;
using SampleGame.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 014 T039 (FR-030, contracts/wiregen-cli.md §1, research R9): <c>--export-wire-contract &lt;path&gt;</c> writes the
/// catalog export and exits; it never starts a hosted service, resolves the store or game config, or serves.
/// </summary>
public sealed class WireContractExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-export-" + Guid.NewGuid().ToString("N"));

    public WireContractExportTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task The_export_file_is_the_catalog_export_and_nothing_starts()
    {
        var file = Path.Combine(_folder, "wire.json");
        var probe = new StartProbe();
        var expected = ExpectedExport();
        var app = SampleTestHost.BuildApp(SampleHostModules, s => s[GameServerCommandLine.ExportPathKey] = file, services: s =>
        {
            s.AddSingleton<IHostedService>(probe);

            // Export mode must not resolve them: either factory would fail the run.
            s.AddSingleton<ITransactionalDocumentStore>(_ => throw new InvalidOperationException("the export opened the store"));
            s.AddSingleton<IGameConfig>(_ => throw new InvalidOperationException("the export loaded the game config"));
        });
        var (output, error) = (new StringWriter(), new StringWriter());

        var code = await GameServerRunExtensions.ExportAsync(app, output, error);

        code.ShouldBe(HostExitCodes.Ok, error.ToString());
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[0].ShouldBe((byte)'{', "UTF-8 without BOM");
        bytes.ShouldNotContain((byte)'\r');
        Encoding.UTF8.GetString(bytes).ShouldBe(expected);
        expected.ShouldContain("\"name\": \"battle.end\"");
        expected.ShouldContain("\"name\": \"cheat.grant\"");
        probe.Started.ShouldBeFalse();
        output.ToString().ShouldContain("export: ");
    }

    // T086 minor 5: the export replaces the file in one step, so the old file is never truncated in place. A reader holding it
    // keeps the old bytes; on Windows the replace of an open file is refused (exit 1, old file intact), elsewhere it succeeds.
    [Fact]
    public async Task The_export_replaces_an_existing_file_in_one_step_and_leaves_no_temp_file()
    {
        var file = Path.Combine(_folder, "wire.json");
        await File.WriteAllTextAsync(file, "old export");
        var app = SampleTestHost.BuildApp(settings: s => s[GameServerCommandLine.ExportPathKey] = file);
        var error = new StringWriter();
        int code;
        string seenByReader;
        using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            code = await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, error);
            using var text = new StreamReader(reader);
            seenByReader = await text.ReadToEndAsync();
        }

        seenByReader.ShouldBe("old export");
        if (code == HostExitCodes.Ok)
        {
            (await File.ReadAllTextAsync(file)).ShouldStartWith("{");
        }
        else
        {
            code.ShouldBe(HostExitCodes.InvalidConfiguration);
            error.ToString().ShouldContain($"[export] the export file '{file}' cannot be written:");
            (await File.ReadAllTextAsync(file)).ShouldBe("old export");
        }

        Directory.GetFiles(_folder).ShouldBe([file]);
    }

    [Fact]
    public async Task The_export_overwrites_an_existing_file()
    {
        var file = Path.Combine(_folder, "wire.json");
        await File.WriteAllTextAsync(file, "old export");
        var app = SampleTestHost.BuildApp(settings: s => s[GameServerCommandLine.ExportPathKey] = file);
        var error = new StringWriter();

        (await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, error)).ShouldBe(HostExitCodes.Ok, error.ToString());

        (await File.ReadAllTextAsync(file)).ShouldStartWith("{");
        Directory.GetFiles(_folder).ShouldBe([file]);
    }

    [Fact]
    public async Task A_declaration_fault_exits_1_and_writes_no_file()
    {
        var file = Path.Combine(_folder, "wire.json");
        var app = SampleTestHost.BuildApp(
            SampleTestHost.WithRoutes(RouteDeclaration.Post("probe.old", "/api/probe/old", SecurityProfile.Public, RouteSurface.Host)),
            s => s[GameServerCommandLine.ExportPathKey] = file);
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain(" - [route probe.old] template '/api/probe/old' has no version segment. Fix: prefix with /v{n}/ (API-02).");
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task An_invalid_composition_exits_1_and_writes_no_file()
    {
        var file = Path.Combine(_folder, "wire.json");
        var app = SampleTestHost.BuildApp(settings: s =>
        {
            s[GameServerCommandLine.ExportPathKey] = file;
            s["GameServer:EnvironmentId"] = string.Empty;
        });
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain("[deployment] GameServer:EnvironmentId '' is missing or invalid.");
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_output_folder_exits_1_naming_it()
    {
        var missing = Path.Combine(_folder, "absent");
        var app = SampleTestHost.BuildApp(settings: s => s[GameServerCommandLine.ExportPathKey] = Path.Combine(missing, "wire.json"));
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain($"[export] the folder of the export file '{Path.Combine(missing, "wire.json")}' does not exist ({missing}).");
        Directory.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_mode_without_a_parsed_path_exits_1()
    {
        var app = SampleTestHost.BuildApp();
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain("[export] no output file: export mode needs --export-wire-contract <path>");
    }

    [Fact]
    public void The_parsed_command_line_carries_the_path_into_the_host_configuration()
    {
        var file = Path.Combine(_folder, "wire.json");
        var parsed = GameServerCommandLine.Parse(["--export-wire-contract", file]);
        var builder = WebApplication.CreateBuilder(parsed.HostArgs);

        builder.Configuration[GameServerCommandLine.ExportPathKey].ShouldBe(file);
    }

    /// <summary>The modules of <c>samples/SampleHost/Program.cs</c> in its Development flavor.</summary>
    private static void SampleHostModules(GameServerBuilder builder) =>
        builder
            .AddModule<IdentityModule>()
            .AddModule<EconomyModule>()
            .AddModule<IapModule>()
            .AddModule<SampleGameModule>()
            .AddModule<CounterModule>()
            .AddModule<GmModule>()
            .AddModule<CheatModule>();

    private static string ExpectedExport()
    {
        using var app = SampleTestHost.BuildApp(SampleHostModules);
        return app.Services.GetRequiredService<WireCatalog>().ToExportJson();
    }

    private sealed class StartProbe : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
