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
        if (errors.Count > 0)
        {
            throw new GameServerStartupException(errors);
        }

        app.UseMiddleware<ExceptionMiddleware>();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseRouting();
        app.UseMiddleware<GateMiddleware>();
        app.UseAuthentication();
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
        errors.AddRange(EndpointSecurityValidator.Validate(endpoints, options));
        if (errors.Count > 0)
        {
            throw new GameServerStartupException(errors);
        }

        return app;
    }

    private static void ValidateServices(IServiceProvider services, GameServerOptions options, ModuleRegistry registry, List<string> errors)
    {
        if (services.GetService<ITransactionalDocumentStore>() is null)
        {
            errors.Add("[deployment] no document store is registered. Fix: AddCouchbaseDocumentStore() (or AddInMemoryDocumentStore() for Development/tests).");
        }

        if (options.IsProduction && services.GetService<TestAuthenticationMarker>() is not null)
        {
            errors.Add("[deployment] test authentication is enabled in Production. Fix: remove UseTestAuthentication() (SEC-16).");
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
