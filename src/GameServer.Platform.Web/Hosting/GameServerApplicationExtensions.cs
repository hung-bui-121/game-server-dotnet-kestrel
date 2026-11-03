using GameServer.Config;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Config;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Requests;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Hosting;

public static class GameServerApplicationExtensions
{
    /// <summary>Logger category of the startup warnings (R4) and the startup version line (FR-054).</summary>
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

        // First block (R10): config, registry, module rules and declared catalogs, before anything resolves a catalog.
        var configWarnings = ConfigStartup.Run(services, options, registry, errors);
        var catalog = services.GetRequiredService<WireCatalog>();
        errors.AddRange(catalog.Validate());
        ValidateServices(services, options, registry, errors);

        DeploymentChecks.ValidateSecretSources(app.Configuration, options, errors);
        DeploymentChecks.ValidateShutdown(options, errors);
        errors.AddRange(services.GetRequiredService<DataProtectionSettings>().Validate(options));
        if (options.Gate.CacheSeconds is < 0 or > 10)
        {
            errors.Add("[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).");
        }

        errors.AddRange(options.RequestLog.Validate());
        RunStartupChecks(services, errors, [.. configWarnings, .. HostWarnings(catalog, options)]);
        ValidateModuleOptions(services, errors);
        var forwarded = DeploymentChecks.BuildForwardedHeaders(options, errors);

        // Endpoints are mapped even when errors exist so that every violation is reported at once.
        // Outermost: the request event measures the whole pipeline and sees exceptions escaping it (research R11).
        app.UseMiddleware<RequestLogMiddleware>();
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

        if (options.AllowsDevelopmentTools && options.Hosting.MapGmApi)
        {
            GameServer.Platform.Web.OpenApi.GmOpenApi.Map(app);
        }

        if (errors.Count > 0)
        {
            // One report (FR-009): the endpoints mapped so far join the other faults. Enumerating DataSources builds every
            // endpoint (research V16), so a passing configuration leaves the endpoint check to the pre-serving run alone.
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
            errors.AddRange(EndpointValidation.Run(endpoints, services, includeUnmapped: false, reported: errors));
            throw new GameServerStartupException(errors);
        }

        // Endpoints are validated, all of them (also those mapped after this call), before the server listens (FR-012, T035).
        services.GetRequiredService<EndpointValidationState>().Validated(app);
        return app;
    }

    /// <summary>
    /// FR-054: one line naming what starts, then the config line (FR-044, data-model §5.6), logged by the pre-serving endpoint check
    /// once the whole validation succeeded (provision and export modes never get there).
    /// </summary>
    internal static void LogStartup(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<GameServerRuntimeInfo>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(StartupLogCategory);
        logger.LogInformation(
            "Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}), instance {InstanceId}.",
            runtime.GameId,
            runtime.GameVersion,
            runtime.FrameworkVersion,
            runtime.EnvironmentId,
            runtime.DeploymentEnvironment,
            runtime.InstanceId);
        LogConfig(logger, services.GetRequiredService<ConfigPhaseSummary>().Current);
    }

    /// <summary>
    /// The bundle size is the largest <c>GetConfig</c> response, the value checked against the limit (FR-053); the compression part
    /// names the encodings the startup probe kept and the permits of this host's gate (T071 round 2 m-2).
    /// </summary>
    private static void LogConfig(ILogger logger, ConfigPhaseSummary.Values summary)
    {
        var config = summary.Config;
        if (summary is { Bundle: { } bundle, Compressor: { } compressor })
        {
            logger.LogInformation(
                "Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount} client-visible, " +
                "bundle {BundleBytes} B, compression {Compression}), {RuleCount} rules, {CatalogCount} catalogs.",
                config.Label,
                config.ConfigVersion,
                config.ClientConfigVersion,
                config.FileNames.Count,
                config.ClientVisibleFiles.Count,
                bundle.MaxResponseBytes,
                compressor.Describe(),
                summary.RuleCount,
                summary.CatalogCount);
            return;
        }

        logger.LogInformation(
            "Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount} client-visible, " +
            "bundle not served), {RuleCount} rules, {CatalogCount} catalogs.",
            config.Label,
            config.ConfigVersion,
            config.ClientConfigVersion,
            config.FileNames.Count,
            config.ClientVisibleFiles.Count,
            summary.RuleCount,
            summary.CatalogCount);
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

    /// <summary>
    /// FR-086: the request event is off by default outside Development/Test; turning it on there is allowed and announced once
    /// with the volume estimate (data-model §1).
    /// </summary>
    internal static string? RequestLogVolumeWarning(GameServerOptions options) =>
        options.RequestLog.Enabled == true && !options.AllowsDevelopmentTools
            ? $"[request-log] GameServer:RequestLog:Enabled=true in deployment environment '{options.DeploymentEnvironment}': one event per " +
              "request (~300-400 B each, about 13-17 GB per instance per day at 500 requests/s). Raise " +
              "Observability:Logging:FileSizeLimitBytes/RetainedFileCountLimit or ship the log files before relying on it; " +
              "Observability:Logging:LevelOverrides:GameServer.Requests=Warning keeps only failures (FR-086)."
            : null;

    /// <summary>Warnings of the host itself: untyped wire mappers (FG-028, one per section) and the request-log volume (FR-086).</summary>
    private static IEnumerable<string> HostWarnings(WireCatalog catalog, GameServerOptions options) =>
        RequestLogVolumeWarning(options) is { } volume ? [.. catalog.ValidationWarnings, volume] : catalog.ValidationWarnings;

    /// <summary>Checks contributed by the host and the store packages (R4): errors join the report, warnings are logged once.</summary>
    private static void RunStartupChecks(IServiceProvider services, List<string> errors, IEnumerable<string> hostWarnings)
    {
        var result = CheckAll(services, StartupCheckMode.Serve);
        result.Warnings.AddRange(hostWarnings);

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
    /// Creates and runs every <see cref="IStartupCheck"/> (serve and provision mode, FG-023, research R9): each one is created from
    /// its registration inside its own isolation, then run inside another. A check must not throw; one that does still yields one
    /// report (FR-009). A check that needs the config or a catalog that is unavailable gets one "did not run" line (a warning in
    /// provision mode, BC-9) instead of repeating the cause, which the report already has.
    /// </summary>
    internal static StartupCheckResult CheckAll(IServiceProvider services, StartupCheckMode mode)
    {
        var result = new StartupCheckResult();
        var descriptors = services.GetRequiredService<StartupCheckRegistrations>().Descriptors();
        for (var index = 0; index < descriptors.Count; index++)
        {
            IStartupCheck check;
            try
            {
                check = StartupCheckRegistrations.Create(descriptors[index], services);
            }
            catch (Exception ex)
            {
                var name = StartupCheckRegistrations.NameOf(descriptors[index], index);
                if (ReportUnavailable(ex, name, mode, result))
                {
                    continue;
                }

                // A constructor that reports through GameServerStartupException gives its own lines, as Check does (m4).
                if (ex is GameServerStartupException startup)
                {
                    result.Errors.AddRange(startup.Violations);
                }
                else
                {
                    result.Errors.Add(ConfigReportLines.CheckNotCreated(name, ex));
                }

                continue;
            }

            try
            {
                check.Check(result);
            }
            catch (Exception ex)
            {
                // A marked exception gives "did not run": the config or catalog it needs is unavailable for a reason already reported.
                if (ReportUnavailable(ex, check.GetType().Name, mode, result))
                {
                    continue;
                }

                if (ex is GameServerStartupException startup)
                {
                    result.Errors.AddRange(startup.Violations);
                }
                else
                {
                    result.Errors.Add(ConfigReportLines.CheckFailed(check.GetType().Name, ex));
                }
            }
        }

        return result;
    }

    /// <summary>Adds the "did not run" line of a marked exception (data-model §3.4); false when it carries no marker.</summary>
    private static bool ReportUnavailable(Exception exception, string check, StartupCheckMode mode, StartupCheckResult result)
    {
        var marker = Unavailable.Find(exception);
        if (marker is null)
        {
            return false;
        }

        if (mode == StartupCheckMode.Provision || marker == Unavailable.Provision)
        {
            // Provision mode does not load config (D4): a check that needs it is skipped with a warning, never an error.
            result.Warnings.Add(ConfigReportLines.CheckNotRunProvision(check));
        }
        else if (Unavailable.CatalogTypeName(marker) is { } catalog)
        {
            result.Errors.Add(ConfigReportLines.CheckNotRunCatalog(check, catalog));
        }
        else
        {
            result.Errors.Add(ConfigReportLines.CheckNotRunConfig(check));
        }

        return true;
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
        catch (GameServerStartupException ex) when (Unavailable.Find(ex) is null)
        {
            errors.AddRange(ex.Violations);
        }
        catch (GameServerStartupException)
        {
            // A consumer taking the config or a declared catalog that failed: the config phase already reported the cause.
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
                    "Fix: services.AddAggregateWireMapper<TState, TWire>(id, map) in the module adapter (API-10).");
            }
        }
    }
}
