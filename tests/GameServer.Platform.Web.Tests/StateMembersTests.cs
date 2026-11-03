using GameServer.Platform.Web.Aggregates;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FG-027 groundwork: the member tree of a state is what the document serializer persists (research R16, V9).</summary>
public sealed class StateMembersTests
{
    public enum Mode
    {
        None,
        Casual,
        Ranked,
    }

    public sealed record ActiveBattle
    {
        public required long Seed { get; init; }

        public DateTimeOffset StartedAt { get; init; }

        public int WinCash { get; init; }
    }

    public sealed record PendingChest(string Tier, int Chapter);

    public sealed record Slot(DateTimeOffset? UnlockEndUtc, int Index);

    public sealed record GunState
    {
        public int Level { get; init; }
    }

    public sealed record ProgressState
    {
        public required int Cash { get; init; }

        public IReadOnlyDictionary<string, GunState> Guns { get; init; } = new Dictionary<string, GunState>();

        public IReadOnlyList<Slot> Slots { get; init; } = [];

        public IReadOnlyList<PendingChest> Pending { get; init; } = [];

        public ActiveBattle? ActiveBattle { get; init; }

        public Mode Mode { get; init; }

        public DateTimeOffset? LastSeen { get; init; }

        public int DoubleCash => Cash * 2;
    }

    public sealed record Node
    {
        public int Value { get; init; }

        public Node? Next { get; init; }
    }

    [Fact]
    public void Paths_follow_csharp_names_with_elements_transparent()
    {
        var members = StateMembers.Of(typeof(ProgressState));

        members.All.Select(m => m.Path).ShouldBe(
        [
            "Cash",
            "Guns",
            "Guns.Level",
            "Slots",
            "Slots.UnlockEndUtc",
            "Slots.Index",
            "Pending",
            "Pending.Tier",
            "Pending.Chapter",
            "ActiveBattle",
            "ActiveBattle.Seed",
            "ActiveBattle.StartedAt",
            "ActiveBattle.WinCash",
            "Mode",
            "LastSeen",
        ]);
    }

    [Fact]
    public void Computed_properties_are_not_state()
    {
        var members = StateMembers.Of(typeof(ProgressState));

        members.Resolve("DoubleCash", out _).ShouldBeFalse();
        members.All.ShouldNotContain(m => m.Name == "DoubleCash");
    }

    [Fact]
    public void Node_kinds_describe_collections_dictionaries_and_nullables()
    {
        var members = StateMembers.Of(typeof(ProgressState));

        members.Resolve("Guns", out var guns).ShouldBeTrue();
        var dictionary = guns!.Node.ShouldBeOfType<StateDictionary>();
        dictionary.KeyType.ShouldBe(typeof(string));
        dictionary.Value.ShouldBeOfType<StateObject>();

        members.Resolve("Slots", out var slots).ShouldBeTrue();
        slots!.Node.ShouldBeOfType<StateCollection>().Element.ShouldBeOfType<StateObject>();

        members.Resolve("Slots.UnlockEndUtc", out var unlock).ShouldBeTrue();
        unlock!.Node.ShouldBeOfType<StateNullable>().Inner.ShouldBeOfType<StateLeaf>().Type.ShouldBe(typeof(DateTimeOffset));
        unlock.JsonName.ShouldBe("unlockEndUtc");

        members.Resolve("ActiveBattle.Seed", out var seed).ShouldBeTrue();
        seed!.Node.ShouldBeOfType<StateLeaf>().Type.ShouldBe(typeof(long));

        members.Resolve("Mode", out var mode).ShouldBeTrue();
        mode!.Node.ShouldBeOfType<StateLeaf>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("ActiveBattle.Unknown")]
    [InlineData("Cash.Value")]
    [InlineData("activeBattle.seed")]
    public void Resolve_of_an_unknown_path_is_false(string path)
    {
        StateMembers.Of(typeof(ProgressState)).Resolve(path, out var member).ShouldBeFalse();
        member.ShouldBeNull();
    }

    [Fact]
    public void A_type_cycle_is_not_expanded()
    {
        var members = StateMembers.Of(typeof(Node));

        members.Resolve("Next", out var next).ShouldBeTrue();
        next!.Node.ShouldBeOfType<StateUnfillable>().Reason.ShouldBe("type cycle");
        members.All.Select(m => m.Path).ShouldBe(["Value", "Next"]);
    }
}
