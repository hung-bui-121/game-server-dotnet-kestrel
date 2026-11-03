using Microsoft.Extensions.DependencyInjection;

namespace GameServer.TestKit;

/// <summary>Choices of one <see cref="LocalFlavorFactory{TEntryPoint}"/>.</summary>
public sealed class LocalFlavorOptions
{
    /// <summary>ASP.NET Core environment = flavor file (<c>appsettings.{Environment}.json</c>). Default <c>Local</c>.</summary>
    public string Environment { get; set; } = "Local";

    /// <summary>Existing root to reuse (restart on the same data); the factory never deletes it.</summary>
    public string? Folder { get; set; }

    /// <summary>Runs the outbox sweeper (off by default: it starts background work).</summary>
    public bool OutboxSweeper { get; set; }

    /// <summary>Applied after <see cref="LocalFlavorSettings"/> (test overrides win).</summary>
    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs after the host's own registrations (<c>ConfigureTestServices</c>).</summary>
    public Action<IServiceCollection>? Services { get; set; }
}
