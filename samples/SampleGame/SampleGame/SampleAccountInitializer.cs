using GameServer.Accounts;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules.Economy;

namespace SampleGame;

/// <summary>Content of the optional, server-only <c>account.json</c> (FR-050): what a new account starts with.</summary>
public sealed record SampleAccountSettings
{
    /// <summary>Gold granted to every new account (0 = none); checked by rule <c>sample.account</c>.</summary>
    public long StartingGold { get; init; }
}

/// <summary>
/// Account initializer <c>sample.starting_gold</c> (FG-015, FR-050): grants <see cref="SampleAccountSettings.StartingGold"/> gold in
/// the transaction that creates the account, with ledger source <c>account_init</c>. Without <c>account.json</c> (the test config)
/// or with a value of 0 it changes nothing, so the creation writes no receipt.
/// </summary>
public sealed class SampleAccountInitializer(ResourceCatalog resources) : IAccountInitializer
{
    public const string Name = "sample.starting_gold";
    public const string ConfigFile = "account.json";
    public const string LedgerSourceType = "account_init";
    public const string Resource = "gold";

    public static AccountInitializerRegistration Registration { get; } =
        AccountInitializerRegistration.Of<SampleAccountInitializer>(Name, [EconomyAggregates.Wallet]);

    public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
    {
        if (!context.Config.HasFile(ConfigFile))
        {
            return ValueTask.FromResult<ErrorCode?>(null);
        }

        var startingGold = context.Config.GetFile<SampleAccountSettings>(ConfigFile).StartingGold;
        return ValueTask.FromResult(
            startingGold == 0
                ? null
                : EconomyOperations.Grant(context, resources, Resource, startingGold, new LedgerSource(LedgerSourceType)));
    }
}
