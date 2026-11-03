using GameServer.Platform.Web.Config;

namespace GameServer.Platform.Web.Tests;

/// <summary>Test-only view of a <see cref="ConfigBundle"/>: the endpoint writes the two parts without joining them.</summary>
internal static class ConfigBundleTestExtensions
{
    /// <summary>The whole full response, <c>Prefix ++ Tail(t)</c>.</summary>
    public static byte[] Full(this ConfigBundle bundle, long serverTimeUtcMs)
    {
        var tail = bundle.Tail(serverTimeUtcMs);
        var full = new byte[bundle.Prefix.Length + tail.Length];
        bundle.Prefix.Span.CopyTo(full);
        tail.CopyTo(full, bundle.Prefix.Length);
        return full;
    }
}
