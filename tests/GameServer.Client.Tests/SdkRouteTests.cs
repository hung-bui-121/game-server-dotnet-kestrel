extern alias sdk;

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GameServer.Actors;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Validation;
using GameServer.Routing;
using Microsoft.Extensions.DependencyInjection;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// FG-026 (2), FR-033, FR-035 (SC-001, SC-002 SDK part): the SDK's route constants are the server catalog's player-facing framework
/// routes (generated <see cref="FrameworkRoutes"/>), and neither the SDK's own code nor the KestrelDemo client script holds a route
/// literal (the client script also no section id literal: T071, SC-002).
/// </summary>
public sealed partial class SdkRouteTests
{
    private const string SampleArgument = "a b/ç";

    private static readonly string Root = RepositoryRoot();

    private static readonly string PackageRuntime = Path.Combine(Root, "sdk", "unity", "com.gameserver.client", "Runtime");

    private static readonly HashSet<RouteSurface> GeneratedSurfaces = [RouteSurface.PlayerApi, RouteSurface.Host];

    private static readonly HashSet<SecurityProfile> GeneratedProfiles = [SecurityProfile.Player, SecurityProfile.Public, SecurityProfile.Dev];

    [Fact]
    public async Task Every_player_facing_framework_route_has_its_generated_member_and_method()
    {
        var routes = await PlayerFacingFrameworkRoutesAsync();
        var members = typeof(FrameworkRoutes).GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m is FieldInfo or MethodInfo { IsSpecialName: false })
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        routes.ShouldNotBeEmpty();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            var name = MemberName(route.Name);
            expected.Add(name);
            expected.Add(name + "Method");
            PathOf(name, route).ShouldBe(ExpectedPath(route.Template), route.Name);
            Constant(name + "Method").ShouldBe(route.Methods.Count == 0 ? "*" : route.Methods.ShouldHaveSingleItem(), route.Name);
        }

        members.ShouldBe(expected, ignoreOrder: true);
        members.ShouldNotContain(m => m.StartsWith("Gm", StringComparison.Ordinal));
        members.ShouldNotContain(m => m.StartsWith("PlatformHealth", StringComparison.Ordinal), "the load-balancer probes are not client routes (T086)");
    }

    [Fact]
    public void The_client_route_constants_keep_their_former_values()
    {
        GameServerClient.LoginRoute.ShouldBe("api/v1/identity/login/guest");
        GameServerClient.ProviderLoginRoute.ShouldBe("api/v1/identity/login/provider");
        GameServerClient.LinkRoute.ShouldBe("api/v1/identity/link");
        GameServerClient.LogoutRoute.ShouldBe("api/v1/identity/logout");
        GameServerClient.DeleteAccountRoute.ShouldBe("api/v1/identity/delete-account");
        GameServerClient.SnapshotRoute.ShouldBe("api/v1/player/snapshot");
        GameServerClient.ConfigRoute.ShouldBe("api/v1/config/get");
        GameServerClient.TimeRoute.ShouldBe("v1/time");
    }

    [Fact]
    public void The_sdk_code_has_no_route_literal()
    {
        var files = Directory.EnumerateFiles(Path.Combine(PackageRuntime, "Core"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(PackageRuntime, "Unity"), "*.cs", SearchOption.AllDirectories))
            .ToArray();

        files.ShouldNotBeEmpty();
        files.SelectMany(file => RouteLiterals(file, File.ReadAllText(file))).ShouldBeEmpty();
    }

    [Fact]
    public void The_literal_scan_finds_a_seeded_route_in_code_but_not_in_comments()
    {
        var source = File.ReadAllText(Path.Combine(PackageRuntime, "Core", "GameServerClient.cs"));
        RouteLiterals("copy.cs", source).ShouldBeEmpty();

        var seeded = new StringBuilder(source)
            .Append("\n// \"api/v1/in/a/comment\"\n/* \"gm/v1/in/a/block\" */\n")
            .Append("class Seeded { string A = \"api/v1/seeded\"; string B = @\"health/\"\"live\"\"\"; char C = '\"'; string D = \"v2/x\"; }\n")
            .ToString();

        RouteLiterals("copy.cs", seeded).ShouldBe(["copy.cs:\"api/v1/seeded\"", "copy.cs:\"health/\"live\"\"", "copy.cs:\"v2/x\""]);
    }

    [Fact]
    public async Task The_sample_client_has_no_route_or_section_id_literal()
    {
        var sectionIds = await SampleHostSectionIdsAsync();
        var source = File.ReadAllText(SampleClientFile);

        sectionIds.ShouldBe(["battle", "counter", "inventory", "profile", "progression", "wallet"], ignoreOrder: true);
        SampleClientLiterals(source, sectionIds).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_sample_client_scan_finds_a_seeded_route_and_section_id()
    {
        var sectionIds = await SampleHostSectionIdsAsync();
        var seeded = File.ReadAllText(SampleClientFile) +
                     "\n// \"wallet\" in a comment\nclass Seeded { string A = \"api/v1/battle/start\"; string B = \"wallet\"; string C = \"gold\"; }\n";

        SampleClientLiterals(seeded, sectionIds).ShouldBe(["SampleGameClient.cs:\"api/v1/battle/start\"", "SampleGameClient.cs:\"wallet\""]);
    }

    /// <summary>KestrelDemo's client script, opened by exact path (never a recursive scan of samples/: Unity's Library is there).</summary>
    private static string SampleClientFile =>
        Path.Combine(Root, "samples", "SampleGame", "KestrelDemo", "Assets", "SampleGame", "SampleGameClient.cs");

    private static IEnumerable<string> SampleClientLiterals(string source, IReadOnlySet<string> sectionIds) =>
        Literals("SampleGameClient.cs", source, value => RouteLike().IsMatch(value) || sectionIds.Contains(value));

    /// <summary>The SampleHost local composition's framework routes the generator selects (data-model §3.1; no unversioned health probe).</summary>
    private static async Task<RouteDeclaration[]> PlayerFacingFrameworkRoutesAsync()
    {
        await using var factory = SampleHostFactory();
        return factory.Services.GetRequiredService<WireCatalog>().Routes
            .Where(r => r.Origin == CatalogOrigin.Framework && !r.ServiceOnly &&
                        GeneratedSurfaces.Contains(r.Declaration.Surface) && GeneratedProfiles.Contains(r.Declaration.Profile) &&
                        !(r.Declaration.Surface == RouteSurface.Host && EndpointSecurityValidator.UnversionedProbeRoutes.Contains(r.Declaration.Template)))
            .Select(r => r.Declaration)
            .ToArray();
    }

    /// <summary>Every section id of the SampleHost local catalog (framework and game).</summary>
    private static async Task<HashSet<string>> SampleHostSectionIdsAsync()
    {
        await using var factory = SampleHostFactory();
        return factory.Services.GetRequiredService<WireCatalog>().Sections.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static LocalFlavorFactory<Program> SampleHostFactory()
    {
        var options = new LocalFlavorOptions { Environment = "Local" };
        options.Settings["Observability:Logging:Console:Enabled"] = "false";
        return new LocalFlavorFactory<Program>(options);
    }

    /// <summary>data-model §5: split on '.' and '_', upper-case the first letter of each part.</summary>
    private static string MemberName(string routeName) =>
        string.Concat(routeName.Split('.', '_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string? Constant(string name) => (string?)typeof(FrameworkRoutes).GetField(name)?.GetRawConstantValue();

    /// <summary>A constant's value, or a parameter helper's output for <see cref="SampleArgument"/> per parameter.</summary>
    private static string? PathOf(string name, RouteDeclaration route)
    {
        if (typeof(FrameworkRoutes).GetMethod(name) is { } helper)
        {
            helper.GetParameters().Select(p => p.Name).ShouldBe(TemplateParameter().Matches(route.Template).Select(m => m.Groups[1].Value), route.Name);
            return (string?)helper.Invoke(null, helper.GetParameters().Select(_ => (object)SampleArgument).ToArray());
        }

        return Constant(name);
    }

    private static string ExpectedPath(string template) =>
        TemplateParameter().Replace(template.TrimStart('/'), _ => Uri.EscapeDataString(SampleArgument));

    /// <summary>String literals of <paramref name="source"/> that start like a route (<c>api/</c>, <c>gm/</c>, <c>health/</c>, <c>v{n}/</c>).</summary>
    internal static IEnumerable<string> RouteLiterals(string file, string source) => Literals(file, source, RouteLike().IsMatch);

    /// <summary>
    /// String literals of <paramref name="source"/> that <paramref name="match"/> accepts, as <c>file:"value"</c>. A small lexer:
    /// comments and char literals are skipped, regular and verbatim strings are read with their escapes (interpolated strings count as
    /// regular ones).
    /// </summary>
    internal static IEnumerable<string> Literals(string file, string source, Func<string, bool> match)
    {
        var hits = new List<string>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i = source.IndexOf('\n', i) is var end and >= 0 ? end : source.Length;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i = source.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : source.Length;
            }
            else if (c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i++;
            }
            else if (c == '"' || (c is '@' or '$' && i + 1 < source.Length && source[i + 1] is '"' or '@' or '$'))
            {
                var verbatim = false;
                while (source[i] != '"')
                {
                    verbatim |= source[i] == '@';
                    i++;
                }

                var value = new StringBuilder();
                i++;
                while (i < source.Length)
                {
                    if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        value.Append('"');
                        i += 2;
                    }
                    else if (source[i] == '"')
                    {
                        i++;
                        break;
                    }
                    else if (!verbatim && source[i] == '\\' && i + 1 < source.Length)
                    {
                        value.Append(source[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        value.Append(source[i]);
                        i++;
                    }
                }

                if (match(value.ToString()))
                {
                    hits.Add($"{file}:\"{value}\"");
                }
            }
            else
            {
                i++;
            }
        }

        return hits;
    }

    [GeneratedRegex(@"^/?(api|gm|health|v[0-9]+)/")]
    private static partial Regex RouteLike();

    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex TemplateParameter();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("GameServer.slnx not found above " + AppContext.BaseDirectory);
    }
}
