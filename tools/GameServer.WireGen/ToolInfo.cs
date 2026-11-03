using System.Reflection;

namespace GameServer.WireGen;

/// <summary>
/// Facts baked into the tool at build (<c>AssemblyMetadata</c>, see the project file): the framework checkout it was built in, its
/// restored <c>Grpc.Tools</c> package folder and the versions the framework pins (FR-022, research R10, R12).
/// </summary>
/// <param name="FrameworkRoot">Framework checkout (folder of <c>GameServer.slnx</c>), with a trailing separator.</param>
/// <param name="GrpcToolsRoot">Restored <c>Grpc.Tools</c> package folder (<c>$(PkgGrpc_Tools)</c>).</param>
/// <param name="FrameworkVersion"><c>GameServerFrameworkVersion</c>, e.g. <c>0.7.0</c>.</param>
/// <param name="GrpcToolsVersion">CPM version of <c>Grpc.Tools</c> (protoc comes from it).</param>
/// <param name="ProtobufVersion">CPM version of <c>Google.Protobuf</c> (the Unity package ships the same dll).</param>
internal sealed record ToolInfo(string FrameworkRoot, string GrpcToolsRoot, string FrameworkVersion, string GrpcToolsVersion, string ProtobufVersion)
{
    private const string Prefix = "GameServer.WireGen.";

    /// <summary>The values of this build of the tool.</summary>
    public static ToolInfo Current { get; } = Read(typeof(ToolInfo).Assembly);

    internal static ToolInfo Read(Assembly assembly)
    {
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(a => a.Key[Prefix.Length..], a => a.Value, StringComparer.Ordinal);
        return new ToolInfo(
            Required(values, "FrameworkRoot"),
            Required(values, "GrpcToolsRoot"),
            Required(values, "FrameworkVersion"),
            Required(values, "Version.Grpc.Tools"),
            Required(values, "Version.Google.Protobuf"));
    }

    private static string Required(Dictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"GameServer.WireGen was built without its '{Prefix}{key}' metadata. Fix: build it from the framework checkout with central package " +
                "management (tools/GameServer.WireGen/GameServer.WireGen.csproj).");
}
