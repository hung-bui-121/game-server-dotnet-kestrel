using Microsoft.Extensions.Configuration;

namespace GameServer.Platform.Web.Hosting;

public static class ConfigurationBinding
{
    /// <summary>
    /// Binds section <paramref name="sectionName"/> (a new <typeparamref name="T"/> when absent). A value the binder cannot convert
    /// (<c>Couchbase__KvTimeoutMs=abc</c>) becomes a <see cref="GameServerStartupException"/> naming the key, so the host prints the
    /// startup report and exits 1 (FR-009) instead of dying on an unhandled <see cref="InvalidOperationException"/>.
    /// </summary>
    public static T GetSettings<T>(this IConfiguration configuration, string sectionName)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        try
        {
            return configuration.GetSection(sectionName).Get<T>() ?? new T();
        }
        catch (InvalidOperationException ex)
        {
            throw new GameServerStartupException(
                $"[configuration] {ex.Message} Fix: give that key a value of the expected type (flavor file, or the environment variable " +
                "with ':' written as '__').");
        }
    }
}
