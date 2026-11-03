using GameServer.Accounts;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using Shouldly;

namespace GameServer.Platform.Tests.Accounts;

/// <summary>
/// Slice 016 T011 (FR-010, FR-018, public-api §1.1/§1.2): the registration keeps what the module declared; value rules are
/// startup lines (T023), never exceptions here.
/// </summary>
public sealed class AccountInitializerRegistrationTests
{
    [Fact]
    public void Of_keeps_name_type_touches_in_order_and_claim_count()
    {
        var registration = AccountInitializerRegistration.Of<NoopInitializer>("probe.starting_state", ["wallet", "starter"], maxClaimMarkers: 1);

        registration.Name.ShouldBe("probe.starting_state");
        registration.InitializerType.ShouldBe(typeof(NoopInitializer));
        registration.Touches.ShouldBe(["wallet", "starter"]);
        registration.MaxClaimMarkers.ShouldBe(1);
    }

    [Fact]
    public void Claim_count_defaults_to_zero_and_values_are_not_checked_here()
    {
        AccountInitializerRegistration.Of<NoopInitializer>("probe.a", []).MaxClaimMarkers.ShouldBe(0);

        // Invalid name, profile, duplicates and an out-of-range claim count are reported by the startup check (T023).
        var unvalidated = AccountInitializerRegistration.Of<NoopInitializer>("Not Valid", ["profile", "profile"], maxClaimMarkers: 9);
        unvalidated.Touches.ShouldBe(["profile", "profile"]);
        unvalidated.MaxClaimMarkers.ShouldBe(9);
    }

    [Fact]
    public void Touches_are_a_copy_of_the_given_list()
    {
        var touches = new List<string> { "wallet" };

        var registration = AccountInitializerRegistration.Of<NoopInitializer>("probe.a", touches);
        touches.Add("starter");

        registration.Touches.ShouldBe(["wallet"]);
        registration.Touches.ShouldNotBeSameAs(touches);
    }

    [Fact]
    public void Null_name_or_touches_throw()
    {
        Should.Throw<ArgumentNullException>(() => AccountInitializerRegistration.Of<NoopInitializer>(null!, [])).ParamName.ShouldBe("name");
        Should.Throw<ArgumentNullException>(() => AccountInitializerRegistration.Of<NoopInitializer>("probe.a", null!)).ParamName.ShouldBe("touches");
    }

    [Fact]
    public void A_descriptor_without_initializers_has_an_empty_list()
    {
        new ModuleDescriptor { Name = "probe" }.AccountInitializers.ShouldBeEmpty();
    }

    private sealed class NoopInitializer : IAccountInitializer
    {
        public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context) => ValueTask.FromResult<ErrorCode?>(null);
    }
}
