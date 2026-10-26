using GameServer.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

public static class GameServerApplicationExtensions
{
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
            if (module.Module is IHostEndpointsModule hostEndpoints)
            {
                hostEndpoints.MapHostEndpoints(app);
            }

            if (options.Hosting.MapPlayerApi && module.Module is IPlayerApiModule player)
            {
                player.MapPlayerApi(app);
            }

            if (options.Hosting.MapGmApi && module.Module is IGmApiModule gm)
            {
                gm.MapGmApi(app);
            }
        }

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints);
        errors.AddRange(EndpointSecurityValidator.Validate(
            endpoints,
            options,
            registry.Modules.SelectMany(m => m.Descriptor.PublicEndpoints),
            registry.Modules.SelectMany(m => m.Descriptor.Permissions)));
        if (errors.Count > 0)
        {
            throw new GameServerStartupException(errors);
        }

        return app;
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
                errors.Add(
                    $"[deployment] the in-memory store is configured in '{options.DeploymentEnvironment}'. " +
                    "Fix: set GameServer:Store=Couchbase; the in-memory store is for Development/Test only (review M6).");
            }

            (store as ICollectionAwareStore)?.UseCollections(registry.RequiredCollections());
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
