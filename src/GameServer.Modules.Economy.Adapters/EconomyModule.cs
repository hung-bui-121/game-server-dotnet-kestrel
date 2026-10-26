using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wire = GameServer.Modules.Economy.Protocol.V1;

namespace GameServer.Modules.Economy;

/// <summary>
/// Framework Economy module (ECO-01..08): <c>wallet</c> and <c>inventory</c> aggregates, Resource Catalog from
/// <c>resources.json</c>, ledger projection, external-grant claims. Other modules change resources with
/// <see cref="EconomyOperations"/> inside their own commands.
/// </summary>
public sealed class EconomyModule : IGameModule
{
    public ModuleDescriptor Describe() => EconomyModuleDefinition.Describe();

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
