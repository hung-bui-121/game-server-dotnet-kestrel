using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Analytics;
using GameServer.Platform.Commands;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Jobs;
using GameServer.Platform.Outbox;
using GameServer.Platform.Privacy;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using GameServer.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Hosting;

/// <summary>Per-module registration switches.</summary>
public sealed class ModuleRegistrationOptions
{
    public bool IsServiceOnly { get; private set; }

    /// <summary>Registers services only; no Player/GM endpoints are mapped (ARC-04).</summary>
    public ModuleRegistrationOptions ServiceOnly()
    {
        IsServiceOnly = true;
        return this;
    }
}

/// <summary>Composition root API of the framework (ARC-03): one call per module.</summary>
public sealed class GameServerBuilder
{
    internal GameServerBuilder(WebApplicationBuilder host, GameServerOptions options, ModuleRegistry registry)
    {
        Host = host;
        Options = options;
        Registry = registry;
    }

    public WebApplicationBuilder Host { get; }

    public IServiceCollection Services => Host.Services;

    public GameServerOptions Options { get; }

    public ModuleRegistry Registry { get; }

    public GameServerBuilder AddModule<TModule>(Action<ModuleRegistrationOptions>? configure = null)
        where TModule : IGameModule, new() =>
        AddModule(new TModule(), configure);

    public GameServerBuilder AddModule(IGameModule module, Action<ModuleRegistrationOptions>? configure = null)
    {
        var registration = new ModuleRegistrationOptions();
        configure?.Invoke(registration);
        Registry.Add(module, registration.IsServiceOnly);
        module.ConfigureServices(Services, new ModuleContext(Options.GameId, Options.EnvironmentId, Options.DeploymentEnvironment));

        // Lease jobs and deletion steps are resolved per run in their own scope (JOB-01, FR-020); a module may register them itself first.
        foreach (var job in Registry.Modules[^1].Descriptor.Jobs)
        {
            Services.TryAddTransient(job.JobType);
        }

        foreach (var step in Registry.Modules[^1].Descriptor.DeletionSteps)
        {
            Services.TryAddTransient(step.StepType);
        }

        return this;
    }

    /// <summary>Game overrides, applied in call order after the modules added so far (EXT-02).</summary>
    public GameServerBuilder ConfigureExtensions(Action<ExtensionRegistry> configure)
    {
        configure(new ExtensionRegistry(Services, Registry));
        return this;
    }

    /// <summary>Header-based test authentication (Development/test only; rejected in Production).</summary>
    public GameServerBuilder UseTestAuthentication()
    {
        Services.AddSingleton<TestAuthenticationMarker>();
        Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
        Services.Configure<ActorSchemeOptions>(o =>
        {
            o.PlayerSchemes.Add(TestAuthenticationHandler.SchemeName);
            o.GmSchemes.Add(TestAuthenticationHandler.SchemeName);
        });
        return this;
    }
}

internal sealed class TestAuthenticationMarker;

public static class GameServerBuilderExtensions
{
    /// <summary>Registers the framework services and binds <see cref="GameServerOptions"/> from <c>GameServer</c>.</summary>
    public static GameServerBuilder AddGameServer(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSettings<GameServerOptions>(GameServerOptions.SectionName);

        // Resolved once and validated by DeploymentIdentityCheck (FR-003): an invalid value never reads as Development/Test,
        // so registration decisions taken before validation (test auth, cheats, OpenAPI) fail closed.
        var deployment = DeploymentEnvironmentResolution.Resolve(builder.Configuration, builder.Environment);
        options.DeploymentEnvironment = deployment.Effective;

        var registry = new ModuleRegistry();
        var services = builder.Services;

        services.AddSingleton(options);
        services.AddSingleton(deployment);
        services.AddSingleton<IStartupCheck, DeploymentIdentityCheck>();
        services.AddSingleton(registry);
        services.TryAddSingleton<IClock, GameServer.Time.SystemClock>();
        services.AddSingleton<PlatformMetrics>();
        services.AddSingleton<AggregateDocumentSerializer>();
        services.AddSingleton<PerPlayerCommandLimiter>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<IOutboxRelay, OutboxRelay>();
        AddAnalytics(services);
        services.AddSingleton<OutboxSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxSweeper>());
        services.AddSingleton<LeaseStore>();
        services.AddSingleton<LeaseJobRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<LeaseJobRunner>());

        // PRIV-01 account purge (research R6/R7): the only purge writer and the step pipeline; module steps get erasers from it.
        services.AddSingleton<PurgeTargets>();
        services.AddSingleton<IAccountPurgeWriter, AccountPurgeWriter>();
        services.AddSingleton<AccountPurgeService>();

        // Read side of the purge records: GM view, GM target validator, purge job (core service, review minor 12a).
        services.TryAddSingleton<AccountDeletionRecords>();
        services.AddSingleton<PlayerSnapshotReader>();
        services.AddSingleton<IPlayerAggregateWriter, PlayerAggregateWriter>();
        services.AddSingleton<GameServer.Platform.Persistence.IAuditedStore, GameServer.Platform.Persistence.AuditedStore>();
        services.AddSingleton<ReceiptReader>();
        services.AddSingleton<GameServer.Platform.Persistence.ClaimStore>();
        services.TryAddSingleton<GameServer.Platform.Persistence.IStoreHealthProbe, GameServer.Platform.Persistence.DocumentStoreHealthProbe>();
        services.AddSingleton<IGameConfig>(sp =>
        {
            var environment = sp.GetRequiredService<IHostEnvironment>();
            var root = Path.IsPathRooted(options.ConfigPath) ? options.ConfigPath : Path.Combine(environment.ContentRootPath, options.ConfigPath);
            return FileGameConfigLoader.Load(root, options.DeploymentEnvironment);
        });
        services.AddSingleton<AggregateWireMapperRegistry>();
        services.AddSingleton<ErrorResponseWriter>();
        services.AddSingleton<ActorContextAccessor>();
        services.TryAddSingleton<IGateEvaluator, StoreGateEvaluator>();
        services.AddAuthentication();
        services.AddOptions<ActorSchemeOptions>();

        // OPS-04: one key ring per deployment, shared by every instance (GM cookies, antiforgery, OIDC correlation).
        SharedDataProtection.Add(services, builder.Configuration, options);
        services.AddAntiforgery(antiforgery => antiforgery.HeaderName = CsrfOptions.HeaderName);
        services.AddOptions<CsrfOptions>();
        services.AddCors();
        if (options.AllowsDevelopmentTools)
        {
            // GM API contract for frontend developers (deliverable #8); not served outside Development/Test.
            OpenApi.GmOpenApi.Add(services);
        }

        // OPS-03: drain in-flight requests (commits complete, PERF-02) before the host stops.
        services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(options.Hosting.ShutdownTimeoutSeconds));
        services.AddSingleton<ShutdownSignal>();
        services.AddHostedService<DrainDelayService>();

        AddLimits(builder, options);
        return new GameServerBuilder(builder, options, registry);
    }

    /// <summary>
    /// Analytics core (R13): options validated on start (serve only, FR-030), pipeline registered before the outbox sweeper
    /// so it stops after it, and the consumer of the reserved outbox kind <c>analytics</c> (FR-040). Sinks are registered by
    /// the host (<c>AddAnalyticsFiles()</c>, tests); without one analytics is inactive.
    /// </summary>
    private static void AddAnalytics(IServiceCollection services)
    {
        services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AnalyticsOptions>, AnalyticsOptionsValidator>();

        // Counted from the final registrations when first resolved (after Build), so sinks added later still count and
        // deciding "active" never constructs a sink.
        services.AddSingleton(_ => new AnalyticsActivation(services.Count(d => d.ServiceType == typeof(IAnalyticsSink))));
        services.AddSingleton(sp => new AnalyticsTimeZone(sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value.TimeZone));
        services.AddSingleton<AnalyticsMetrics>();
        services.AddSingleton<AnalyticsRecordFormatter>();
        services.AddSingleton<AnalyticsPipeline>();
        services.AddSingleton<IAnalyticsEmitter>(sp => sp.GetRequiredService<AnalyticsPipeline>());
        services.AddHostedService(sp => sp.GetRequiredService<AnalyticsPipeline>());
        services.AddSingleton<IOutboxConsumer>(sp => new AnalyticsOutboxConsumer(
            new Lazy<AnalyticsPipeline>(sp.GetRequiredService<AnalyticsPipeline>),
            sp.GetRequiredService<AnalyticsMetrics>(),
            sp.GetRequiredService<ILogger<AnalyticsOutboxConsumer>>()));
    }

    private static void AddLimits(WebApplicationBuilder builder, GameServerOptions options)
    {
        var limits = options.Limits;
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = Math.Max(limits.GmBodyBytes, limits.PlayerBodyBytes));

        builder.Services.AddRequestTimeouts(timeouts =>
        {
            timeouts.AddPolicy(ApiRouteExtensions.PlayerTimeoutPolicy, TimeoutPolicy(limits.PlayerTimeoutSeconds));
            timeouts.AddPolicy(ApiRouteExtensions.GmTimeoutPolicy, TimeoutPolicy(limits.GmTimeoutSeconds));
        });

        builder.Services.AddRateLimiter(rate =>
        {
            rate.AddConcurrencyLimiter(ApiRouteExtensions.CommandLimiterPolicy, o =>
            {
                o.PermitLimit = limits.CommandConcurrency;
                o.QueueLimit = limits.CommandConcurrency * limits.QueueFactor;
                o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
            });
            rate.AddConcurrencyLimiter(ApiRouteExtensions.GmLimiterPolicy, o =>
            {
                o.PermitLimit = limits.GmConcurrency;
                o.QueueLimit = limits.GmConcurrency * limits.QueueFactor;
                o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
            });
            rate.OnRejected = (context, _) =>
                new ValueTask(context.HttpContext.RequestServices.GetRequiredService<ErrorResponseWriter>()
                    .WriteAsync(context.HttpContext, CoreErrorCodes.RateLimited, 1000));
        });
    }

    private static RequestTimeoutPolicy TimeoutPolicy(int seconds) => new()
    {
        Timeout = TimeSpan.FromSeconds(seconds),
        TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
        WriteTimeoutResponse = context =>
            context.RequestServices.GetRequiredService<ErrorResponseWriter>().WriteAsync(context, CoreErrorCodes.RequestTimeout, 1000),
    };
}
