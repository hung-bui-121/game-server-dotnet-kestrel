using GameServer.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using GameServer.Platform.Web.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Hosting;

public static class GameServerApplicationExtensions
{
    /// <summary>Logger category of the startup warnings (R4).</summary>
    public const string StartupLogCategory = "GameServer.Startup";

    /// <summary>
    /// Builds the pipeline in the order of research R11, maps framework and module endpoints, then runs the
    /// startup validation (ARC-06, ARC-07, SEC-08). Throws <see cref="GameServerStartupException"/> before
    /// any traffic when the configuration is invalid.
    /// </summary>
    public static WebApplication UseGameServer(this WebApplication app)
    {
        var services = app.Services;
        var options = services.GetRequiredService<GameServerOptions>();
        var registry = services.GetRequiredService<ModuleRegistry>();
        var errors = new List<string>();

        IGameConfig? config = null;
        try
        {
            config = services.GetRequiredService<IGameConfig>();
            services.GetRequiredService<PlatformMetrics>().SetConfigVersion(config.ConfigVersion);
        }
        catch (GameServerStartupException ex)
        {
            errors.AddRange(ex.Violations);
        }

        errors.AddRange(registry.Validate(options, config));
        ValidateServices(services, options, registry, errors);

        DeploymentChecks.ValidateSecretSources(app.Configuration, options, errors);
        DeploymentChecks.ValidateShutdown(options, errors);
        errors.AddRange(services.GetRequiredService<DataProtectionSettings>().Validate(options));
        if (options.Gate.CacheSeconds is < 0 or > 10)
        {
            errors.Add("[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).");
        }

        RunStartupChecks(services, errors);
        ValidateModuleOptions(services, errors);
        var forwarded = DeploymentChecks.BuildForwardedHeaders(options, errors);

        // Endpoints are mapped even when errors exist so that every violation is reported at once.
        if (forwarded is not null)
        {
            // First: the client IP used by rate limits and audit comes from trusted proxies only (OPS-02).
            app.UseForwardedHeaders(forwarded);
        }

        app.UseMiddleware<ExceptionMiddleware>();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseRouting();

        // GM CORS (SEC-06) before authentication: preflights are answered here, never authenticated. Without a
        // registered policy (default: same-origin) the middleware does nothing.
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/gm"), branch => branch.UseCors(GmCorsPolicy.Name));
        app.UseMiddleware<GateMiddleware>();
        app.UseMiddleware<ProfileEnforcementMiddleware>();
        app.UseRequestTimeouts();
        app.UseRateLimiter();

        CoreEndpoints.MapPublic(app);
        if (options.Hosting.MapPlayerApi)
        {
            CoreEndpoints.MapPlayer(app);
        }

        foreach (var module in registry.Modules.Where(m => !m.ServiceOnly))
        {
            try
            {
                MapModule(app, options, module.Module);
            }
            catch (OptionsValidationException ex)
            {
                // A module reading its own options while mapping (GM SSO routes) hits the failure ValidateModuleOptions
                // already reported: keep one line per failure and go on, so the report stays complete (FR-009).
                AddNew(errors, ex.Failures);
            }
        }

        var platformPublic = new List<string>();
        if (options.AllowsDevelopmentTools && options.Hosting.MapGmApi)
        {
            GameServer.Platform.Web.OpenApi.GmOpenApi.Map(app);
            platformPublic.Add(GameServer.Platform.Web.OpenApi.GmOpenApi.Route);
        }

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        errors.AddRange(EndpointSecurityValidator.Validate(
            endpoints,
            options,
            registry.Modules.SelectMany(m => m.Descriptor.PublicEndpoints).Concat(platformPublic),
            registry.Modules.SelectMany(m => m.Descriptor.Permissions)));
        errors.AddRange(CommandRouteValidator.Validate(endpoints, registry.Commands));
        if (errors.Count > 0)
        {
            throw new GameServerStartupException(errors);
        }

        return app;
    }

    private static void MapModule(WebApplication app, GameServerOptions options, GameServer.Modules.IGameModule module)
    {
        if (module is IHostEndpointsModule hostEndpoints)
        {
            hostEndpoints.MapHostEndpoints(app);
        }

        if (options.Hosting.MapPlayerApi && module is IPlayerApiModule player)
        {
            player.MapPlayerApi(app);
        }

        if (options.Hosting.MapGmApi && module is IGmApiModule gm)
        {
            gm.MapGmApi(app);
        }
    }

    /// <summary>Checks contributed by the host and the store packages (R4): errors join the report, warnings are logged once.</summary>
    private static void RunStartupChecks(IServiceProvider services, List<string> errors)
    {
        var result = CheckAll(services);
        errors.AddRange(result.Errors);
        if (result.Warnings.Count > 0)
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(StartupLogCategory);
            foreach (var warning in result.Warnings)
            {
                logger.LogWarning("{StartupWarning}", warning);
            }
        }
    }

    /// <summary>
    /// Runs every <see cref="IStartupCheck"/> (serve and provision mode). A check must not throw; one that does still yields one
    /// report (FR-009): its <see cref="GameServerStartupException"/> violations, or one error naming the check type and the cause.
    /// </summary>
    internal static StartupCheckResult CheckAll(IServiceProvider services)
    {
        var result = new StartupCheckResult();
        foreach (var check in services.GetServices<IStartupCheck>())
        {
            try
            {
                check.Check(result);
            }
            catch (GameServerStartupException ex)
            {
                result.Errors.AddRange(ex.Violations);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"[{check.GetType().Name}] the startup check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Module options validated on start (GM SSO, identity pepper, providers) join the same report instead of failing
    /// later inside <c>StartAsync</c> (FR-009). The host validates them again at start; that run is never reached on errors.
    /// </summary>
    private static void ValidateModuleOptions(IServiceProvider services, List<string> errors)
    {
        try
        {
            services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException ex)
        {
            AddNew(errors, ex.Failures);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OptionsValidationException))
        {
            AddNew(errors, ex.InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures));
        }
    }

    /// <summary>One line per failure: options read while services are built may already have reported the same failure.</summary>
    private static void AddNew(List<string> errors, IEnumerable<string> failures)
    {
        foreach (var failure in failures)
        {
            if (!errors.Contains(failure))
            {
                errors.Add(failure);
            }
        }
    }

    private static void ValidateServices(IServiceProvider services, GameServerOptions options, ModuleRegistry registry, List<string> errors)
    {
        var store = services.GetService<ITransactionalDocumentStore>();
        if (store is null || services.GetService<IKeyValueStore>() is null)
        {
            errors.Add("[deployment] no document store is registered. Fix: AddCouchbaseDocumentStore() (or AddInMemoryDocumentStore() for Development/tests).");
        }
        else
        {
            if (store is IDevelopmentOnlyStore && !options.AllowsDevelopmentTools)
            {
                var kind = options.Store == StoreKind.File ? StoreKind.File : StoreKind.InMemory;
                errors.Add(
                    $"[deployment] the {kind} store is configured in '{options.DeploymentEnvironment}'. " +
                    "Fix: set GameServer:Store=Couchbase and the Couchbase settings; the InMemory and File stores are for Development/Test only (FR-007).");
            }

            (store as ICollectionAwareStore)?.UseCollections(registry.RequiredCollections());
            var transactionOnly = registry.TransactionOnlyCollections();
            (store as ITransactionOnlyAwareStore)?.UseTransactionOnlyCollections(transactionOnly);
            (services.GetService<IKeyValueStore>() as ITransactionOnlyAwareStore)?.UseTransactionOnlyCollections(transactionOnly);
        }

        if (!options.AllowsDevelopmentTools && services.GetService<TestAuthenticationMarker>() is not null)
        {
            errors.Add(
                $"[deployment] test authentication is enabled in '{options.DeploymentEnvironment}'. " +
                "Fix: remove UseTestAuthentication(); it is allowed only in Development/Test (SEC-16).");
        }

        try
        {
            services.GetRequiredService<IOutboxRelay>();
        }
        catch (GameServerStartupException ex)
        {
            errors.AddRange(ex.Violations);
        }
        catch (OptionsValidationException ex)
        {
            // A game consumer reading its own validated options while the relay is built. Options validated on start are
            // reported again by ValidateModuleOptions (kept once); options with Validate() but no ValidateOnStart are
            // reported only here, so the start fails instead of every command returning INTERNAL (FR-009). The framework's
            // analytics consumer reads no options here, so the duplicate-kind check (FR-040) still runs when only the
            // analytics settings are invalid.
            AddNew(errors, ex.Failures);
        }

        if (options.Hosting.MapPlayerApi)
        {
            var mappers = services.GetRequiredService<AggregateWireMapperRegistry>();
            foreach (var aggregate in registry.Aggregates.Values.Where(a => a.ClientVisible && !mappers.Has(a.AggregateId)))
            {
                errors.Add(
                    $"[aggregate {aggregate.AggregateId}] is client-visible but has no wire mapper. " +
                    "Fix: services.AddAggregateWireMapper<TState>(id, map) in the module adapter (API-10).");
            }
        }
    }
}
