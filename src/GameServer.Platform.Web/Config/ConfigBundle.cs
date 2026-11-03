using GameServer.Config;
using GameServer.Protocol.Core.V1;
using Google.Protobuf;

namespace GameServer.Platform.Web.Config;

/// <summary>
/// The <c>GetConfig</c> response bytes of the host's config (research R11, data-model §4.1), built once by the config phase. The
/// client-visible files never change for the host lifetime, so the files and the server version (fields 1 and 3) are serialized
/// once as <see cref="Prefix"/>; only the per-request tail (server time 4, client version 5) is serialized per request.
/// Google.Protobuf writes fields in number order and merges concatenated messages, so <c>Prefix ++ Tail(t)</c> is byte-identical
/// to serializing the whole message (VERIFY V5).
/// </summary>
internal sealed class ConfigBundle
{
    private readonly byte[] _prefix;
    private readonly string _serverVersion;
    private readonly string _clientVersion;

    private ConfigBundle(byte[] prefix, int fileCount, string serverVersion, string clientVersion)
    {
        _prefix = prefix;
        _serverVersion = serverVersion;
        _clientVersion = clientVersion;
        FileCount = fileCount;

        // The tail is longest for a negative time (10-byte varint), so this is the largest response the config can produce (FR-053).
        MaxResponseBytes = prefix.Length + Tail(long.MinValue).Length;
    }

    /// <summary><c>GetConfigResponse{ config_version = server, files = client-visible sorted ordinal }</c>, serialized.</summary>
    public ReadOnlyMemory<byte> Prefix => _prefix;

    /// <summary>Number of client-visible files in the bundle.</summary>
    public int FileCount { get; }

    /// <summary>Length of the largest full response (<see cref="Prefix"/> plus the longest tail); checked against the limit at startup.</summary>
    public int MaxResponseBytes { get; }

    public static ConfigBundle Build(IGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var prefix = new GetConfigResponse { ConfigVersion = config.ConfigVersion };

        // The file bytes are wrapped, not copied: they are serialized right away and the config is immutable.
        prefix.Files.AddRange(config.ClientVisibleFiles
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new ConfigFile { Name = name, Content = UnsafeByteOperations.UnsafeWrap(config.GetFileBytes(name)) }));
        return new ConfigBundle(prefix.ToByteArray(), prefix.Files.Count, config.ConfigVersion, config.ClientConfigVersion);
    }

    /// <summary>
    /// True when the version a client holds names exactly the current client-visible files: the client version, or the server
    /// version that SDKs before 0.7.0 hold (ordinal, wire-changes §3).
    /// </summary>
    public bool IsCurrent(string requestVersion) =>
        string.Equals(requestVersion, _clientVersion, StringComparison.Ordinal)
        || string.Equals(requestVersion, _serverVersion, StringComparison.Ordinal);

    /// <summary>The per-request part of a full response: <c>GetConfigResponse{ server_time_utc_ms = t, client_config_version }</c>.</summary>
    public byte[] Tail(long serverTimeUtcMs) =>
        new GetConfigResponse { ServerTimeUtcMs = serverTimeUtcMs, ClientConfigVersion = _clientVersion }.ToByteArray();

    /// <summary>The whole full response, <c>Prefix ++ Tail(t)</c>; the endpoint writes the two parts without joining them.</summary>
    public byte[] Full(long serverTimeUtcMs)
    {
        var tail = Tail(serverTimeUtcMs);
        var full = new byte[_prefix.Length + tail.Length];
        _prefix.CopyTo(full, 0);
        tail.CopyTo(full, _prefix.Length);
        return full;
    }

    /// <summary>The <c>not_modified</c> response (a few dozen bytes, serialized per request).</summary>
    public byte[] NotModified(long serverTimeUtcMs) => new GetConfigResponse
    {
        ConfigVersion = _serverVersion,
        NotModified = true,
        ServerTimeUtcMs = serverTimeUtcMs,
        ClientConfigVersion = _clientVersion,
    }.ToByteArray();
}
