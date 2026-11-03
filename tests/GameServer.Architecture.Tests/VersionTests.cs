using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Platform;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 013 T065 (FR-050, FR-051, US6-1, US6-2, SC-006): one declared framework version, equal to the Unity package version,
/// stamped into every framework, sample and template assembly (assembly, file and informational version, the latter with the
/// git sha when built from a checkout).
/// </summary>
public sealed partial class VersionTests
{
    private const string PackageJson = "sdk/unity/com.gameserver.client/package.json";

    private static readonly string[] StampedAssemblyPatterns = ["GameServer.*.dll", "Sample*.dll", "Template.*.dll"];

    [Fact]
    public void The_declared_version_equals_the_unity_package_version()
    {
        var declared = RepositoryTooling.DeclaredFrameworkVersion();
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryTooling.Root, PackageJson)));

        Mismatch(declared, package.RootElement.GetProperty("version").GetString()).ShouldBeNull();
        SemVer().IsMatch(declared).ShouldBeTrue($"GameServerFrameworkVersion '{declared}' is not MAJOR.MINOR.PATCH");
    }

    [Fact]
    public void A_version_mismatch_names_both_values_and_the_fix()
    {
        Mismatch("0.7.0", "0.7.1").ShouldBe(
            "Framework version mismatch: Directory.Build.props GameServerFrameworkVersion is '0.7.0', " +
            $"{PackageJson} version is '0.7.1'. Fix: bump both together; the release tag is v{{version}} (FR-051).");
        Mismatch("0.7.0", "0.7.0").ShouldBeNull();
    }

    [Fact]
    public void Every_framework_sample_and_template_assembly_carries_the_declared_version()
    {
        var declared = RepositoryTooling.DeclaredFrameworkVersion();
        var gitCheckout = Directory.Exists(Path.Combine(RepositoryTooling.Root, ".git")) || File.Exists(Path.Combine(RepositoryTooling.Root, ".git"));
        var files = StampedAssemblyPatterns
            .SelectMany(pattern => Directory.GetFiles(AppContext.BaseDirectory, pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var expected in new[] { "GameServer.Core.dll", "GameServer.Platform.Web.dll", "GameServer.TestKit.dll", "SampleHost.dll", "Template.Module.Adapters.dll" })
        {
            files.Select(Path.GetFileName).ShouldContain(expected);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var info = FileVersionInfo.GetVersionInfo(file);
            AssemblyName.GetAssemblyName(file).Version.ShouldBe(new Version(declared + ".0"), $"{name} assembly version");
            info.FileVersion.ShouldBe(declared + ".0", $"{name} file version");
            var informational = info.ProductVersion ?? string.Empty;
            informational.Split('+')[0].ShouldBe(declared, $"{name} informational version '{informational}'");
            if (gitCheckout)
            {
                informational.ShouldMatch($"^{Regex.Escape(declared)}\\+[0-9a-f]{{40}}$", $"{name}: built from a git checkout, the informational version carries the sha");
            }
        }
    }

    [Fact]
    public void The_public_version_api_reports_the_declared_version()
    {
        var declared = RepositoryTooling.DeclaredFrameworkVersion();

        GameServerVersion.Framework.ShouldBe(declared);
        GameServerVersion.FrameworkInformational.ShouldStartWith(declared);
        GameServerVersion.Of(Assembly.Load(new AssemblyName("SampleHost"))).ShouldBe(declared, "SampleHost is built with the framework's props");
    }

    /// <summary>Null when equal; otherwise the failure text naming both declarations.</summary>
    internal static string? Mismatch(string declared, string? package) =>
        string.Equals(declared, package, StringComparison.Ordinal)
            ? null
            : $"Framework version mismatch: Directory.Build.props GameServerFrameworkVersion is '{declared}', {PackageJson} version is '{package}'. " +
              "Fix: bump both together; the release tag is v{version} (FR-051).";

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();
}
