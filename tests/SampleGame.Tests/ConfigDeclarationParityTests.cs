using GameServer.Tests.Shared;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>FR-018, US1-10 (slice 015 T028): the kit reports exactly the host's lines, in the same order.</summary>
public sealed class ConfigDeclarationParityTests
{
    public static TheoryData<string> Faults => [.. SeededFaults.RuleAndCatalogNames];

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task The_kit_reports_the_lines_of_the_host_for_each_seeded_fault(string fault)
    {
        using var seeded = new SeededConfig();
        SeededFaults.Apply(fault, seeded);

        var host = await ConfigStartupReportTests.StartupFailsAsync(seeded, SeededFaults.Modules(fault));
        var kit = ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(seeded.Path), SeededFaults.KitModules(fault));

        kit.Errors.ShouldBe(host);
    }

    [Fact]
    public async Task The_kit_keeps_the_host_order_across_files_and_modules()
    {
        using var seeded = new SeededConfig();
        SeededFaults.Edit(seeded, "resources.json", r => r["resources"]![1]!["maxQuantity"] = 0);
        SeededFaults.Edit(seeded, "iap.json", r => r["products"]!["google:gems_100"]!["grants"]![0]!["quantity"] = 0);
        SeededFaults.Edit(seeded, "battle.json", r => r["stages"]![1]!["xp"] = "70");
        SeededFaults.Edit(seeded, "shop.json", r => r["items"]!["potion_pack"]!["price"]!["amount"] = 0);

        var host = await ConfigStartupReportTests.StartupFailsAsync(seeded, GameHttp.FullGame);
        var kit = ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(seeded.Path), SeededFaults.KitModules("none"));

        host.Count.ShouldBe(4);
        kit.Errors.ShouldBe(host);
    }
}
