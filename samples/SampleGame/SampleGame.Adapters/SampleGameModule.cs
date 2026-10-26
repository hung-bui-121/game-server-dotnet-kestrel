using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wire = SampleGame.Protocol.V1;

namespace SampleGame.Adapters;

/// <summary>
/// Game module of the sample (SMP-02, SMP-03, SMP-05): progression and battle aggregates, battle and shop commands.
/// Login (SMP-01) comes from the Identity module and IAPurchase (SMP-04) from the IAP module.
/// </summary>
public sealed class SampleGameModule : IGameModule, IPlayerApiModule, IGmApiModule
{
    public const string GmProgressionRoute = "/gm/v1/players/{playerId}/progression";

    public const string StartRoute = "/api/v1/battle/start";
    public const string EndRoute = "/api/v1/battle/end";
    public const string BuyRoute = "/api/v1/shop/buy";

    public ModuleDescriptor Describe() => SampleGameModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.TryAddSingleton(sp => BattleCatalog.Load(sp.GetRequiredService<IGameConfig>()));
        services.TryAddSingleton(sp => ShopCatalog.Load(sp.GetRequiredService<IGameConfig>()));
        services.TryAddSingleton<IBattleStartPolicy, ReplaceActiveBattlePolicy>();
        services.TryAddSingleton<IBattleResultPolicy, EligibilityOnlyResultPolicy>();

        services.AddAggregateWireMapper<ProgressionState>(
            SampleGameAggregates.Progression,
            s => new Wire.ProgressionState { Level = s.Level, Xp = s.Xp, Stage = s.Stage });
        services.AddAggregateWireMapper<BattleState>(SampleGameAggregates.Battle, ToWire);
    }

    public void MapGmApi(IEndpointRouteBuilder routes) =>
        routes.MapGmCommand<SetProgressionBody, SetProgressionCommand, SetProgressionResult>(
            GmProgressionRoute,
            SetProgressionHandler.Operation,
            SampleGamePermissions.ProgressionEdit,
            body =>
            {
                if (body is { Level: null, Xp: null, Stage: null } || body.Level < 1 || body.Xp < 0 || body.Stage < 1)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "Set at least one of level (>= 1), xp (>= 0), stage (>= 1).");
                }

                return new SetProgressionCommand(body.Level, body.Xp, body.Stage, body.Reason!);
            });

    public void MapPlayerApi(IEndpointRouteBuilder routes)
    {
        routes.MapPlayerCommand<Wire.StartBattleRequest, StartBattleCommand, StartBattleResult>(
            StartRoute,
            StartBattleHandler.Operation,
            request => new StartBattleCommand(request.Stage),
            result => new Wire.StartBattleResult
            {
                BattleId = result.BattleId,
                Seed = result.Seed,
                ExpiresAtUtcMs = result.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            });

        routes.MapPlayerCommand<Wire.EndBattleRequest, EndBattleCommand, EndBattleResult>(
            EndRoute,
            EndBattleHandler.Operation,
            request => string.IsNullOrEmpty(request.BattleId) || request.BattleId.Length > 64
                ? throw new GameServerException(CoreErrorCodes.BadRequest, "battle_id is required (≤ 64 chars).")
                : new EndBattleCommand(request.BattleId, request.Won),
            result =>
            {
                var wire = new Wire.EndBattleResult { XpGained = result.XpGained, StageUnlocked = result.StageUnlocked };
                wire.Rewards.AddRange(result.Rewards.Select(r => new Wire.RewardLine { Resource = r.Resource, Quantity = r.Quantity }));
                return wire;
            });

        routes.MapPlayerCommand<Wire.BuyItemRequest, BuyItemCommand, BuyItemResult>(
            BuyRoute,
            BuyItemHandler.Operation,
            request => string.IsNullOrEmpty(request.ShopItemId) || request.ShopItemId.Length > 64
                ? throw new GameServerException(CoreErrorCodes.BadRequest, "shop_item_id is required (≤ 64 chars).")
                : new BuyItemCommand(request.ShopItemId, request.Quantity),
            result => new Wire.BuyItemResult { PricePaid = result.PricePaid, Granted = result.Granted });
    }

    private static Wire.BattleState ToWire(BattleState state)
    {
        var wire = new Wire.BattleState
        {
            Stats = new Wire.BattleStats
            {
                Started = state.Stats.Started,
                Won = state.Stats.Won,
                Lost = state.Stats.Lost,
                Abandoned = state.Stats.Abandoned,
                Expired = state.Stats.Expired,
            },
        };
        if (state.ActiveBattle is { } active)
        {
            wire.ActiveBattle = new Wire.ActiveBattle
            {
                BattleId = active.BattleId,
                Stage = active.Stage,
                StartedAtUtcMs = active.StartedAtUtc.ToUnixTimeMilliseconds(),
                ExpiresAtUtcMs = active.ExpiresAtUtc.ToUnixTimeMilliseconds(),
                Seed = active.Seed,
            };
        }

        return wire;
    }
}

/// <summary>Body of <c>POST /gm/v1/players/{playerId}/progression</c>; null fields are unchanged.</summary>
public sealed record SetProgressionBody(int? Level, long? Xp, int? Stage, string? Reason) : IGmCommandBody;
