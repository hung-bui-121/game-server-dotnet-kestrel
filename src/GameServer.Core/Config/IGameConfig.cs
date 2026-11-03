using System.Text.Json.Nodes;

namespace GameServer.Config;

/// <summary>Immutable game config loaded from files at startup (CFG-01).</summary>
public interface IGameConfig
{
    /// <summary>
    /// Server config version (CFG-01): <c>"{label}+{hash12}"</c> of the label, every effective file and the client-visible set.
    /// Stored in receipts and carried as <c>config_version</c>.
    /// </summary>
    string ConfigVersion { get; }

    /// <summary>
    /// <c>"{label}+{hash12}"</c> of what a client receives: the label and the client-visible files (FG-024, FG-019).
    /// Carried as <c>client_config_version</c> on player responses; never equal to <see cref="ConfigVersion"/>.
    /// </summary>
    string ClientConfigVersion { get; }

    string Label { get; }

    IReadOnlyCollection<string> FileNames { get; }

    IReadOnlyCollection<string> ClientVisibleFiles { get; }

    bool HasFile(string name);

    /// <summary>Raw bytes of an effective file.</summary>
    ReadOnlyMemory<byte> GetFileBytes(string name);

    /// <summary>Parsed JSON of an effective file.</summary>
    JsonNode GetFileJson(string name);

    /// <summary>Deserializes an effective file.</summary>
    T GetFile<T>(string name);
}
