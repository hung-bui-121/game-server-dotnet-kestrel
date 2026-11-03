using Shouldly;

namespace GameServer.Platform.Tests;

/// <summary>One decision for the Dev profile (FG-010, FR-006, research R1): Development and Test only, no setting.</summary>
public sealed class DevProfileDecisionTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Test", true)]
    [InlineData("development", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    [InlineData("Qa", false)]
    [InlineData("", false)]
    public void Dev_profile_is_allowed_only_where_development_tools_are(string deploymentEnvironment, bool expected)
    {
        var options = new GameServerOptions { DeploymentEnvironment = deploymentEnvironment };

        options.AllowsDevProfile.ShouldBe(expected);
        options.AllowsDevProfile.ShouldBe(options.AllowsDevelopmentTools);
    }

    [Fact]
    public void Default_options_allow_the_dev_profile_because_the_default_environment_is_development()
    {
        var options = new GameServerOptions();

        options.DeploymentEnvironment.ShouldBe("Development");
        options.AllowsDevProfile.ShouldBeTrue();
    }
}
