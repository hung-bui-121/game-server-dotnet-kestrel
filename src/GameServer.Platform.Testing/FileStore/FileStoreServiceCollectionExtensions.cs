using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Testing;

public static class FileStoreServiceCollectionExtensions
{
    private const string FolderKey = FileStoreOptions.SectionName + ":" + nameof(FileStoreOptions.Folder);
    private const string ResetKey = FileStoreOptions.SectionName + ":" + nameof(FileStoreOptions.ResetOnStart);
    private const string GameIdKey = GameServerOptions.SectionName + ":" + nameof(GameServerOptions.GameId);

    /// <summary>
    /// Registers the file store (local flavor): reads <c>FileStore:Folder</c> and <c>FileStore:ResetOnStart</c>, resolves the folder
    /// against the content root and registers the store, its initializer and hosted service, its health probe and its startup check.
    /// The store is container-owned: disposing the host releases the folder lock.
    /// </summary>
    public static IServiceCollection AddFileDocumentStore(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Read by hand: GameServer.Platform.Testing has no configuration binder (no new package, plan T017).
        var options = new FileStoreOptions { Folder = configuration[FolderKey] ?? string.Empty };
        string? error = null;
        if (configuration[ResetKey] is { Length: > 0 } reset)
        {
            if (bool.TryParse(reset, out var value))
            {
                options.ResetOnStart = value;
            }
            else
            {
                error = $"[filestore] {ResetKey} '{reset}' must be true or false.";
            }
        }

        var folder = string.IsNullOrWhiteSpace(options.Folder) ? string.Empty : Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Folder));
        var settings = new FileStoreSettings(folder, configuration[GameIdKey] ?? string.Empty, options.ResetOnStart, environment.ContentRootPath);
        services.RemoveAll<FileDocumentStore>();
        services.AddSingleton(sp => new FileDocumentStore(settings, sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<FileDocumentStore>>()));
        return Register(services, settings, error);
    }

    /// <summary>Registers an existing file store (tests); the caller owns and disposes it.</summary>
    public static IServiceCollection AddFileDocumentStore(this IServiceCollection services, FileDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        services.RemoveAll<FileDocumentStore>();
        services.AddSingleton(store);
        return Register(services, store.Settings, configurationError: null);
    }

    private static IServiceCollection Register(IServiceCollection services, FileStoreSettings settings, string? configurationError)
    {
        services.RemoveAll<ITransactionalDocumentStore>();
        services.RemoveAll<IKeyValueStore>();
        services.RemoveAll<IPagedQueryStore>();
        services.RemoveAll<IMaintenanceQueryStore>();
        services.RemoveAll<InMemoryDocumentStore>();
        services.RemoveAll<IStoreInitializer>();

        services.AddSingleton(sp => sp.GetRequiredService<FileDocumentStore>().Store);
        services.AddSingleton<ITransactionalDocumentStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.AddSingleton<IKeyValueStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.AddSingleton<IPagedQueryStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.AddSingleton<IMaintenanceQueryStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.AddSingleton<IStoreInitializer>(sp => sp.GetRequiredService<FileDocumentStore>());
        services.AddHostedService(sp => sp.GetRequiredService<FileDocumentStore>());

        // AddGameServer registers the default probe with TryAdd: replace it, so a faulted store fails readiness (R8).
        services.RemoveAll<IStoreHealthProbe>();
        services.AddSingleton<IStoreHealthProbe, FileStoreHealthProbe>();
        services.AddSingleton<IStartupCheck>(sp => new FileStoreCheck(settings, sp.GetRequiredService<ModuleRegistry>(), configurationError));
        return services;
    }
}
