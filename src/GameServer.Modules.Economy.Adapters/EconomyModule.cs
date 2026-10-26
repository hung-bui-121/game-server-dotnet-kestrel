using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wire = GameServer.Modules.Economy.Protocol.V1;

namespace GameServer.Modules.Economy;

/// <summary>
/// Framework Economy module (ECO-01..08): <c>wallet</c> and <c>inventory</c> aggregates, Resource Catalog from
/// <c>resources.json</c>, ledger projection, external-grant claims. Other modules change resources with
/// <see cref="EconomyOperations"/> inside their own commands. GM API: resource adjustment and ledger (GM-03, GM-06).
/// </summary>
public sealed class EconomyModule : IGameModule, IGmApiModule
{
    public const string AdjustRoute = "/gm/v1/players/{playerId}/resources/adjust";
    public const string LedgerRoute = "/gm/v1/players/{playerId}/ledger";

    public ModuleDescriptor Describe() => EconomyModuleDefinition.Describe();

    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        routes.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(
            AdjustRoute,
            AdjustResourceHandler.Operation,
            EconomyPermissions.ResourceAdjust,
            body => string.IsNullOrWhiteSpace(body.Resource)
                ? throw new GameServerException(CoreErrorCodes.BadRequest, "resource is required.")
                : new AdjustResourceCommand(body.Resource, body.Delta, body.Reason!));

        routes.MapGet(LedgerRoute, async (HttpContext http, string playerId) =>
                GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    EconomyOperations.LedgerCollection,
                    "playerId",
                    GmHttp.RequirePlayerId(playerId),
                    "committedAtUtc",
                    descending: true,
                    d => d.Content)))
            .AsGmEndpoint(EconomyPermissions.PlayerRead);
    }

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.TryAddSingleton(sp => ResourceCatalog.Load(sp.GetRequiredService<IGameConfig>()));
        services.AddAggregateWireMapper<WalletState>(EconomyAggregates.Wallet, state =>
        {
            var wire = new Wire.WalletState();
            wire.Balances.Add(state.Balances.ToDictionary());
            return wire;
        });
        services.AddAggregateWireMapper<InventoryState>(EconomyAggregates.Inventory, state =>
        {
            var wire = new Wire.InventoryState();
            wire.Items.Add(state.Items.ToDictionary());
            return wire;
        });
    }
}

/// <summary>Body of <c>POST /gm/v1/players/{playerId}/resources/adjust</c>.</summary>
public sealed record AdjustResourceBody(string? Resource, long Delta, string? Reason) : IGmCommandBody;

/// <summary>
/// Development cheat module (OPS-09): register only when <c>GameServerOptions.AllowsDevelopmentTools</c>; the startup
/// validator rejects its Dev endpoint in Production.
/// </summary>
public sealed class CheatModule : IGameModule, IPlayerApiModule
{
    public const string GrantRoute = "/api/v1/cheat/grant";

    public ModuleDescriptor Describe() => CheatModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes) =>
        routes.MapDevCommand<Wire.CheatGrantRequest, CheatGrantCommand, CheatGrantResult>(
            GrantRoute,
            CheatGrantHandler.Operation,
            request => string.IsNullOrWhiteSpace(request.Resource)
                ? throw new GameServerException(CoreErrorCodes.BadRequest, "resource is required.")
                : new CheatGrantCommand(request.Resource, request.Quantity),
            result => new Wire.CheatGrantResult { BalanceAfter = result.BalanceAfter });
}
