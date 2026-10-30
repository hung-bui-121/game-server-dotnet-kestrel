using System.Collections.Concurrent;
using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Modules.Identity;
using GameServer.Platform.Outbox;
using GameServer.Platform.Web.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sample.Counter;
using Sample.Counter.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US3: the host refuses to start on invalid configuration (ACC-12, ACC-26, ACC-32).</summary>
public sealed class StartupValidationTests
{
    private static async Task<GameServerStartupException> StartupFails(
        Action<WebApplication>? endpoints = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<GameServer.Platform.Web.Hosting.GameServerBuilder>? modules = null,
        bool testAuthentication = true)
    {
        return await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(modules, settings, endpoints, testAuthentication);
        });
    }

    [Fact]
    public async Task Endpoint_without_profile_fails()
    {
        var ex = await StartupFails(app => app.MapGet("/api/v1/naked", () => "x"));

        ex.Message.ShouldContain("GET /api/v1/naked] has 0 security profiles");
    }

    [Fact]
    public async Task Endpoint_with_two_profiles_fails()
    {
        var ex = await StartupFails(app => app.MapGet("/api/v1/both", () => "x").AsPlayerEndpoint().AsDevEndpoint());

        ex.Message.ShouldContain("has 2 security profiles");
    }

    [Fact]
    public async Task Unversioned_route_fails()
    {
        var ex = await StartupFails(app => app.MapGet("/api/counter", () => "x").AsPlayerEndpoint());

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
        var ex = await StartupFails(app => app.MapGet("/v1/open", () => "x").AsPublicEndpoint(protobuf: false));

        ex.Message.ShouldContain("not in GameServer:PublicEndpointAllowlist");
    }

    [Fact]
    public async Task Dev_endpoint_and_test_auth_in_production_fail()
    {
        var ex = await StartupFails(
            app => app.MapPost("/api/v1/cheat/gold", () => "x").AsDevEndpoint(),
            s => s["GameServer:DeploymentEnvironment"] = "Production");

        ex.Message.ShouldContain("uses profile Dev in Production");
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
    public async Task Valid_host_starts()
    {
        await using var host = await TestHost.StartAsync();
        host.Services.GetRequiredService<Registry.ModuleRegistry>().Modules.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Startup_check_errors_join_the_report()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IStartupCheck>(new FixedCheck(error: "[test] contributed error")));
        });

        ex.Violations.ShouldContain("[test] contributed error");
    }

    [Fact]
    public async Task Startup_check_warnings_are_logged_once_under_the_startup_category()
    {
        var entries = new ConcurrentQueue<(string Category, LogLevel Level, string Message)>();
        await using var host = await TestHost.StartAsync(services: s =>
        {
            s.AddSingleton<IStartupCheck>(new FixedCheck(warning: "[test] contributed warning"));
            s.AddSingleton<ILoggerProvider>(new CategoryLoggerProvider(entries));
        });

        entries.Count(e => e.Message == "[test] contributed warning").ShouldBe(1);
        entries.Single(e => e.Message == "[test] contributed warning").ShouldBe(("GameServer.Startup", LogLevel.Warning, "[test] contributed warning"));
    }

    [Fact]
    public async Task Module_options_validated_on_start_are_reported_by_UseGameServer()
    {
        // A GameServerStartupException (not an OptionsValidationException from StartAsync) proves UseGameServer reported it.
        var ex = await StartupFails(modules: b =>
        {
            TestHost.DefaultModules(b);
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
            TestHost.DefaultModules(b);
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
            TestHost.DefaultModules(b);
            b.Services.AddOptions<ProbeOptions>().Validate(_ => false, "[test] consumer options are invalid").ValidateOnStart();
            b.Services.AddSingleton<IOutboxConsumer>(sp => new OptionsConsumer(sp.GetRequiredService<IOptions<ProbeOptions>>().Value));
        });

        ex.Violations.Count(v => v == "[test] consumer options are invalid").ShouldBe(1);
    }

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
