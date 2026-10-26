using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Modules.Economy;

/// <summary>GM permissions owned by the Economy module (SEC-07 catalog).</summary>
public static class EconomyPermissions
{
    public const string ResourceAdjust = "resource.adjust";

    /// <summary>Shared read permission (also declared by module gm); the catalog is the union of all modules.</summary>
    public const string PlayerRead = "player.read";
}

/// <summary>GM resource adjustment (GM-03): positive delta grants, negative delta debits.</summary>
public sealed record AdjustResourceCommand(string Resource, long Delta, string Reason);

public sealed record AdjustResourceResult(long BalanceBefore, long BalanceAfter);

/// <summary>
/// <c>economy.adjust</c>: changes one resource of the target player with ledger <c>sourceType = gm_adjust</c> and an audit
/// entry, both in the outbox of the same receipt (GM-03, SEC-14). The audit references the receipt instead of copying
/// the delta: the ledger entry of that receipt holds it.
/// </summary>
public sealed class AdjustResourceHandler(ResourceCatalog catalog) : ICommandHandler<AdjustResourceCommand, AdjustResourceResult>
{
    public const string Operation = "economy.adjust";
    public const string SourceType = "gm_adjust";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [EconomyAggregates.Wallet, EconomyAggregates.Inventory],
        profile: SecurityProfile.Gm,
        permission: EconomyPermissions.ResourceAdjust);

    public ValueTask<CommandOutcome<AdjustResourceResult>> HandleAsync(AdjustResourceCommand request, ICommandContext context)
    {
        var before = EconomyOperations.Balance(context, catalog, request.Resource);
        var source = new LedgerSource(SourceType);
        var error = request.Delta switch
        {
            0 or long.MinValue => EconomyErrors.InvalidQuantity,
            > 0 => EconomyOperations.Grant(context, catalog, request.Resource, request.Delta, source),
            _ => EconomyOperations.Debit(context, catalog, request.Resource, -request.Delta, source),
        };
        if (error is not null)
        {
            return ValueTask.FromResult(CommandOutcome<AdjustResourceResult>.Reject(error));
        }

        context.AddAudit(EconomyPermissions.ResourceAdjust, Operation, request.Reason);
        return ValueTask.FromResult(CommandOutcome<AdjustResourceResult>.Ok(
            new AdjustResourceResult(before, EconomyOperations.Balance(context, catalog, request.Resource))));
    }
}
