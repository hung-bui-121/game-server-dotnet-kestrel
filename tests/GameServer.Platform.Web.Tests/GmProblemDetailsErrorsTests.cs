using System.Net;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Errors;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T007 (FR-026, FR-022, BC-7): <see cref="GameServerException.Errors"/> becomes the Problem Details member
/// <c>errors</c> (keys in ordinal order, after every existing member) and is ignored by the Protobuf error envelope.
/// </summary>
public sealed class GmProblemDetailsErrorsTests
{
    private static readonly RouteDeclaration JsonWithErrors = RouteDeclaration.Get("test.json_errors", "/v1/test/json-errors", SecurityProfile.Public, RouteSurface.Host);
    private static readonly RouteDeclaration JsonWithout = RouteDeclaration.Get("test.json_plain", "/v1/test/json-plain", SecurityProfile.Public, RouteSurface.Host);
    private static readonly RouteDeclaration ProtoWithErrors = RouteDeclaration.Post("test.proto_errors", "/api/v1/test/proto-errors", SecurityProfile.Player);
    private static readonly RouteDeclaration ProtoWithout = RouteDeclaration.Post("test.proto_plain", "/api/v1/test/proto-plain", SecurityProfile.Player);

    private static readonly Dictionary<string, IReadOnlyList<string>> FieldErrors = new(StringComparer.Ordinal)
    {
        ["level"] = ["The field level must be between 1 and 2147483647."],
        ["items[1].quantity"] = ["first", "second"],
        ["Reason"] = ["reason is required."],
    };

    [Fact]
    public async Task A_json_endpoint_writes_errors_after_the_existing_members()
    {
        await using var host = await StartAsync();

        var response = await host.Client().GetAsync(JsonWithErrors.Template);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Select(p => p.Key).ShouldBe(["type", "title", "status", "code", "codeNumber", "requestId", "retryable", "details", "errors"]);
        body["code"]!.GetValue<string>().ShouldBe("BAD_REQUEST");
        body["details"]!.ToJsonString().ShouldBe("""{"hint":"x"}""");
        var errors = body["errors"]!.AsObject();
        errors.Select(p => p.Key).ShouldBe(["Reason", "items[1].quantity", "level"]);
        errors["items[1].quantity"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe(["first", "second"]);
        errors["level"]!.AsArray().Single()!.GetValue<string>().ShouldBe("The field level must be between 1 and 2147483647.");
    }

    [Fact]
    public async Task Without_errors_the_problem_has_no_errors_member()
    {
        await using var host = await StartAsync();

        var response = await host.Client().GetAsync(JsonWithout.Template);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Select(p => p.Key).ShouldBe(["type", "title", "status", "code", "codeNumber", "requestId", "retryable"]);
    }

    [Fact]
    public async Task A_protobuf_endpoint_ignores_errors()
    {
        await using var host = await StartAsync();
        var client = host.PlayerClient("p1");

        var withErrors = await (await client.PostAsync(ProtoWithErrors.Template, null)).ReadErrorAsync();
        var without = await (await client.PostAsync(ProtoWithout.Template, null)).ReadErrorAsync();

        withErrors.Code.ShouldBe("BAD_REQUEST");
        withErrors.RequestId = string.Empty;
        without.RequestId = string.Empty;
        withErrors.ToByteArray().ShouldBe(without.ToByteArray());
    }

    private static Task<TestHost> StartAsync() =>
        SampleTestHost.StartAsync(
            modules: SampleTestHost.WithRoutes(JsonWithErrors, JsonWithout, ProtoWithErrors, ProtoWithout),
            settings: s =>
            {
                s["GameServer:PublicEndpointAllowlist:0"] = "/v1/time";
                s["GameServer:PublicEndpointAllowlist:1"] = JsonWithErrors.Template;
                s["GameServer:PublicEndpointAllowlist:2"] = JsonWithout.Template;
            },
            beforeUseGameServer: app =>
            {
                app.MapDeclared(JsonWithErrors, (Func<IResult>)(() => throw WithErrors())).AsPublicEndpoint(protobuf: false);
                app.MapDeclared(JsonWithout, (Func<IResult>)(() => throw new GameServerException(CoreErrorCodes.BadRequest, "plain"))).AsPublicEndpoint(protobuf: false);
                app.MapDeclared(ProtoWithErrors, (Func<IResult>)(() => throw WithErrors())).AsPlayerEndpoint();
                app.MapDeclared(ProtoWithout, (Func<IResult>)(() => throw new GameServerException(CoreErrorCodes.BadRequest, "plain")
                {
                    Details = new Dictionary<string, string> { ["hint"] = "x" },
                })).AsPlayerEndpoint();
            });

    private static GameServerException WithErrors() =>
        new(CoreErrorCodes.BadRequest, "Body validation failed: level.")
        {
            Details = new Dictionary<string, string> { ["hint"] = "x" },
            Errors = FieldErrors,
        };
}
