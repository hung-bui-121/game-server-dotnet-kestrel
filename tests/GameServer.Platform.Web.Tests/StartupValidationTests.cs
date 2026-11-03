using System.Collections.Concurrent;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Modules.Identity;
using GameServer.Platform.Outbox;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sample.Counter;
using Sample.Counter.Adapters;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>US3: the host refuses to start on invalid configuration (ACC-12, ACC-26, ACC-32).</summary>
public sealed class StartupValidationTests
{
    private static async Task<GameServerStartupException> StartupFails(
        Action<WebApplication>? endpoints = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<GameServer.Platform.Web.Hosting.GameServerBuilder>? modules = null,
        bool testAuthentication = true,
        params RouteDeclaration[] routes)
    {
        // An endpoint a test maps itself is declared by the host (BC-1), consistently with what the test checks.
        var composition = routes.Length == 0 ? modules : gs =>
        {
            (modules ?? SampleTestHost.DefaultModules)(gs);
            gs.DeclareRoutes(routes);
        };
        return await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(composition, settings, endpoints, testAuthentication);
        });
    }

    [Fact]
    public async Task Endpoint_without_profile_fails()
    {
        var ex = await StartupFails(app => app.MapGet("/api/v1/naked", () => "x"), routes: RouteDeclaration.Get("test.naked", "/api/v1/naked", SecurityProfile.Player));

        ex.Message.ShouldContain("GET /api/v1/naked] has 0 security profiles");
    }

    [Fact]
    public async Task Endpoint_with_two_profiles_fails()
    {
        var ex = await StartupFails(
            app => app.MapGet("/api/v1/both", () => "x").AsPlayerEndpoint().AsDevEndpoint(),
            routes: RouteDeclaration.Get("test.both", "/api/v1/both", SecurityProfile.Player));

        ex.Message.ShouldContain("has 2 security profiles");
    }

    [Fact]
    public async Task Unversioned_route_fails()
    {
        var ex = await StartupFails(app => app.MapGet("/api/counter", () => "x").AsPlayerEndpoint(), routes: RouteDeclaration.Get("test.unversioned", "/api/counter", SecurityProfile.Player));

        ex.Message.ShouldContain("no version segment");
    }

    [Fact]
    public async Task Duplicate_route_fails()
    {
        var ex = await StartupFails(app => app.MapPost("/api/v1/counter/increment", () => "x").AsPlayerEndpoint());

        ex.Message.ShouldContain("POST /api/v1/counter/increment] is mapped more than once");
    }

    [Fact]
    public async Task Public_endpoint_outside_allowlist_fails()
    {
        var ex = await StartupFails(
            app => app.MapGet("/v1/open", () => "x").AsPublicEndpoint(protobuf: false),
            routes: RouteDeclaration.Get("test.open", "/v1/open", SecurityProfile.Public, RouteSurface.Host));

        ex.Message.ShouldContain("not in GameServer:PublicEndpointAllowlist");
    }

    [Fact]
    public async Task Dev_endpoint_and_test_auth_in_production_fail()
    {
        var ex = await StartupFails(
            app => app.MapPost("/api/v1/cheat/gold", () => "x").AsDevEndpoint(),
            s => s["GameServer:DeploymentEnvironment"] = "Production",
            routes: RouteDeclaration.Post("test.cheat_gold", "/api/v1/cheat/gold", SecurityProfile.Dev));

        ex.Message.ShouldContain("[endpoint POST /api/v1/cheat/gold] uses profile Dev in deployment environment 'Production' (environment id 'test').");
        ex.Message.ShouldContain("test authentication is enabled in 'Production'");
        ex.Message.ShouldContain("the InMemory store is configured in 'Production'");
    }

    [Fact]
    public async Task Staging_rejects_test_auth_and_in_memory_store()
    {
        var ex = await StartupFails(settings: s => s["GameServer:DeploymentEnvironment"] = "Staging");

        ex.Message.ShouldContain("test authentication is enabled in 'Staging'");
        ex.Message.ShouldContain("the InMemory store is configured in 'Staging'");
        ex.Message.ShouldContain("the InMemory and File stores are for Development/Test only");
    }

    [Fact]
    public async Task Missing_game_id_fails_with_fix()
    {
        var ex = await StartupFails(settings: s => s["GameServer:GameId"] = "");

        ex.Message.ShouldContain("GameServer:GameId");
        ex.Message.ShouldContain("Fix:");
    }

    [Fact]
    public async Task Missing_config_fails()
    {
        var ex = await StartupFails(settings: s => s["GameServer:ConfigPath"] = Path.Combine(AppContext.BaseDirectory, "no-such-config"));

        ex.Message.ShouldContain("manifest.json");
    }

    [Fact]
    public async Task Missing_module_dependency_fails()
    {
        var ex = await StartupFails(modules: b => b.AddModule<CounterModule>());

        ex.Message.ShouldContain("[module sample.counter] depends on module 'identity'");
        ex.Message.ShouldContain("'profile' is not registered");
    }

    [Fact]
    public async Task Client_visible_aggregate_without_wire_mapper_fails()
    {
        var ex = await StartupFails(modules: b => b.AddModule<IdentityModule>().AddModule(new NoMapperModule()));

        ex.Message.ShouldContain("[aggregate unmapped] is client-visible but has no wire mapper");
    }

    [Fact]
    public async Task Duplicate_error_codes_fail()
    {
        var ex = await StartupFails(modules: b => b.AddModule<IdentityModule>().AddModule<CounterModule>().AddModule(new DuplicateErrorsModule()));

        ex.Message.ShouldContain("COUNTER_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task Player_command_route_for_an_unregistered_operation_fails()
    {
        var ex = await StartupFails(app => app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            "/api/v1/counter/ghost",
            "counter.ghost",
            request => new IncrementCommand(request.By, null),
            result => new Wire.IncrementResult { Value = result.Value }),
            routes: new RouteDeclaration("counter.ghost", ["POST"], "/api/v1/counter/ghost", SecurityProfile.Player, RouteSurface.PlayerApi, "counter.ghost"));

        ex.Violations.ShouldContain(
            "[endpoint POST /api/v1/counter/ghost] runs command 'counter.ghost' which no registered module declares. " +
            "Fix: add the command to its module's ModuleDescriptor.Commands and AddModule that module, " +
            "or pass the registered operation name to MapPlayerCommand/MapDevCommand/MapGmCommand (ARC-07).");
    }

    [Fact]
    public async Task Gm_command_route_for_an_unregistered_operation_fails()
    {
        var ex = await StartupFails(app => app.MapGmCommand<GhostBody, IncrementCommand, IncrementResult>(
            "/gm/v1/players/{playerId}/ghost",
            "counter.gm_ghost",
            CounterModuleDefinition.ReadPermission,
            _ => new IncrementCommand(1, null)),
            routes: new RouteDeclaration(
                "counter.gm_ghost", ["POST"], "/gm/v1/players/{playerId}/ghost", SecurityProfile.Gm, RouteSurface.GmApi, "counter.gm_ghost", CounterModuleDefinition.ReadPermission));

        ex.Violations.ShouldHaveSingleItem().ShouldStartWith(
            "[endpoint POST /gm/v1/players/{playerId}/ghost] runs command 'counter.gm_ghost' which no registered module declares.");
    }

    [Fact]
    public async Task Command_route_with_another_profile_than_its_command_fails()
    {
        // A Dev route accepts any actor (OPS-09): mapping a Player command there would be its only guard.
        var ex = await StartupFails(app => app.MapDevCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            "/api/v1/counter/dev-increment",
            IncrementHandler.Operation,
            request => new IncrementCommand(request.By, null),
            result => new Wire.IncrementResult { Value = result.Value }),
            routes: new RouteDeclaration(
                "test.dev_increment", ["POST"], "/api/v1/counter/dev-increment", SecurityProfile.Dev, RouteSurface.PlayerApi, IncrementHandler.Operation));

        ex.Violations.ShouldHaveSingleItem().ShouldBe(
            "[endpoint POST /api/v1/counter/dev-increment] has profile Dev but runs command 'counter.increment' declared with profile Player. " +
            "Fix: map Player commands with MapPlayerCommand, Dev commands with MapDevCommand and GM commands with MapGmCommand, " +
            "or correct the profile in the CommandDescriptor (SEC-08).");
    }

    [Fact]
    public async Task Gm_command_route_with_another_permission_than_its_command_fails()
    {
        var ex = await StartupFails(app => app.MapGmCommand<GhostBody, BanPlayerCommand, BanResult>(
            "/gm/v1/players/{playerId}/ban-with-read",
            "identity.ban",
            CounterModuleDefinition.ReadPermission,
            body => new BanPlayerCommand(body.Reason!, null, [])),
            routes: new RouteDeclaration(
                "test.ban_with_read", ["POST"], "/gm/v1/players/{playerId}/ban-with-read", SecurityProfile.Gm, RouteSurface.GmApi, "identity.ban", CounterModuleDefinition.ReadPermission));

        ex.Violations.ShouldHaveSingleItem().ShouldBe(
            "[endpoint POST /gm/v1/players/{playerId}/ban-with-read] requires permission 'counter.read' but runs GM command 'identity.ban' " +
            "which requires 'player.ban'. Fix: pass the command's permission (CommandDescriptor.Permission) to MapGmCommand (SEC-07).");
    }

    [Fact]
    public async Task Command_routes_of_the_default_host_carry_their_registered_operations()
    {
        await using var host = await SampleTestHost.StartAsync();

        var operations = ((IEndpointRouteBuilder)host.App).DataSources
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<CommandOperationMetadata>()?.Operation)
            .OfType<string>()
            .ToArray();
        operations.ShouldContain(IncrementHandler.Operation);
        operations.ShouldContain("identity.ban");
        var registry = host.Services.GetRequiredService<Registry.ModuleRegistry>();
        operations.ShouldAllBe(operation => registry.Commands.ContainsKey(operation));
    }

    [Fact]
    public async Task Valid_host_starts()
    {
        await using var host = await SampleTestHost.StartAsync();
        host.Services.GetRequiredService<Registry.ModuleRegistry>().Modules.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Startup_check_errors_join_the_report()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(services: s => s.AddSingleton<IStartupCheck>(new FixedCheck(error: "[test] contributed error")));
        });

        ex.Violations.ShouldContain("[test] contributed error");
    }

    [Fact]
    public async Task Startup_check_warnings_are_logged_once_under_the_startup_category()
    {
        var entries = new ConcurrentQueue<(string Category, LogLevel Level, string Message)>();
        await using var host = await SampleTestHost.StartAsync(services: s =>
        {
            s.AddSingleton<IStartupCheck>(new FixedCheck(warning: "[test] contributed warning"));
            s.AddSingleton<ILoggerProvider>(new CategoryLoggerProvider(entries));
        });

        entries.Count(e => e.Message == "[test] contributed warning").ShouldBe(1);
        entries.Single(e => e.Message == "[test] contributed warning").ShouldBe(("GameServer.Startup", LogLevel.Warning, "[test] contributed warning"));
    }

    [Fact]
    public async Task An_invalid_request_log_exclusion_fails_startup_with_its_report_line()
    {
        var ex = await StartupFails(settings: s =>
        {
            s["GameServer:RequestLog:ExcludedPaths:0"] = "/v1/time";
            s["GameServer:RequestLog:ExcludedPaths:1"] = "v1/no-slash";
        });

        ex.Violations.ShouldContain("[request-log] GameServer:RequestLog:ExcludedPaths:1 'v1/no-slash' must start with '/'.");
        ex.Violations.ShouldNotContain(v => v.Contains("ExcludedPaths:0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Staging", "true", true)]
    [InlineData("Staging", null, false)]
    [InlineData("Development", "true", false)]
    public async Task Request_logging_enabled_outside_development_logs_one_volume_warning(string environment, string? enabled, bool expected)
    {
        var entries = new ConcurrentQueue<(string Category, LogLevel Level, string Message)>();
        var start = async () =>
        {
            await using var host = await SampleTestHost.StartAsync(
                settings: s =>
                {
                    s["GameServer:DeploymentEnvironment"] = environment;
                    s["GameServer:RequestLog:Enabled"] = enabled;
                },
                services: s => s.AddSingleton<ILoggerProvider>(new CategoryLoggerProvider(entries)));
        };
        if (environment == "Staging")
        {
            // A Staging test host fails on test authentication and the in-memory store; the warning is logged with that report.
            await Should.ThrowAsync<GameServerStartupException>(start);
        }
        else
        {
            await start();
        }

        var warnings = entries.Where(e => e.Message.StartsWith("[request-log]", StringComparison.Ordinal)).ToList();
        if (!expected)
        {
            warnings.ShouldBeEmpty();
            return;
        }

        var warning = warnings.ShouldHaveSingleItem();
        warning.Category.ShouldBe("GameServer.Startup");
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("GameServer:RequestLog:Enabled=true in deployment environment 'Staging'");
        warning.Message.ShouldContain("~300-400 B each, about 13-17 GB per instance per day at 500 requests/s");
    }

    [Fact]
    public async Task Module_options_validated_on_start_are_reported_by_UseGameServer()
    {
        // A GameServerStartupException (not an OptionsValidationException from StartAsync) proves UseGameServer reported it.
        var ex = await StartupFails(modules: b =>
        {
            SampleTestHost.DefaultModules(b);
            b.Services.AddOptions<ProbeOptions>().Validate(_ => false, "[test] probe options are invalid").ValidateOnStart();
        });

        ex.Violations.ShouldContain("[test] probe options are invalid");
    }

    [Fact]
    public async Task A_consumer_reading_options_validated_without_ValidateOnStart_fails_startup_with_their_message()
    {
        // Without ValidateOnStart only building the outbox relay reads these options: the start must fail with the message
        // instead of serving and answering every command INTERNAL.
        var ex = await StartupFails(modules: b =>
        {
            SampleTestHost.DefaultModules(b);
            b.Services.AddOptions<ProbeOptions>().Validate(_ => false, "[test] consumer options are invalid");
            b.Services.AddSingleton<IOutboxConsumer>(sp => new OptionsConsumer(sp.GetRequiredService<IOptions<ProbeOptions>>().Value));
        });

        ex.Violations.Count(v => v == "[test] consumer options are invalid").ShouldBe(1);
    }

    [Fact]
    public async Task Options_validated_on_start_and_read_by_a_consumer_are_reported_once()
    {
        var ex = await StartupFails(modules: b =>
        {
            SampleTestHost.DefaultModules(b);
            b.Services.AddOptions<ProbeOptions>().Validate(_ => false, "[test] consumer options are invalid").ValidateOnStart();
            b.Services.AddSingleton<IOutboxConsumer>(sp => new OptionsConsumer(sp.GetRequiredService<IOptions<ProbeOptions>>().Value));
        });

        ex.Violations.Count(v => v == "[test] consumer options are invalid").ShouldBe(1);
    }

    private sealed record GhostBody(string? Reason) : IGmCommandBody;

    private sealed class ProbeOptions;

    private sealed class OptionsConsumer(ProbeOptions options) : IOutboxConsumer
    {
        public ProbeOptions Options { get; } = options;

        public string Kind => "probe_options";

        public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedCheck(string? error = null, string? warning = null) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            if (error is not null)
            {
                result.Errors.Add(error);
            }

            if (warning is not null)
            {
                result.Warnings.Add(warning);
            }
        }
    }

    private sealed class CategoryLoggerProvider(ConcurrentQueue<(string Category, LogLevel Level, string Message)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class CategoryLogger(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue((category, logLevel, formatter(state, exception)));
    }

    private sealed class NoMapperModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "nomapper",
            Aggregates = [new PlayerAggregateDescriptor<CounterState>("unmapped", 1, true, () => new CounterState())],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed class DuplicateErrorsModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "dup-errors",
            ErrorCodes = new GameServer.Errors.ErrorCodeSet(30000, 30099, CounterErrors.LimitExceeded with { Number = 30001 }),
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
