using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Gm.Tests.Infrastructure;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 017 (FR-020, FR-022, FR-023, FR-025, US2-2, US2-5): a GM command body is validated before <c>toDomain</c>, the target
/// validators and the runner; a refusal is the existing <c>400 BAD_REQUEST</c> plus <c>errors</c>, counted and logged like any
/// other.
/// </summary>
public sealed class GmCommandValidationTests
{
    private const string MiddlewareCategory = "GameServer.Platform.Web.Middleware.ExceptionMiddleware";

    internal const string ReasonMessage = "reason is required (1..500 characters, not only white space).";

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("too long")]
    public async Task A_bad_reason_is_refused_with_errors_before_anything_runs(string fault)
    {
        var reason = fault switch
        {
            "blank" => "  ",
            "too long" => new string('r', 501),
            _ => null,
        };
        var targets = new CountingTargetValidator();
        await using var host = await GmTestHost.StartAsync(services: s => s.AddSingleton<IGmTargetValidator>(targets));
        var player = await host.NewPlayerAsync();
        var gm = await host.GmClientAsync("admin");
        var receipts = host.Store.Count(ReceiptDocument.Collection);
        var problem = await gm.PostCommandAsync(UnbanRoute(player.PlayerId), new { reason }).ShouldBeAsync(400);

        problem["code"]!.GetValue<string>().ShouldBe("BAD_REQUEST");
        problem["errors"]!.ToJsonString().ShouldBe($$"""{"reason":["{{ReasonMessage}}"]}""");
        targets.Calls.ShouldBe(0);
        host.Store.Count(ReceiptDocument.Collection).ShouldBe(receipts);
        var line = host.LogEntries.Where(e => e.Category == MiddlewareCategory).ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldBe("Request rejected with BAD_REQUEST: Body validation failed: reason.");
    }

    [Fact]
    public async Task A_valid_unban_runs_as_before()
    {
        var targets = new CountingTargetValidator();
        await using var host = await GmTestHost.StartAsync(services: s => s.AddSingleton<IGmTargetValidator>(targets));
        var player = await host.NewPlayerAsync();
        var gm = await host.GmClientAsync("admin");

        var response = await gm.PostCommandAsync(UnbanRoute(player.PlayerId), new { reason = "appeal accepted" }).ShouldBeAsync(200);

        response["code"]!.GetValue<string>().ShouldBe("OK");
        targets.Calls.ShouldBe(1);
    }

    /// <summary>
    /// T022 (SC-002): for every keyword of the probe body in the document of the same host, the boundary value is accepted and the
    /// value one step outside is refused under that property's JSON path; every required member, nested ones included, is refused
    /// when absent; a pattern means the same for the document (checked with the regex) and the server, "" included on an optional
    /// field (review r1 M1, m3, m6).
    /// </summary>
    [Fact]
    public async Task Document_and_server_agree_on_every_probe_rule()
    {
        await using var host = await GmTestHost.StartAsync(extra: b => b.AddModule<ProbeGmModule>());
        var player = await host.NewPlayerAsync();
        var gm = host.GmClient("probe-staff", ProbeGmModule.Permission);
        var route = ProbeGmModule.Template.Replace("{playerId}", player.PlayerId, StringComparison.Ordinal);
        var schemas = (await GmDocumentShapeTests.DocumentAsync(host))["components"]!["schemas"]!;
        var body = schemas["ProbeGmBody"]!;

        var nestedSchema = schemas["ProbeNested"]!;
        var required = GmDocumentShapeTests.Strings(body["required"]);
        var nestedRequired = GmDocumentShapeTests.Strings(nestedSchema["required"]);
        nestedRequired.ShouldBe(["inner", "count"]);

        var cases = new List<(string Key, JsonObject Accepted, JsonObject? Refused)>();
        foreach (var (name, schema) in body["properties"]!.AsObject())
        {
            cases.AddRange(Cases(schema!, optional: !required.Contains(name))
                .Select(c => (name, With(name, c.Accepted), c.Refused is null ? null : With(name, c.Refused))));
        }

        foreach (var (name, schema) in nestedSchema["properties"]!.AsObject())
        {
            foreach (var (accepted, refused) in Cases(schema!, optional: !nestedRequired.Contains(name)))
            {
                cases.Add(($"nested.{name}", With("nested", Nested(name, accepted)), refused is null ? null : With("nested", Nested(name, refused))));
                cases.Add((
                    $"items[0].{name}",
                    With("items", new JsonArray(Nested(name, accepted))),
                    refused is null ? null : With("items", new JsonArray(Nested(name, refused)))));
            }
        }

        foreach (var name in required)
        {
            var without = ValidProbe();
            without.Remove(name);
            cases.Add((name, ValidProbe(), without));
        }

        foreach (var name in nestedRequired)
        {
            var nested = ValidNested();
            nested.Remove(name);
            cases.Add(($"nested.{name}", ValidProbe(), With("nested", nested.DeepClone())));
            cases.Add(($"items[0].{name}", ValidProbe(), With("items", new JsonArray(nested.DeepClone()))));
        }

        cases.Count.ShouldBeGreaterThanOrEqualTo(40);
        var disagreements = new List<string>();
        foreach (var (key, accepted, refused) in cases)
        {
            var ok = await gm.PostCommandAsync(route, accepted);
            if ((int)ok.StatusCode != 200)
            {
                disagreements.Add($"{key}: {accepted.ToJsonString()} answered {(int)ok.StatusCode} {await ok.Content.ReadAsStringAsync()}");
            }

            if (refused is null)
            {
                continue;
            }

            var no = await gm.PostCommandAsync(route, refused);
            var keys = (int)no.StatusCode == 400 ? (await no.JsonAsync())["errors"]?.AsObject().Select(e => e.Key).ToArray() : null;
            if (keys is null || !keys.SequenceEqual([key]))
            {
                disagreements.Add($"{key}: {refused.ToJsonString()} answered {(int)no.StatusCode} {await no.Content.ReadAsStringAsync()}");
            }
        }

        disagreements.ShouldBeEmpty();
    }

    /// <summary>T024 (FR-027, US2 independent test): the sample's annotated progression body is the reference example.</summary>
    [Fact]
    public async Task The_sample_progression_ranges_are_enforced_before_the_command()
    {
        await using var host = await GmTestHost.StartAsync();
        var player = await host.NewPlayerAsync();
        var gm = await host.GmClientAsync("admin");
        var route = SampleGame.Adapters.SampleGameModule.GmProgressionRoute.Replace("{playerId}", player.PlayerId, StringComparison.Ordinal);
        var receipts = host.Store.Count(ReceiptDocument.Collection);

        var refused = await gm.PostCommandAsync(route, new { level = 0, xp = -1, reason = "bad values" }).ShouldBeAsync(400);
        refused["errors"]!.AsObject().Select(e => e.Key).ShouldBe(["level", "xp"]);
        refused["errors"]!["level"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe("The field level must be between 1 and 2147483647.");
        host.Store.Count(ReceiptDocument.Collection).ShouldBe(receipts);

        // The cross-field rule stays in toDomain: 400 without errors, as before.
        var empty = await gm.PostCommandAsync(route, new { reason = "nothing to set" }).ShouldBeAsync(400);
        empty["code"]!.GetValue<string>().ShouldBe("BAD_REQUEST");
        empty.AsObject().ContainsKey("errors").ShouldBeFalse();

        var accepted = await gm.PostCommandAsync(route, new { stage = 1, reason = "reset stage" }).ShouldBeAsync(200);
        accepted["code"]!.GetValue<string>().ShouldBe("OK");
        host.Store.Count(ReceiptDocument.Collection).ShouldBe(receipts + 1);
    }

    /// <summary>T025 (FR-022, BC-2): the validation answer's member set; other BAD_REQUESTs keep their shape (no errors).</summary>
    [Fact]
    public async Task Only_body_rule_failures_carry_errors()
    {
        await using var host = await GmTestHost.StartAsync();
        var player = await host.NewPlayerAsync();
        var gm = await host.GmClientAsync("admin");
        var route = UnbanRoute(player.PlayerId);

        var validation = await gm.PostCommandAsync(route, new { reason = "" }).ShouldBeAsync(400);
        validation.AsObject().Select(m => m.Key).ShouldBe(["type", "title", "status", "code", "codeNumber", "requestId", "retryable", "errors"]);

        var badJson = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") };
        badJson.Headers.Add(CorrelationMiddleware.HeaderName, Guid.NewGuid().ToString("D"));
        var noJson = await gm.SendAsync(badJson).ShouldBeAsync(400);
        noJson["code"]!.GetValue<string>().ShouldBe("BAD_REQUEST");
        noJson.AsObject().ContainsKey("errors").ShouldBeFalse();

        var noRequestId = await gm.PostAsync(route, System.Net.Http.Json.JsonContent.Create(new { reason = "fine" })).ShouldBeAsync(400);
        noRequestId.AsObject().Select(m => m.Key).ShouldBe(["type", "title", "status", "code", "codeNumber", "requestId", "retryable"]);
    }

    /// <summary>
    /// T063 (review M1): routing answers a Content-Type that <c>Accepts</c> does not list with its own 415 endpoint, which has no
    /// security profile; it must stay 415 UNSUPPORTED_MEDIA_TYPE (as on main), never 403.
    /// </summary>
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData(null)]
    public async Task A_wrong_content_type_is_415_on_commands_and_actions(string? contentType)
    {
        await using var host = await GmTestHost.StartAsync();
        var player = await host.NewPlayerAsync();
        var gm = await host.GmClientAsync("admin");

        foreach (var route in new[] { UnbanRoute(player.PlayerId), "/gm/v1/staff/support/disable" })
        {
            var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("""{"reason":"x"}"""));
            if (contentType is not null)
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }

            var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
            request.Headers.Add(CorrelationMiddleware.HeaderName, Guid.NewGuid().ToString("D"));

            var problem = await gm.SendAsync(request).ShouldBeAsync(415);

            problem["code"]!.GetValue<string>().ShouldBe("UNSUPPORTED_MEDIA_TYPE", route);
        }
    }

    /// <summary>Review r2 M1: a defaulted parameter and a computed member may be absent from the JSON.</summary>
    [Fact]
    public async Task A_defaulted_parameter_and_a_computed_member_are_optional_over_http()
    {
        await using var host = await GmTestHost.StartAsync(extra: b => b.AddModule<ProbeGmModule>());
        var player = await host.NewPlayerAsync();
        var gm = host.GmClient("probe-staff", ProbeGmModule.Permission);
        var route = ProbeGmModule.Template.Replace("{playerId}", player.PlayerId, StringComparison.Ordinal);
        var body = ValidProbe();
        body.ContainsKey("weight").ShouldBeFalse();
        body.ContainsKey("hasRange").ShouldBeFalse();

        var answer = await gm.PostCommandAsync(route, body).ShouldBeAsync(200);

        answer["code"]!.GetValue<string>().ShouldBe("OK");
    }

    /// <summary>A valid probe body: the required members only.</summary>
    private static JsonObject ValidProbe() => new()
    {
        ["pRequired"] = "x",
        ["aRequired"] = "x",
        ["pRequiredNumber"] = 0,
        ["reason"] = "matrix",
    };

    private static JsonObject With(string name, JsonNode value)
    {
        var body = ValidProbe();
        body[name] = value;
        return body;
    }

    /// <summary>A nested record valid except for <paramref name="name"/>.</summary>
    /// <summary>A valid nested record: its required members only.</summary>
    private static JsonObject ValidNested() => new() { ["inner"] = 1, ["count"] = 1 };

    /// <summary>A nested record valid except for <paramref name="name"/>.</summary>
    private static JsonObject Nested(string name, JsonNode value)
    {
        var nested = ValidNested();
        nested[name] = value.DeepClone();
        return nested;
    }

    /// <summary>(boundary value, one step outside or null when only the acceptance is checked) for each keyword of a property schema.</summary>
    private static IEnumerable<(JsonNode Accepted, JsonNode? Refused)> Cases(JsonNode schema, bool optional)
    {
        if (schema["minimum"] is { } minimum)
        {
            var value = minimum.GetValue<long>();
            yield return (value, value - 1);
        }

        if (schema["maximum"] is { } maximum)
        {
            var value = maximum.GetValue<long>();
            yield return (value, value + 1);
        }

        if (schema["minLength"] is { } minLength)
        {
            var value = minLength.GetValue<int>();
            yield return (new string('a', value), new string('a', value - 1));
        }

        if (schema["maxLength"] is { } maxLength)
        {
            var value = maxLength.GetValue<int>();
            yield return (new string('a', value), new string('a', value + 1));
        }

        if (schema["minItems"] is { } minItems)
        {
            var value = minItems.GetValue<int>();
            yield return (Items(schema, value), Items(schema, value - 1));
        }

        if (schema["maxItems"] is { } maxItems)
        {
            var value = maxItems.GetValue<int>();
            yield return (Items(schema, value), Items(schema, value + 1));
        }

        // [RegularExpression] (review r1 M1): the document's pattern accepts and refuses what the server does, "" included.
        if (schema["pattern"]?.GetValue<string>() is { } pattern && pattern.StartsWith("^(?:", StringComparison.Ordinal))
        {
            Regex.IsMatch("abc", pattern).ShouldBeTrue(pattern);
            Regex.IsMatch("abc1", pattern).ShouldBeFalse(pattern);
            Regex.IsMatch("1abc", pattern).ShouldBeFalse(pattern);
            Regex.IsMatch(string.Empty, pattern).ShouldBeTrue(pattern);
            yield return ("abc", "abc1");
            yield return ("abc", "1abc");
            if (optional)
            {
                yield return (string.Empty, null);
            }
        }

        // Required strings and the reason (review M3): white space only is refused.
        if (schema["pattern"]?.GetValue<string>() == "\\S")
        {
            yield return ("abc", "   ");
        }
    }

    /// <summary>A list of <paramref name="count"/> valid items: strings, or valid nested records for a list of objects.</summary>
    private static JsonArray Items(JsonNode schema, int count) =>
        new(Enumerable.Range(0, count)
            .Select(_ => schema["items"]?["type"]?.GetValue<string>() == "string" ? (JsonNode?)"a" : ValidNested())
            .ToArray());

    internal static string UnbanRoute(string playerId) => IdentityModule.UnbanRoute.Replace("{playerId}", playerId, StringComparison.Ordinal);

    /// <summary>Counts target checks; accepts every player.</summary>
    internal sealed class CountingTargetValidator : IGmTargetValidator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task ValidateAsync(string playerId, string operation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

}

/// <summary>
/// FR-025: a validation refusal counts once in <c>http_errors_total{code="BAD_REQUEST"}</c>. Not parallel: the platform meter is
/// shared by name between the hosts of parallel test classes.
/// </summary>
[Collection(GmValidationMetricTests.Name)]
public sealed class GmValidationMetricTests
{
    public const string Name = "gm-validation-metric";

    [Fact]
    public async Task A_validation_refusal_counts_one_bad_request()
    {
        await using var host = await GmTestHost.StartAsync();
        var player = await host.NewPlayerAsync();
        var gm = await host.GmClientAsync("admin");
        using var errors = new HttpErrorCounter();

        await gm.PostCommandAsync(GmCommandValidationTests.UnbanRoute(player.PlayerId), new { reason = " " }).ShouldBeAsync(400);

        errors.Count("BAD_REQUEST").ShouldBe(1);
    }

    /// <summary><c>http_errors_total</c> of the platform meter, by code.</summary>
    private sealed class HttpErrorCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<string> _codes = new();

        public HttpErrorCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName && instrument.Name == "http_errors_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "code")
                    {
                        _codes.Enqueue((string)tag.Value!);
                    }
                }
            });
            _listener.Start();
        }

        public int Count(string code) => _codes.Count(c => c == code);

        public void Dispose() => _listener.Dispose();
    }
}

[CollectionDefinition(GmValidationMetricTests.Name, DisableParallelization = true)]
public sealed class GmValidationMetricCollection;
