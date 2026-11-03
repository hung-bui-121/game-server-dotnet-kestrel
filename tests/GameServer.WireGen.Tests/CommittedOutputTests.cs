using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>
/// FR-034, FR-035, US5-1 (slice 014 T063): the committed generated files are exactly what the committed configs produce from the
/// current server (every framework build runs this, so the Unity package cannot drift from the catalog). The host export runs for
/// real (<c>--no-build</c>, the test's configuration: the solution build comes first).
/// </summary>
public sealed class CommittedOutputTests
{
    private const string SdkConfig = "sdk/GameServer.Client/wiregen.json";
    private const string FixtureConfig = "tests/ConsumerFixture/tools/wiregen.json";

    private static readonly string Dotnet = DotnetHost.Resolve(null);

    [Theory]
    [InlineData(SdkConfig)]
    [InlineData(FixtureConfig)]
    public async Task Verify_of_a_committed_config_passes(string config)
    {
        var (code, output, error) = await VerifyAsync(Repo.Path(config));

        code.ShouldBe(WireGenExitCodes.Ok, output + error);
        error.ShouldNotContain("warning", Case.Insensitive);
    }

    [Fact]
    public async Task An_edited_generated_file_of_the_package_fails_verify_naming_it()
    {
        using var tree = new TempTree();
        var package = Repo.Path("sdk/unity/com.gameserver.client");
        File.Copy(Path.Combine(package, "package.json"), tree.Path("package.json"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(package, "Runtime", "Generated"), "*.cs", SearchOption.AllDirectories))
        {
            var target = tree.Path(Path.GetRelativePath(package, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.AppendAllText(tree.Path("Runtime/Generated/FrameworkRoutes.cs"), "// edited\n");
        var before = tree.Snapshot();

        var (code, output, error) = await VerifyAsync(Repo.Path(SdkConfig), "--output-root", tree.Root);

        code.ShouldBe(WireGenExitCodes.OutOfDate, output + error);
        (output + error).ShouldContain("Runtime/Generated/FrameworkRoutes.cs");
        (output + error).ShouldNotContain("Runtime/Generated/FrameworkSections.cs");
        tree.Snapshot().ShouldBe(before);
    }

    private static async Task<(int Code, string Output, string Error)> VerifyAsync(string config, params string[] extra)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["verify", "--config", config, "--no-build", "--configuration", Repo.Configuration, "--dotnet", Dotnet, .. extra];
        var code = await WireGenCli.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
