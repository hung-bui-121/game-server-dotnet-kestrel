using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform.Registry;
using Sample.Counter.Adapters;
using SampleGame.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>Deletion treatments of the framework's built-in modules (PRIV-01, FR-019, data-model.md).</summary>
public sealed class DeletionPlanTests
{
    private static ModuleRegistry SampleHostRegistry()
    {
        var registry = new ModuleRegistry();
        IGameModule[] modules = [new IdentityModule(), new EconomyModule(), new IapModule(), new SampleGameModule(), new CounterModule(), new GmModule()];
        foreach (var module in modules)
        {
            registry.Add(module, serviceOnly: false);
        }

        return registry;
    }

    [Fact]
    public void Built_in_modules_declare_a_treatment_for_every_collection()
    {
        var registry = SampleHostRegistry();

        registry.Validate(new GameServerOptions { GameId = "test", EnvironmentId = "dev" }, null).ShouldBeEmpty();

        var plan = registry.DeletionPlan().Where(d => !d.IsPlayerAggregate).ToDictionary(d => d.Collection, d => d.Treatment);
        plan.ShouldBe(new Dictionary<string, DeletionTreatment>
        {
            ["receipts"] = DeletionTreatment.Anonymize,
            ["gm_audit"] = DeletionTreatment.Anonymize,
            ["dataprotection"] = DeletionTreatment.NoPlayerData,
            ["gate"] = DeletionTreatment.NoPlayerData,
            ["leases"] = DeletionTreatment.NoPlayerData,
            ["identities"] = DeletionTreatment.Remove,
            ["sessions"] = DeletionTreatment.Remove,
            ["ledger"] = DeletionTreatment.Anonymize,
            ["extref_claims"] = DeletionTreatment.Anonymize,
            ["counter_log"] = DeletionTreatment.Anonymize,
            ["gm_staff"] = DeletionTreatment.NoPlayerData,
            ["gm_staff_identities"] = DeletionTreatment.NoPlayerData,
            ["gm_sessions"] = DeletionTreatment.NoPlayerData,
        });
        registry.DeletionPlan().Where(d => d.IsPlayerAggregate).ShouldAllBe(d => d.Treatment == DeletionTreatment.Remove);
    }

    [Fact]
    public void The_gm_module_no_longer_provisions_the_receipts_player_index()
    {
        // ix_receipts_player is platform-provisioned so the purge has it without the gm module (research R5).
        new GmModule().Describe().Collections.ShouldNotContain(c => c.Name == "receipts");
    }
}
