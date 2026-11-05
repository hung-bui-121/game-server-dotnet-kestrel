using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T018 (FR-020..FR-023, D6, D7, A5): the validator applies every supported DataAnnotation on both targets at its
/// boundary, checks JSON presence for <c>[Required]</c> value types, walks nested objects and lists with JSON paths, and merges
/// the reason rule of a command body.
/// </summary>
public sealed class GmBodyValidatorTests
{
    public sealed record Line([property: Range(1, 99)] int Quantity, [Required] string? Sku);

    public sealed record Box([property: StringLength(4)] string? Label, Line? Line);

    public sealed record Probe(
        [property: Range(1, 10)] int PInt = 5,
        [Range(1, 10)] int AInt = 5,
        [property: Range(2, 20)] int? PIntN = null,
        [Range(0, long.MaxValue)] long ALong = 0,
        [property: Range(0.5, 9.5)] double PDouble = 1,
        [property: StringLength(10, MinimumLength = 2)] string? PStrLen = null,
        [StringLength(10, MinimumLength = 2)] string? AStrLen = null,
        [MinLength(3)] string? AMin = null,
        [property: MaxLength(7)] string? PMax = null,
        [Length(1, 4)] List<string>? ALenList = null,
        [property: RegularExpression("^[a-z]+$")] string? PRegex = null,
        [property: Required] string? PReq = "x",
        [Required] string? AReq = "x",
        [property: Required] long Delta = 0,
        Box? Box = null,
        List<Line>? Lines = null,
        string? Reason = "because") : IGmCommandBody;

    public static TheoryData<string, Probe, Probe> Boundaries() => new()
    {
        { "pInt", new Probe(PInt: 10), new Probe(PInt: 11) },
        { "aInt", new Probe(AInt: 1), new Probe(AInt: 0) },
        { "pIntN", new Probe(PIntN: 20), new Probe(PIntN: 21) },
        { "aLong", new Probe(ALong: 0), new Probe(ALong: -1) },
        { "pDouble", new Probe(PDouble: 9.5), new Probe(PDouble: 9.6) },
        { "pStrLen", new Probe(PStrLen: "ab"), new Probe(PStrLen: "a") },
        { "aStrLen", new Probe(AStrLen: new string('x', 10)), new Probe(AStrLen: new string('x', 11)) },
        { "aMin", new Probe(AMin: "abc"), new Probe(AMin: "ab") },
        { "pMax", new Probe(PMax: "1234567"), new Probe(PMax: "12345678") },
        { "aLenList", new Probe(ALenList: ["a", "b", "c", "d"]), new Probe(ALenList: ["a", "b", "c", "d", "e"]) },
        { "pRegex", new Probe(PRegex: "abc"), new Probe(PRegex: "abc1") },
        { "pReq", new Probe(PReq: "y"), new Probe(PReq: "") },
        { "aReq", new Probe(AReq: "y"), new Probe(AReq: "  ") },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void The_boundary_value_passes_and_the_next_one_fails_under_its_json_name(string key, Probe accepted, Probe refused)
    {
        Validate(accepted).ShouldBeEmpty();

        var errors = Validate(refused);

        errors.Keys.ShouldBe([key]);
        errors[key].ShouldHaveSingleItem().ShouldContain(key);
    }

    /// <summary>T063 (review m6): the shared "valid" answer cannot be changed by a caller that casts it.</summary>
    [Fact]
    public void The_valid_answer_is_immutable()
    {
        var valid = Validate(new Probe());

        valid.ShouldBeEmpty();
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, IReadOnlyList<string>>)valid).Add("x", ["y"]));
        Validate(new Probe()).ShouldBeEmpty();
    }

    [Fact]
    public void Range_on_null_passes_and_required_refuses_null()
    {
        Validate(new Probe(PIntN: null)).ShouldBeEmpty();

        Validate(new Probe(PReq: null))["pReq"].ShouldBe(["The pReq field is required."]);
    }

    [Fact]
    public void An_absent_required_value_type_is_missing_and_a_present_zero_is_accepted()
    {
        var withDelta = Json(new Probe());
        var withoutDelta = withDelta.Replace("\"delta\":0,", string.Empty, StringComparison.Ordinal);
        withoutDelta.ShouldNotContain("delta");

        Validate(new Probe(), withDelta).ShouldBeEmpty();
        Validate(new Probe(), withoutDelta)["delta"].ShouldBe(["The delta field is required."]);
        Validate(new Probe(), withoutDelta.Replace("{", "{\"DELTA\":0,", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void Nested_objects_and_list_items_are_reported_by_json_path()
    {
        var body = new Probe(
            Box: new Box("12345", new Line(0, "s")),
            Lines: [new Line(1, "a"), new Line(100, null)]);

        var errors = Validate(body);

        errors.Keys.ShouldBe(["box.label", "box.line.quantity", "lines[1].quantity", "lines[1].sku"]);
        errors["lines[1].sku"].ShouldBe(["The sku field is required."]);
    }

    [Fact]
    public void Several_failures_and_the_reason_rule_are_one_answer_in_ordinal_order()
    {
        var errors = Validate(new Probe(PInt: 0, AMin: "x", Reason: " "));

        errors.Keys.ShouldBe(["aMin", "pInt", "reason"]);
        errors["reason"].ShouldBe([GmBodyValidator.ReasonMessage]);
        errors.Values.SelectMany(m => m).ShouldAllBe(m => !m.Contains("Exception", StringComparison.Ordinal) && !m.Contains(" at ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_command_body_needs_a_reason(string? reason) =>
        Validate(new Probe(Reason: reason)).Keys.ShouldBe(["reason"]);

    [Fact]
    public void The_reason_holds_up_to_500_characters_and_is_not_checked_outside_commands()
    {
        Validate(new Probe(Reason: new string('r', 500))).ShouldBeEmpty();
        Validate(new Probe(Reason: new string('r', 501)))["reason"].ShouldBe([GmBodyValidator.ReasonMessage]);
        GmBodyValidator.Validate(new Probe(Reason: null), Encoding.UTF8.GetBytes(Json(new Probe())), commandBody: false).ShouldBeEmpty();
    }

    public sealed record AnnotatedAction([property: StringLength(5)] string? Note, string? Reason);

    public sealed record PlainAction(string? Note, string? Reason);

    private static readonly RouteDeclaration Annotated = RouteDeclaration.Post("probe.annotated", "/gm/v1/probe/annotated", SecurityProfile.Gm, RouteSurface.GmApi, "counter.read");
    private static readonly RouteDeclaration Plain = RouteDeclaration.Post("probe.plain", "/gm/v1/probe/plain", SecurityProfile.Gm, RouteSurface.GmApi, "counter.read");

    /// <summary>T020 (FR-024, N1): GmHttp.ReadJsonAsync validates annotations (no reason rule); an unannotated body reads as before.</summary>
    [Fact]
    public async Task Read_json_validates_annotated_bodies_only()
    {
        await using var host = await SampleTestHost.StartAsync(
            SampleTestHost.WithRoutes(Annotated, Plain),
            beforeUseGameServer: app =>
            {
                app.MapDeclared(Annotated, async (HttpContext http) => GmHttp.Json(await GmHttp.ReadJsonAsync<AnnotatedAction>(http))).AsGmEndpoint("counter.read");
                app.MapDeclared(Plain, async (HttpContext http) => GmHttp.Json(await GmHttp.ReadJsonAsync<PlainAction>(http))).AsGmEndpoint("counter.read");
            });
        var client = host.GmClient("support", "counter.read");

        var refused = await client.PostAsJsonAsync(Annotated.Template, new { note = "123456" });
        var accepted = await client.PostAsJsonAsync(Annotated.Template, new { note = "12345" });
        var plain = await client.PostAsJsonAsync(Plain.Template, new { note = "123456" });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        problem["code"]!.GetValue<string>().ShouldBe("BAD_REQUEST");
        problem["errors"]!.ToJsonString().ShouldBe("""{"note":["The field note must be a string with a maximum length of 5."]}""");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        plain.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await plain.Content.ReadAsStringAsync()).ShouldBe("""{"note":"123456","reason":null}""");
    }

    private static string Json(Probe body) => JsonSerializer.Serialize(body, GameServerJson.Options);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(Probe body, string? json = null) =>
        GmBodyValidator.Validate(body, Encoding.UTF8.GetBytes(json ?? Json(body)), commandBody: true);
}
