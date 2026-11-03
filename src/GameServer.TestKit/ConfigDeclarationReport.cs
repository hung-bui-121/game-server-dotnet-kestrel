using System.Diagnostics.CodeAnalysis;
using GameServer.Config;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.TestKit;

/// <summary>The lines of a <see cref="ConfigDeclarationCheck"/> run (exactly the host's) and the catalogs it built.</summary>
public sealed class ConfigDeclarationReport
{
    private readonly IReadOnlyDictionary<Type, object> _catalogs;

    internal ConfigDeclarationReport(
        IGameConfig config, IReadOnlyList<string> errors, IReadOnlyList<string> warnings, IReadOnlyDictionary<Type, object> catalogs)
    {
        Config = config;
        Errors = errors;
        Warnings = warnings;
        _catalogs = catalogs;
    }

    /// <summary>Required-file, rule and catalog lines that would fail the host's start, in the host's order.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Lines the host would log as startup warnings.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool Passed => Errors.Count == 0;

    /// <summary>The config the report was made from (<see cref="TestGameConfigStatus.From"/> publishes it).</summary>
    internal IGameConfig Config { get; }

    /// <summary>The built catalogs by type.</summary>
    internal IReadOnlyDictionary<Type, object> Catalogs => _catalogs;

    /// <summary>The built catalog; throws InvalidOperationException naming the lines when it was not built.</summary>
    public T Catalog<T>()
        where T : class =>
        TryGetCatalog<T>(out var catalog)
            ? catalog
            : throw new InvalidOperationException(
                $"Catalog {typeof(T).Name} was not built" +
                (Errors.Count == 0 ? ": no given module declares it." : ":" + Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => " - " + e))));

    public bool TryGetCatalog<T>([NotNullWhen(true)] out T? catalog)
        where T : class
    {
        catalog = _catalogs.GetValueOrDefault(typeof(T)) as T;
        return catalog is not null;
    }

    /// <summary>Throws InvalidOperationException listing every error; returns this report otherwise.</summary>
    public ConfigDeclarationReport ThrowIfFailed() =>
        Passed
            ? this
            : throw new InvalidOperationException(
                "Config declarations failed:" + Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => " - " + e)));

    /// <summary>Registers every built catalog as a singleton instance (BC-8: services built from modules alone).</summary>
    public IServiceCollection AddCatalogsTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var (type, catalog) in _catalogs)
        {
            services.AddSingleton(type, catalog);
        }

        return services;
    }
}
