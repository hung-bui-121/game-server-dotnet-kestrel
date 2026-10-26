using GameServer.Actors;
using GameServer.Commands;
using GameServer.Modules;

namespace GameServer.Modules.Economy;

/// <summary>Business half of the Economy module descriptor.</summary>
public static class EconomyModuleDefinition
{
    public const string ModuleName = "economy";

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.1.0",
        DependsOn = ["identity"],
        RequiredConfigFiles = [ResourceCatalog.ConfigFile],
        Aggregates = [EconomyAggregates.WalletDescriptor, EconomyAggregates.InventoryDescriptor],
        Collections =
        [
            new CollectionSpec(EconomyOperations.LedgerCollection, [new IndexSpec("ix_ledger_player", "playerId, committedAtUtc")]),
            new CollectionSpec(EconomyOperations.ClaimCollection),
        ],
        ErrorCodes = EconomyErrors.All,
        OutboxProjections = [new OutboxProjection(EconomyOperations.LedgerKind, EconomyOperations.LedgerCollection)],
    };
}

/// <summary>Development cheat (OPS-09): grants a resource; still ledgered with <c>sourceType = cheat</c>.</summary>
public sealed record CheatGrantCommand(string Resource, long Quantity);

public sealed record CheatGrantResult(long BalanceAfter);

public sealed class CheatGrantHandler(ResourceCatalog catalog) : ICommandHandler<CheatGrantCommand, CheatGrantResult>
{
    public const string Operation = "cheat.grant";
    public const string SourceType = "cheat";

    public static CommandDescriptor Descriptor { get; } =
        new(Operation, touches: [EconomyAggregates.Wallet, EconomyAggregates.Inventory], profile: SecurityProfile.Dev);

    public ValueTask<CommandOutcome<CheatGrantResult>> HandleAsync(CheatGrantCommand request, ICommandContext context)
    {
        if (EconomyOperations.Grant(context, catalog, request.Resource, request.Quantity, new LedgerSource(SourceType)) is { } error)
        {
            return ValueTask.FromResult(CommandOutcome<CheatGrantResult>.Reject(error));
        }

        return ValueTask.FromResult(CommandOutcome<CheatGrantResult>.Ok(new CheatGrantResult(EconomyOperations.Balance(context, catalog, request.Resource))));
    }
}

public static class CheatModuleDefinition
{
    public const string ModuleName = "economy.cheat";

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.1.0",
        DependsOn = [EconomyModuleDefinition.ModuleName],
        Commands = [Command.Define<CheatGrantCommand, CheatGrantResult>(CheatGrantHandler.Descriptor).HandledBy<CheatGrantHandler>()],
    };
}
