using System.Reflection;
using GameServer.Config;
using GameServer.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace GameServer.Architecture.Tests;

/// <summary>
/// FR-024, FR-025, FR-070, US6-1 (slice 015 T055): no framework, sample or template module registers a declared config catalog
/// itself. The platform registers every declared catalog from its declaration and builds it before traffic (BC-8); a lazy
/// <c>TryAddSingleton(sp => X.Load(...))</c> left in <c>ConfigureServices</c> would hide its faults until the first request.
/// </summary>
public sealed class ConfigCatalogArchitectureTests
{
    private static readonly ModuleContext Context = new("test", "test", "Development");

    [Fact]
    public void No_module_registers_a_declared_catalog_in_configure_services()
    {
        var modules = ModuleTypes().Select(type => (IGameModule)Activator.CreateInstance(type)!).ToArray();
        var catalogs = DeclaredCatalogTypes(modules);
        modules.Select(m => m.GetType().Name).ShouldContain("EconomyModule");
        modules.Select(m => m.GetType().Name).ShouldContain("IapModule");
        modules.Select(m => m.GetType().Name).ShouldContain("SampleGameModule");
        modules.Select(m => m.GetType().Name).ShouldContain("TemplateModule");
        catalogs.Select(t => t.Name).ShouldBe(["BattleCatalog", "IapCatalog", "ResourceCatalog", "ShopCatalog"], ignoreOrder: true);

        modules.SelectMany(module => CatalogRegistrations(module, catalogs)).ShouldBeEmpty();
    }

    [Fact]
    public void A_lazy_catalog_factory_left_in_configure_services_is_named()
    {
        var module = new LazyCatalogModule();

        CatalogRegistrations(module, DeclaredCatalogTypes([module])).ShouldBe([$"{nameof(LazyCatalogModule)} registers {nameof(ProbeCatalog)}"]);
    }

    /// <summary>The registrations of a declared catalog type that <paramref name="module"/> makes into a fresh collection.</summary>
    private static IEnumerable<string> CatalogRegistrations(IGameModule module, IReadOnlySet<Type> catalogs)
    {
        var services = new ServiceCollection();
        module.ConfigureServices(services, Context);
        return services.Where(d => catalogs.Contains(d.ServiceType)).Select(d => $"{module.GetType().Name} registers {d.ServiceType.Name}");
    }

    private static HashSet<Type> DeclaredCatalogTypes(IEnumerable<IGameModule> modules) =>
        modules.SelectMany(m => m.Describe().ConfigCatalogs).Select(c => c.CatalogType).ToHashSet();

    /// <summary>Every concrete module with a parameterless constructor of the framework, the samples and the template.</summary>
    private static Type[] ModuleTypes() =>
        Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.StartsWith("GameServer.", StringComparison.Ordinal)
                           || name.StartsWith("SampleGame", StringComparison.Ordinal)
                           || name.StartsWith("Sample.", StringComparison.Ordinal)
                           || name.StartsWith("Template.", StringComparison.Ordinal))
            .Where(name => !name!.EndsWith(".Tests", StringComparison.Ordinal))
            .Select(name => Assembly.Load(new AssemblyName(name!)))
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                           typeof(IGameModule).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            .Distinct()
            .ToArray();

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

    public sealed class ProbeCatalog
    {
        public static ProbeCatalog Load(IGameConfig config) => new();
    }

    /// <summary>The shape this test exists to catch: a declared catalog that the module also registers lazily.</summary>
    private sealed class LazyCatalogModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "lazy",
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => ProbeCatalog.Load(c.Config), "probe.json")],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
            services.TryAddSingleton(sp => ProbeCatalog.Load(sp.GetRequiredService<IGameConfig>()));
    }
}
