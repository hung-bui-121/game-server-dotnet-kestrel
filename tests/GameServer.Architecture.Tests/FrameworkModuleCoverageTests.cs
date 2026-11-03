extern alias samplehost;

using System.Reflection;
using GameServer.Modules;
using GameServer.Platform.Registry;
using GameServer.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// FR-034, US5-1 (slice 014 T063): the Unity package is generated from the SampleHost Local catalog, so that composition must
/// register every framework module, or a framework route or section would be missing from <c>FrameworkRoutes</c>/<c>FrameworkSections</c>.
/// </summary>
public sealed class FrameworkModuleCoverageTests
{
    [Fact]
    public async Task The_sample_host_local_composition_registers_every_framework_module()
    {
        var framework = FrameworkModuleTypes();
        framework.Select(t => t.Name).ShouldContain("IdentityModule");
        framework.Select(t => t.Name).ShouldContain("CheatModule");

        Uncovered(framework, await LocalCompositionModuleTypesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_framework_module_missing_from_the_composition_is_named()
    {
        var registered = await LocalCompositionModuleTypesAsync();
        var framework = FrameworkModuleTypes();
        var dropped = registered.First(t => t.Name == "IapModule");

        Uncovered(framework, registered.Where(t => t != dropped).ToArray()).ShouldBe([dropped.FullName!]);
    }

    internal static IEnumerable<string> Uncovered(IReadOnlyCollection<Type> framework, IReadOnlyCollection<Type> registered) =>
        framework.Except(registered).Select(t => t.FullName!).Order(StringComparer.Ordinal);

    /// <summary>Every concrete <see cref="IGameModule"/> with a parameterless constructor in a <c>GameServer.*</c> assembly (tests excluded).</summary>
    private static Type[] FrameworkModuleTypes() =>
        Directory.GetFiles(AppContext.BaseDirectory, "GameServer.*.dll")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith(".Tests", StringComparison.Ordinal))
            .Select(file => Assembly.Load(AssemblyName.GetAssemblyName(file)))
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                           typeof(IGameModule).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            .Distinct()
            .ToArray();

    private static async Task<Type[]> LocalCompositionModuleTypesAsync()
    {
        var options = new LocalFlavorOptions { Environment = "Local" };
        options.Settings["Observability:Logging:Console:Enabled"] = "false";
        await using var factory = new LocalFlavorFactory<samplehost::Program>(options);
        return factory.Services.GetRequiredService<ModuleRegistry>().Modules.Select(m => m.Module.GetType()).ToArray();
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
