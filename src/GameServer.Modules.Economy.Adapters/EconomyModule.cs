using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
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
    public const string AdjustRoute = EconomyRoutes.AdjustTemplate;
    public const string LedgerRoute = EconomyRoutes.LedgerTemplate;

    public ModuleDescriptor Describe() => EconomyModuleDefinition.Describe();

    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        routes.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(
            EconomyRoutes.Adjust,
            // [Required] on Resource is enforced before toDomain; the check stays as the guard of the command.
            body => string.IsNullOrWhiteSpace(body.Resource)
                ? throw new GameServerException(CoreErrorCodes.BadRequest, "resource is required.")
                : new AdjustResourceCommand(body.Resource, body.Delta, body.Reason!))
            .WithSummary("Adjust a resource balance of the player (GM-03).")
            .WithGmConfirm(GmConfirm.Typed);

        routes.MapDeclared(EconomyRoutes.Ledger, async (HttpContext http, string playerId) =>
                GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    EconomyOperations.LedgerCollection,
                    "playerId",
                    GmHttp.RequirePlayerId(playerId),
                    "committedAtUtc",
                    descending: true,
                    d => d.Content)))
            .ProducesGmPage<JsonObject>()
            .WithSummary("List the resource ledger of a player, newest first (paged).")
            .AsGmEndpoint(EconomyPermissions.PlayerRead);

        // The values AdjustResourceBody.Resource accepts ([GmOptions]); the command still checks the id against the catalog.
        routes.MapDeclared(EconomyRoutes.ResourceOptions, (HttpContext http) => GmHttp.Json(new GmOptionList(
                http.RequestServices.GetRequiredService<ResourceCatalog>().All
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .Select(r => new GmOption(r.Id, r.DisplayName))
                    .ToArray())))
            .Produces<GmOptionList>()
            .WithSummary("List the resources of the catalog a GM may adjust.")
            .AsGmEndpoint(EconomyPermissions.GmAccess);
    }

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        // ResourceCatalog is a declared catalog (EconomyModuleDefinition): the platform builds and registers it (FG-018).
        services.AddAggregateWireMapper<WalletState, Wire.WalletState>(EconomyAggregates.Wallet, state =>
        {
            var wire = new Wire.WalletState();
            wire.Balances.Add(state.Balances.ToDictionary());
            return wire;
        });
        services.AddAggregateWireMapper<InventoryState, Wire.InventoryState>(EconomyAggregates.Inventory, state =>
        {
            var wire = new Wire.InventoryState();
            wire.Items.Add(state.Items.ToDictionary());
            return wire;
        });
    }
}

/// <summary>
/// Body of <c>POST /gm/v1/players/{playerId}/resources/adjust</c>. <see cref="Resource"/> is required (null, empty and white space
/// refused) and <see cref="Delta"/> must be present: a missing delta is a 400, never a 0-adjustment (slice 017 review r1 m3).
/// </summary>
public sealed record AdjustResourceBody(
    [property: Required, GmOptions("economy.resource_options")] string? Resource,
    [property: Required] long Delta,
    string? Reason) : IGmCommandBody;

/// <summary>
/// Development cheat module (OPS-09): register only when <c>GameServerOptions.AllowsDevelopmentTools</c>; outside
/// Development/Test the startup validator rejects its Dev endpoint and command, the pipeline and the runner refuse them.
/// </summary>
public sealed class CheatModule : IGameModule, IPlayerApiModule
{
    public const string GrantRoute = CheatRoutes.GrantTemplate;

    public ModuleDescriptor Describe() => CheatModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes) =>
        routes.MapDevCommand<Wire.CheatGrantRequest, CheatGrantCommand, CheatGrantResult>(
            CheatRoutes.Grant,
            request => string.IsNullOrWhiteSpace(request.Resource)
                ? throw new GameServerException(CoreErrorCodes.BadRequest, "resource is required.")
                : new CheatGrantCommand(request.Resource, request.Quantity),
            result => new Wire.CheatGrantResult { BalanceAfter = result.BalanceAfter });
}
