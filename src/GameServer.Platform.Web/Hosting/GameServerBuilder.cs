using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
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
        Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
        return this;
    }
}

internal sealed class TestAuthenticationMarker;

public static class GameServerBuilderExtensions
{
    /// <summary>Registers the framework services and binds <see cref="GameServerOptions"/> from <c>GameServer</c>.</summary>
    public static GameServerBuilder AddGameServer(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(GameServerOptions.SectionName).Get<GameServerOptions>() ?? new GameServerOptions();
        if (builder.Configuration[$"{GameServerOptions.SectionName}:DeploymentEnvironment"] is null)
        {
            options.DeploymentEnvironment = builder.Environment.EnvironmentName;
        }

        var registry = new ModuleRegistry();
        var services = builder.Services;

        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.TryAddSingleton<IClock, GameServer.Time.SystemClock>();
        services.AddSingleton<PlatformMetrics>();
        services.AddSingleton<AggregateDocumentSerializer>();
        services.AddSingleton<PerPlayerCommandLimiter>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<IOutboxRelay, OutboxRelay>();
        services.AddSingleton<OutboxSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxSweeper>());
        services.AddSingleton<PlayerSnapshotReader>();
        services.AddSingleton<IGameConfig>(sp =>
        {
            var environment = sp.GetRequiredService<IHostEnvironment>();
            var root = Path.IsPathRooted(options.ConfigPath) ? options.ConfigPath : Path.Combine(environment.ContentRootPath, options.ConfigPath);
            return FileGameConfigLoader.Load(root, options.DeploymentEnvironment);
        });
        services.AddSingleton<AggregateWireMapperRegistry>();
        services.AddSingleton<ErrorResponseWriter>();
        services.AddSingleton<ActorContextAccessor>();
        services.TryAddSingleton<IGateEvaluator, AllowAllGateEvaluator>();
        services.AddAuthentication();

        AddLimits(builder, options);
        return new GameServerBuilder(builder, options, registry);
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
