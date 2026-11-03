using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T043 (FR-022, US2-4, research R12, V10): mismatched framework, protobuf and protoc versions fail naming both.</summary>
public sealed class VersionGuardTests
{
    private static readonly ToolInfo Tool = new("root", "grpc", "0.7.0", "2.84.0", "3.36.2");

    [Fact]
    public void Matching_versions_pass()
    {
        VersionGuard.CheckExport(Contract("0.7.0", "3.36.2"), Tool).ShouldBeEmpty();
        VersionGuard.CheckPackageVersions(Msbuild(("Grpc.Tools", "2.84.0"), ("Google.Protobuf", "3.36.2"), ("Shouldly", "4.3.0")), "host.csproj", Tool, _ => { })
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_export_of_another_framework_version_names_both()
    {
        VersionGuard.CheckExport(Contract("0.6.0", "3.36.2"), Tool).ShouldHaveSingleItem().ShouldBe(
            "[version] catalog exported by framework 0.6.0, generator is 0.7.0. Fix: build the host against the same framework checkout as the generator (FR-022).");
    }

    [Fact]
    public void A_host_with_another_protobuf_runtime_names_both()
    {
        VersionGuard.CheckExport(Contract("0.7.0", "3.37.0"), Tool).ShouldHaveSingleItem().ShouldBe(
            "[version] the host resolves Google.Protobuf 3.37.0, the Unity package ships 3.36.2. Fix: use the framework's pinned Google.Protobuf 3.36.2 in " +
            "the host (remove its own version or VersionOverride) (FR-022).");
    }

    [Fact]
    public void Central_package_versions_of_the_host_are_compared()
    {
        var errors = VersionGuard.CheckPackageVersions(Msbuild(("Grpc.Tools", "2.70.0"), ("Google.Protobuf", "3.37.0")), "host.csproj", Tool, _ => { });

        errors.ShouldBe(
        [
            "[version] host.csproj resolves Grpc.Tools 2.70.0, the framework pins 2.84.0. Fix: use the framework's Directory.Packages.props version " +
            "(remove the game's own PackageVersion of Grpc.Tools) (FR-022).",
            "[version] host.csproj resolves Google.Protobuf 3.37.0, the framework pins 3.36.2. Fix: use the framework's Directory.Packages.props version " +
            "(remove the game's own PackageVersion of Google.Protobuf) (FR-022).",
        ]);
    }

    [Fact]
    public void Without_central_package_management_a_note_is_printed_and_nothing_fails()
    {
        var notes = new List<string>();

        VersionGuard.CheckPackageVersions("""{ "Items": { "PackageVersion": [] } }""", "host.csproj", Tool, notes.Add).ShouldBeEmpty();

        notes.ShouldHaveSingleItem().ShouldBe(
            "note: host.csproj has no central package versions: its Grpc.Tools version is not checked (Google.Protobuf is checked through the export).");
    }

    [Fact]
    public void Unreadable_msbuild_output_is_an_error()
    {
        Should.Throw<WireGenException>(() => VersionGuard.CheckPackageVersions("not json", "host.csproj", Tool, _ => { }))
            .Message.ShouldStartWith("[version] the package versions of host.csproj cannot be read");
    }

    [Fact]
    public async Task The_fixture_host_resolves_the_pinned_versions()
    {
        var notes = new List<string>();

        var errors = await VersionGuard.CheckHostPackagesAsync(
            DotnetHost.Resolve(null), Repo.Path("tests/ConsumerFixture/src/Fixture.Host/Fixture.Host.csproj"), Repo.Tool, notes.Add);

        errors.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }

    private static WireContract Contract(string framework, string protobuf) => new(framework, protobuf, [], []);

    /// <summary>The JSON shape of <c>dotnet msbuild -getItem:PackageVersion</c> (V10).</summary>
    private static string Msbuild(params (string Id, string Version)[] items) =>
        "{ \"Items\": { \"PackageVersion\": [" +
        string.Join(", ", items.Select(i => $"{{ \"Identity\": \"{i.Id}\", \"Version\": \"{i.Version}\", \"DefiningProjectFullPath\": \"x\" }}")) +
        "] } }";
}
