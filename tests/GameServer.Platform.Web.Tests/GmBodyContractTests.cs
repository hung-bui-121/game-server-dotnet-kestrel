using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GameServer.Platform.Web.Endpoints;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T009 (FR-021, FR-010, research R2, V13): the attribute walk over the System.Text.Json contract of a GM body reads
/// property-target and positional-parameter attributes once each, uses JSON names, walks nested objects and object lists to
/// depth 3, and is cached per type.
/// </summary>
public sealed class GmBodyContractTests
{
    public enum Kind
    {
        First,
    }

    public sealed record Leaf([property: Range(1, 2)] int Deep, Leaf? Again);

    public sealed record Inner([property: StringLength(5)] string? Label, Leaf? Leaf, List<Leaf>? Leaves);

    public sealed record Body(
        [property: Range(1, 10)] int OnProperty,
        [Range(2, 20)] int OnParameter,
        [property: Required, MaxLength(9)][MaxLength(9)] string? Both,
        [property: JsonPropertyName("custom_name")] string? Renamed,
        [property: GmOptions("probe.options")] List<string>? Picks,
        [property: Description("shown only")] string? Described,
        [property: Required] long Delta,
        [property: Required] long? OptionalDelta,
        Inner? Nested,
        List<Inner>? Items,
        Dictionary<string, Inner>? Map,
        JsonNode? Free,
        Kind Choice,
        DateTimeOffset? At);

    [Fact]
    public void Property_and_parameter_targets_are_read_once_each()
    {
        var contract = GmBodyContract.For(typeof(Body));

        Property(contract, "onProperty").Validations.ShouldHaveSingleItem().ShouldBeOfType<RangeAttribute>().Maximum.ShouldBe(10);
        Property(contract, "onParameter").Validations.ShouldHaveSingleItem().ShouldBeOfType<RangeAttribute>().Maximum.ShouldBe(20);
        var both = Property(contract, "both");
        both.Validations.Select(v => v.GetType()).ShouldBe([typeof(RequiredAttribute), typeof(MaxLengthAttribute)]);
        both.ClrName.ShouldBe(nameof(Body.Both));
    }

    [Fact]
    public void Json_names_follow_the_naming_policy_and_json_property_name()
    {
        var contract = GmBodyContract.For(typeof(Body));

        contract.Properties.Select(p => p.JsonName).ShouldBe(
        [
            "onProperty", "onParameter", "both", "custom_name", "picks", "described", "delta", "optionalDelta", "nested", "items", "map",
            "free", "choice", "at",
        ]);
        Property(contract, "custom_name").ClrName.ShouldBe(nameof(Body.Renamed));
    }

    [Fact]
    public void Options_and_description_are_kept_for_the_document()
    {
        var contract = GmBodyContract.For(typeof(Body));

        Property(contract, "picks").OptionsRoute.ShouldBe("probe.options");
        var described = Property(contract, "described");
        described.Description.ShouldBe("shown only");
        described.Validations.ShouldBeEmpty();
    }

    [Fact]
    public void Non_nullable_value_types_need_presence_and_nullable_ones_do_not()
    {
        // Review r1 m3: a value type that cannot be null is required with or without [Required]; nullable = optional.
        var contract = GmBodyContract.For(typeof(Body));

        Property(contract, "delta").RequiresPresence.ShouldBeTrue();
        Property(contract, "onProperty").RequiresPresence.ShouldBeTrue();
        Property(contract, "onProperty").IsRequired.ShouldBeTrue();
        Property(contract, "choice").RequiresPresence.ShouldBeTrue();
        Property(contract, "optionalDelta").RequiresPresence.ShouldBeFalse();
        Property(contract, "optionalDelta").IsRequired.ShouldBeTrue();
        Property(contract, "both").RequiresPresence.ShouldBeFalse();
        Property(contract, "at").IsRequired.ShouldBeFalse();
        contract.NeedsPresence.ShouldBeTrue();
        GmBodyContract.For(typeof(OnlyNullable)).NeedsPresence.ShouldBeFalse();
        GmBodyContract.For(typeof(OnlyNullable)).HasRules.ShouldBeFalse();
    }

    public sealed record OnlyNullable(int? Count, string? Text, DateTimeOffset? At);

    public sealed record Defaults(int Needed, int Weight = 3, bool Flag = false, [property: Required] int Forced = 1)
    {
        public bool Computed => Needed > 0;

        public long Settable { get; init; }
    }

    /// <summary>
    /// Review r2 M1: required = non-nullable value type, read back from the JSON (setter or parameter), parameter without default.
    /// </summary>
    [Fact]
    public void Defaulted_parameters_and_computed_members_are_optional()
    {
        var contract = GmBodyContract.For(typeof(Defaults));

        Property(contract, "needed").RequiresPresence.ShouldBeTrue();
        Property(contract, "weight").RequiresPresence.ShouldBeFalse();
        Property(contract, "flag").RequiresPresence.ShouldBeFalse();
        Property(contract, "computed").RequiresPresence.ShouldBeFalse();
        Property(contract, "computed").IsRequired.ShouldBeFalse();
        Property(contract, "settable").RequiresPresence.ShouldBeTrue();

        // An explicit [Required] on a defaulted value type still checks presence (research R3).
        Property(contract, "forced").RequiresPresence.ShouldBeTrue();
    }

    [Fact]
    public void Nested_objects_and_object_lists_are_walked_to_depth_three()
    {
        var contract = GmBodyContract.For(typeof(Body));

        var nested = Property(contract, "nested");
        nested.IsCollection.ShouldBeFalse();
        var inner = nested.Nested.ShouldNotBeNull();
        inner.Type.ShouldBe(typeof(Inner));
        Property(inner, "label").Validations.ShouldHaveSingleItem().ShouldBeOfType<StringLengthAttribute>();

        // Depth 3 (Body → Inner → Leaf): Leaf's properties are read, its own object properties are not walked.
        var leaf = Property(inner, "leaf").Nested.ShouldNotBeNull();
        Property(leaf, "deep").Validations.ShouldHaveSingleItem();
        Property(leaf, "again").Nested.ShouldBeNull();
        var leaves = Property(inner, "leaves");
        leaves.IsCollection.ShouldBeTrue();
        leaves.Nested.ShouldNotBeNull().Type.ShouldBe(typeof(Leaf));

        var items = Property(contract, "items");
        items.IsCollection.ShouldBeTrue();
        items.Nested.ShouldNotBeNull().Type.ShouldBe(typeof(Inner));
    }

    [Theory]
    [InlineData("map")]
    [InlineData("free")]
    [InlineData("choice")]
    [InlineData("at")]
    [InlineData("picks")]
    public void Dictionaries_json_nodes_enums_dates_and_scalar_lists_are_not_walked(string name)
    {
        var property = Property(GmBodyContract.For(typeof(Body)), name);

        property.Nested.ShouldBeNull();
        property.IsCollection.ShouldBeFalse();
    }

    [Fact]
    public void The_contract_is_cached_per_type()
    {
        GmBodyContract.For(typeof(Body)).ShouldBeSameAs(GmBodyContract.For(typeof(Body)));
    }

    private static GmBodyProperty Property(GmBodyContract contract, string jsonName) =>
        contract.Properties.Single(p => p.JsonName == jsonName);
}
