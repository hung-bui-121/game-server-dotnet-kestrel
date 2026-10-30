using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>Slice 011 R2: canonical deployment environments and the standard flavor table (FR-002, FR-003).</summary>
public sealed class DeploymentEnvironmentsTests
{
    [Theory]
    [InlineData("development", "Development")]
    [InlineData("STAGING", "Staging")]
    [InlineData("Test", "Test")]
    [InlineData("production", "Production")]
    public void Known_values_normalize_to_the_canonical_spelling(string value, string expected)
    {
        DeploymentEnvironments.TryNormalize(value, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Dev")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_values_are_refused(string? value)
    {
        DeploymentEnvironments.TryNormalize(value, out var normalized).ShouldBeFalse();
        normalized.ShouldBeEmpty();
    }

    [Fact]
    public void Standard_flavors_match_the_fr002_table()
    {
        StandardFlavors.DeploymentEnvironmentById.ShouldBe(
            new Dictionary<string, string>
            {
                ["local"] = "Development",
                ["dev"] = "Development",
                ["beta"] = "Staging",
                ["uat"] = "Staging",
                ["live"] = "Production",
            },
            ignoreOrder: true);
        StandardFlavors.DeploymentEnvironmentById.ContainsKey("Live").ShouldBeFalse("keys are ordinal lowercase");
    }

    [Fact]
    public void Describe_lists_all_five_flavors()
    {
        var text = StandardFlavors.Describe();

        foreach (var (id, environment) in StandardFlavors.DeploymentEnvironmentById)
        {
            text.ShouldContain($"{id}->{environment}");
        }
    }
}
