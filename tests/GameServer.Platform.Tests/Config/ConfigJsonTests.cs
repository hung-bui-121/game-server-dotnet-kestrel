using System.Text.Json;
using GameServer.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>FR-011, FR-017 (slice 015 T011): the shared JSON readers and their exact problem texts (startup-report §4).</summary>
public sealed class ConfigJsonTests
{
    private static readonly ConfigLocation At = ConfigLocation.Row(3);

    [Theory]
    [InlineData("""{}""", "'maxQuantity' is missing")]
    [InlineData("""{"maxQuantity": null}""", "'maxQuantity' is null")]
    [InlineData("""{"maxQuantity": "999"}""", "'maxQuantity' must be an integer of at least 1, found \"999\"")]
    [InlineData("""{"maxQuantity": 1.0}""", "'maxQuantity' must be an integer of at least 1, found 1.0")]
    [InlineData("""{"maxQuantity": 1e3}""", "'maxQuantity' must be an integer of at least 1, found 1e3")]
    [InlineData("""{"maxQuantity": 0}""", "'maxQuantity' must be an integer of at least 1, found 0")]
    [InlineData("""{"maxQuantity": -1}""", "'maxQuantity' must be an integer of at least 1, found -1")]
    [InlineData("""{"maxQuantity": {"a": 1}}""", "'maxQuantity' must be an integer of at least 1, found object")]
    [InlineData("""{"maxQuantity": 99999999999999999999}""", "'maxQuantity' must be an integer of at least 1, found 99999999999999999999")]
    public void A_required_integer_reports_one_problem(string json, string problem)
    {
        var (value, problems) = Read(json, (o, p) => ConfigJson.RequiredInteger(o, "maxQuantity", At, p, min: 1, fix: "set the largest quantity a player may hold (ECO-07)"));

        value.ShouldBeNull();
        problems.Entries.Select(e => (e.At, e.Problem, e.Fix)).ShouldBe([(At, problem, "set the largest quantity a player may hold (ECO-07)")]);
    }

    [Theory]
    [InlineData("""{"x": 1}""", 1L)]
    [InlineData("""{"x": 168}""", 168L)]
    public void An_integer_in_range_is_read(string json, long expected)
    {
        var (value, problems) = Read(json, (o, p) => ConfigJson.RequiredInteger(o, "x", At, p, 1, 168));

        value.ShouldBe(expected);
        problems.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1L, long.MaxValue, "an integer of at least 1")]
    [InlineData(1L, 168L, "an integer from 1 to 168")]
    [InlineData(long.MinValue, 10L, "an integer of at most 10")]
    [InlineData(long.MinValue, long.MaxValue, "an integer")]
    public void Range_texts_and_the_default_fix_name_the_range(long min, long max, string type)
    {
        var (_, problems) = Read("""{"x": 169.5}""", (o, p) => ConfigJson.RequiredInteger(o, "x", At, p, min, max));

        problems.Entries.Single().Problem.ShouldBe($"'x' must be {type}, found 169.5");
        problems.Entries.Single().Fix.ShouldBe($"set 'x' to {type}");
    }

    [Fact]
    public void An_optional_integer_is_null_without_a_problem_when_absent_and_reported_when_wrong()
    {
        Read("""{}""", (o, p) => ConfigJson.OptionalInteger(o, "battleHours", At, p, 1, 168)).Problems.Entries.ShouldBeEmpty();
        Read("""{"battleHours": 24}""", (o, p) => ConfigJson.OptionalInteger(o, "battleHours", At, p, 1, 168)).Value.ShouldBe(24);

        var (value, problems) = Read("""{"battleHours": 169}""", (o, p) => ConfigJson.OptionalInteger(o, "battleHours", At, p, 1, 168));
        value.ShouldBeNull();
        problems.Entries.Single().Problem.ShouldBe("'battleHours' must be an integer from 1 to 168, found 169");
        Read("""{"battleHours": null}""", (o, p) => ConfigJson.OptionalInteger(o, "battleHours", At, p, 1, 168))
            .Problems.Entries.Single().Problem.ShouldBe("'battleHours' is null");
    }

    [Theory]
    [InlineData("""{}""", "'id' is missing")]
    [InlineData("""{"id": null}""", "'id' is null")]
    [InlineData("""{"id": 5}""", "'id' must be a string, found number")]
    [InlineData("""{"id": ""}""", "'id' must not be empty")]
    public void A_required_string_reports_one_problem(string json, string problem)
    {
        var (value, problems) = Read(json, (o, p) => ConfigJson.RequiredString(o, "id", At, p));

        value.ShouldBeNull();
        problems.Entries.Single().Problem.ShouldBe(problem);
        problems.Entries.Single().Fix.ShouldBe(problem.EndsWith("empty", StringComparison.Ordinal) ? "set 'id' to a non-empty string" : "set 'id' to a string");
    }

    [Fact]
    public void A_string_is_read_and_an_empty_one_may_be_allowed()
    {
        Read("""{"id": "gold"}""", (o, p) => ConfigJson.RequiredString(o, "id", At, p)).Value.ShouldBe("gold");
        Read("""{"id": ""}""", (o, p) => ConfigJson.RequiredString(o, "id", At, p, allowEmpty: true)).Value.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("""{}""", "'resources' is missing")]
    [InlineData("""{"resources": null}""", "'resources' is null")]
    [InlineData("""{"resources": {}}""", "'resources' must be an array, found object")]
    [InlineData("""{"resources": "x"}""", "'resources' must be an array, found string")]
    public void An_array_reports_one_problem(string json, string problem)
    {
        var problems = new ConfigProblems();
        using var document = JsonDocument.Parse(json);

        ConfigJson.TryGetArray(document.RootElement, "resources", ConfigLocation.WholeFile, problems, out _, "put the resource rows in a \"resources\" array (ECO-07)")
            .ShouldBeFalse();

        problems.Entries.Single().Problem.ShouldBe(problem);
        problems.Entries.Single().Fix.ShouldBe("put the resource rows in a \"resources\" array (ECO-07)");
    }

    [Theory]
    [InlineData("""{"products": []}""", "'products' must be an object, found array")]
    [InlineData("""{"products": true}""", "'products' must be an object, found boolean")]
    [InlineData("""[]""", "'products' is missing")]
    public void An_object_reports_one_problem(string json, string problem)
    {
        var problems = new ConfigProblems();
        using var document = JsonDocument.Parse(json);

        ConfigJson.TryGetObject(document.RootElement, "products", ConfigLocation.WholeFile, problems, out _).ShouldBeFalse();

        problems.Entries.Single().Problem.ShouldBe(problem);
        problems.Entries.Single().Fix.ShouldBe("set 'products' to an object");
    }

    [Fact]
    public void Present_values_are_returned_and_unknown_properties_are_never_reported()
    {
        var problems = new ConfigProblems();
        using var document = JsonDocument.Parse("""{"items": {"a": 1}, "rows": [1], "icon": "x.png", "extra": {"deep": null}}""");

        ConfigJson.TryGetObject(document.RootElement, "items", ConfigLocation.WholeFile, problems, out var items).ShouldBeTrue();
        ConfigJson.TryGetArray(document.RootElement, "rows", ConfigLocation.WholeFile, problems, out var rows).ShouldBeTrue();

        items.GetProperty("a").GetInt32().ShouldBe(1);
        rows.GetArrayLength().ShouldBe(1);
        problems.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""5""", "the row must be an object, found number")]
    [InlineData("""[]""", "the row must be an object, found array")]
    [InlineData("""null""", "the row must be an object, found null")]
    public void A_row_must_be_an_object(string json, string problem)
    {
        var problems = new ConfigProblems();
        using var document = JsonDocument.Parse(json);

        ConfigJson.RequireObject(document.RootElement, ConfigLocation.Row(2), problems, "write each resource as an object (ECO-07)").ShouldBeFalse();

        problems.Entries.Single().ShouldBe(new ConfigProblem(ConfigLocation.Row(2), problem, "write each resource as an object (ECO-07)", false));
        ConfigJson.RequireObject(JsonDocument.Parse("{}").RootElement, ConfigLocation.Row(1), problems).ShouldBeTrue();
    }

    [Fact]
    public void Paths_use_dots_for_identifiers_brackets_for_other_keys_and_indexes()
    {
        ConfigJson.PathOf("a", "b").ShouldBe("$.a.b");
        ConfigJson.PathOf("items", "key with.dot").ShouldBe("$.items['key with.dot']");
        ConfigJson.PathOf("rows", 2).ShouldBe("$.rows[2]");
        ConfigJson.PathOf("items", "it's").ShouldBe("$.items['it\\'s']");
        ConfigJson.PathOf("products", "google:gems_100", "grants", 0).ShouldBe("$.products['google:gems_100'].grants[0]");
        ConfigJson.PathOf().ShouldBe("$");
        Should.Throw<ArgumentException>(() => ConfigJson.PathOf(1.5));
    }

    private static (T Value, ConfigProblems Problems) Read<T>(string json, Func<JsonElement, ConfigProblems, T> read)
    {
        var problems = new ConfigProblems();
        using var document = JsonDocument.Parse(json);
        return (read(document.RootElement, problems), problems);
    }
}
