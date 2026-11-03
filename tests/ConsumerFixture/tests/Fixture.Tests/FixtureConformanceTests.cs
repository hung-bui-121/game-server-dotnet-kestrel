using Fixture.Game;
using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Aggregates;
using GameServer.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wire = Fixture.V1;

namespace Fixture.Tests;

/// <summary>
/// FG-027 from a game repository (FR-061..FR-063, FR-066): the kit checks the game's own mapper through public API only — the
/// <c>card</c> section with a nested record list, a dictionary, a nullable member, dates as UTC milliseconds and a hidden member.
/// </summary>
public sealed class FixtureConformanceTests
{
    [Fact]
    public void The_game_and_framework_mappers_pass()
    {
        var report = WireConformance.CheckModules([new IdentityModule(), new FixtureModule()]);

        report.CheckedAggregates.ShouldBe([FixtureModuleDefinition.CardAggregate, IdentityAggregates.Profile]);
        report.ThrowIfFailed();
    }

    // Consumer guide §5.5: a state a command produces, as the sample; the With* options chain.
    [Fact]
    public void A_supplied_sample_state_passes()
    {
        var options = new WireConformanceOptions()
            .WithSampleState(FixtureModuleDefinition.CardAggregate, new CardState
            {
                Title = "after save",
                Counts = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
                Slots = [new CardSlot("sword", DateTimeOffset.UnixEpoch.AddDays(1)), new CardSlot("shield", DateTimeOffset.UnixEpoch.AddDays(2))],
                Nickname = "n",
                UpdatedUtc = DateTimeOffset.UnixEpoch.AddDays(3),
                InternalNote = "kept on the server",
            });

        WireConformance.CheckModules([new IdentityModule(), new FixtureModule()], options).ThrowIfFailed();
    }

    [Fact]
    public void A_mapper_that_drops_a_nested_member_fails_naming_it()
    {
        var report = WireConformance.CheckModules([new DroppingCardModule()]);

        report.Passed.ShouldBeFalse();
        report.Findings.Select(f => (f.Problem, f.Path)).ShouldBe(
        [
            (WireConformanceProblem.FieldAtDefault, "slots.unlock_end_utc_ms"),
            (WireConformanceProblem.MemberNotOnWire, "Slots.UnlockEndUtc"),
        ]);
        Should.Throw<WireConformanceException>(report.ThrowIfFailed).Message.ShouldContain("[aggregate card] MemberNotOnWire: Slots.UnlockEndUtc");
    }

    /// <summary>The fixture's card aggregate with a test-local copy of its mapper that forgets the slot unlock time.</summary>
    private sealed class DroppingCardModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "fixture.dropping",
            Aggregates = [new PlayerAggregateDescriptor<CardState>(FixtureModuleDefinition.CardAggregate, 1, clientVisible: true, () => new CardState())],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
            services.AddAggregateWireMapper<CardState, Wire.CardState>(FixtureModuleDefinition.CardAggregate, ToWire, o => o.Hidden(nameof(CardState.InternalNote)));

        private static Wire.CardState ToWire(CardState state)
        {
            var wire = new Wire.CardState { Title = state.Title, UpdatedUtcMs = state.UpdatedUtc.ToUnixTimeMilliseconds() };
            foreach (var (key, count) in state.Counts)
            {
                wire.Counts[key] = count;
            }

            foreach (var slot in state.Slots)
            {
                wire.Slots.Add(new Wire.CardState.Types.Slot { Item = slot.Item });
            }

            if (state.Nickname is not null)
            {
                wire.Nickname = state.Nickname;
            }

            return wire;
        }
    }
}
