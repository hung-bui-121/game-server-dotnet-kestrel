using System.Reflection;
using GameServer.Errors;
using GameServer.Modules;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// Slice 013 T068 (FR-060, FR-062, US7-2): the band <see cref="ErrorCodeBands.GameStart"/>..<see cref="ErrorCodeBands.GameEnd"/> belongs
/// to game modules. Every framework-owned module — core codes, built-in modules, samples and the module template, discovered
/// from the assemblies of this test's output — keeps its range and its codes outside it.
/// </summary>
public sealed class ErrorCodeBandTests
{
    private static readonly string[] FrameworkOwnedAssemblyPatterns = ["GameServer.*.dll", "Sample*.dll", "Template.*.dll"];

    /// <summary>The framework's ranges (data-model §5, PCR platform §8).</summary>
    private static readonly Dictionary<string, (int Start, int End)> KnownRanges = new(StringComparer.Ordinal)
    {
        ["core"] = (0, 999),
        ["sample.counter"] = (10000, 10099),
        ["economy"] = (11000, 11099),
        ["iap"] = (12000, 12099),
        ["gm"] = (13000, 13099),
        ["identity"] = (14000, 14099),
        ["sample.game"] = (40000, 40099),
        ["template.module"] = (90000, 90099),
    };

    [Fact]
    public void No_framework_owned_module_declares_an_error_code_in_the_game_band()
    {
        var owners = FrameworkOwnedErrorCodes();
        KnownRanges.Keys.ShouldBeSubsetOf(owners.Keys, "the discovery must see every module that owns a known range");

        var violations = owners.SelectMany(owner => Violations(owner.Key, owner.Value)).ToArray();

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Framework_owned_modules_keep_their_documented_ranges()
    {
        var owners = FrameworkOwnedErrorCodes();

        foreach (var (owner, range) in KnownRanges)
        {
            var set = owners[owner];
            (set.RangeStart, set.RangeEnd).ShouldBe(range, $"error-code range of '{owner}' (update data-model §5 and PCR platform §8 with it)");
        }
    }

    [Fact]
    public void A_range_or_code_inside_the_game_band_is_reported_with_module_and_range()
    {
        var code = new ErrorCode(50001, "TEMPLATE_PROBE", 200, false, "probe");

        Violations("template.module", new ErrorCodeSet(90000, 90099, code)).ShouldBe(
            ["[module template.module] error code TEMPLATE_PROBE(50001) is inside the game band 50000-89999 (FR-062). Fix: framework-owned modules use numbers outside the band."]);
        Violations("probe", new ErrorCodeSet(89990, 90010)).ShouldBe(
            ["[module probe] error-code range 89990-90010 overlaps the game band 50000-89999 (FR-062). Fix: framework-owned modules use ranges outside the band."]);
        Violations("core", CoreErrorCodes.All).ShouldBeEmpty();
    }

    private static IEnumerable<string> Violations(string owner, ErrorCodeSet set)
    {
        if (set.IsNone)
        {
            yield break;
        }

        if (set.RangeStart <= ErrorCodeBands.GameEnd && ErrorCodeBands.GameStart <= set.RangeEnd)
        {
            yield return $"[module {owner}] error-code range {set.RangeStart}-{set.RangeEnd} overlaps the game band {ErrorCodeBands.GameStart}-{ErrorCodeBands.GameEnd} (FR-062). " +
                         "Fix: framework-owned modules use ranges outside the band.";
        }

        foreach (var code in set.Codes.Where(c => c.Number is >= ErrorCodeBands.GameStart and <= ErrorCodeBands.GameEnd))
        {
            yield return $"[module {owner}] error code {code} is inside the game band {ErrorCodeBands.GameStart}-{ErrorCodeBands.GameEnd} (FR-062). " +
                         "Fix: framework-owned modules use numbers outside the band.";
        }
    }

    /// <summary>Core codes plus every <see cref="IGameModule"/> with a parameterless constructor in the framework, sample and template assemblies.</summary>
    private static Dictionary<string, ErrorCodeSet> FrameworkOwnedErrorCodes()
    {
        var owners = new Dictionary<string, ErrorCodeSet>(StringComparer.Ordinal) { ["core"] = CoreErrorCodes.All };
        var modules = FrameworkOwnedAssemblyPatterns
            .SelectMany(pattern => Directory.GetFiles(AppContext.BaseDirectory, pattern))
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith(".Tests", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file => Assembly.Load(AssemblyName.GetAssemblyName(file)))
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IGameModule).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null);
        foreach (var type in modules)
        {
            var descriptor = ((IGameModule)Activator.CreateInstance(type)!).Describe();
            owners.Add(descriptor.Name, descriptor.ErrorCodes);
        }

        return owners;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
