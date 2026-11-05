using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fixture.Game;
using GameServer.Platform.Web.Auth;
using GameServer.TestKit;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// Slice 017 (FR-054, SC-006, CI-01) as a game does it: the committed GM document of the game equals what its real host serves
/// (one kit call over <see cref="LocalFlavorFactory{TEntryPoint}"/>), a changed bound is reported at its line, and the game's own
/// GM command enforces its annotated body. <c>UPDATE_OPENAPI=1</c> rewrites <c>docs/api/gm-openapi.json</c> after a reviewed change.
/// </summary>
public sealed class FixtureGmOpenApiTests
{
    private static string FixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Fixture.Host")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Fixture root (src/Fixture.Host) not found above " + AppContext.BaseDirectory);
    }

    private static string Committed => Path.Combine(FixtureRoot(), "docs", "api", "gm-openapi.json");

    [Fact]
    public async Task The_committed_gm_document_is_what_the_game_host_serves()
    {
        await using var factory = new LocalFlavorFactory<Program>();
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await GmOpenApiSnapshot.WriteAsync(factory.Services, Committed);
        }

        (await GmOpenApiSnapshot.DiffAsync(factory.Services, Committed)).ShouldBeEmpty();
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Committed))!;
        document["paths"]![FixtureRoutes.NoteSetTemplate]!["post"]!["operationId"]!.GetValue<string>().ShouldBe(SetNoteHandler.Operation);
        var text = document["components"]!["schemas"]!["SetNoteBody"]!["properties"]!["text"]!;
        text["maxLength"]!.GetValue<int>().ShouldBe(SetNoteHandler.MaxTextLength);
        document["components"]!["schemas"]!["SetNoteBody"]!["properties"]!["preset"]!["x-gm-options"]!.GetValue<string>().ShouldBe("fixture.note_presets");
    }

    [Fact]
    public async Task A_changed_bound_in_the_committed_copy_is_reported_at_its_line()
    {
        await using var factory = new LocalFlavorFactory<Program>();
        var lines = (await GmOpenApiSnapshot.GetDocumentAsync(factory.Services)).Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains($"\"maxLength\": {SetNoteHandler.MaxTextLength},", StringComparison.Ordinal));
        index.ShouldBeGreaterThan(0);
        var copy = (string[])lines.Clone();
        copy[index] = copy[index].Replace($"{SetNoteHandler.MaxTextLength}", $"{SetNoteHandler.MaxTextLength + 1}", StringComparison.Ordinal);
        var folder = Directory.CreateTempSubdirectory("fixture-gm-snapshot-").FullName;
        try
        {
            var path = Path.Combine(folder, "gm-openapi.json");
            await File.WriteAllTextAsync(path, string.Join('\n', copy));

            var message = await GmOpenApiSnapshot.DiffAsync(factory.Services, path);

            message.ShouldStartWith($"GM OpenAPI document differs from {path} at line {index + 1}:\n");
            message.ShouldContain($"  committed: {copy[index]}\n");
            message.ShouldContain($"  host:      {lines[index]}\n");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_game_gm_command_enforces_its_annotated_body()
    {
        await using var factory = new LocalFlavorFactory<Program>();
        var login = await factory.CreateClient().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var gm = factory.CreateClient();
        gm.DefaultRequestHeaders.Add(TestAuthenticationHandler.GmHeader, "fixture-gm");
        gm.DefaultRequestHeaders.Add(TestAuthenticationHandler.GmPermissionsHeader, FixturePermissions.NoteEdit);
        var route = FixtureRoutes.NoteSetTemplate.Replace("{playerId}", login.PlayerId, StringComparison.Ordinal);

        var tooLong = await PostAsync(gm, route, new { text = new string('n', SetNoteHandler.MaxTextLength + 1), reason = "too long" });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonNode.Parse(await tooLong.Content.ReadAsStringAsync())!;
        problem["errors"]!.AsObject().Select(e => e.Key).ShouldBe(["text"]);

        var ok = await PostAsync(gm, route, new { text = "Welcome back", preset = "welcome", reason = "support ticket 42" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var answer = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!;
        answer["code"]!.GetValue<string>().ShouldBe("OK");
        answer["receiptId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        var presets = JsonNode.Parse(await gm.GetStringAsync(FixtureRoutes.NotePresetsTemplate))!;
        presets.ToJsonString().ShouldBe("""{"items":[{"value":"apology","label":"Apology for an incident"},{"value":"welcome","label":"Welcome back"}]}""");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        return client.SendAsync(request);
    }
}
