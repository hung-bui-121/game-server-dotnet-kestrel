using System.Text.Json.Nodes;

namespace GameServer.Config;

/// <summary>Immutable game config loaded from files at startup (CFG-01).</summary>
public interface IGameConfig
{
    /// <summary><c>"{label}+{hash12}"</c> of the effective file set.</summary>
    string ConfigVersion { get; }

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
