using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Gate;

/// <summary>Maintenance flag of the deployment (GATE-01).</summary>
public sealed record MaintenanceState
{
    public bool Enabled { get; init; }

    public string? Message { get; init; }

    public DateTimeOffset? ExpectedEndUtc { get; init; }
}

/// <summary>Minimum client version of one platform and where to update (GATE-02).</summary>
public sealed record MinClientVersion
{
    public string MinVersion { get; init; } = string.Empty;

    public string? UpdateUrl { get; init; }
}

/// <summary>
/// Document <c>gate/gate</c> (GATE-01, GATE-02): one per deployment, changed only by GM through an audited
/// transaction; read by the gate middleware through a ≤ 10 s cache.
/// </summary>
public sealed partial record GateDocument
{
    public static readonly DocumentKey Key = new(PlatformCollections.Gate, "gate");

    public MaintenanceState Maintenance { get; init; } = new();

    /// <summary>Platform (lower case, e.g. <c>android</c>, <c>ios</c>) → minimum version.</summary>
    public IReadOnlyDictionary<string, MinClientVersion> MinClientVersions { get; init; } = new Dictionary<string, MinClientVersion>(StringComparer.Ordinal);

    public long Rev { get; init; }

    public static GateDocument Parse(StoredDocument? document) =>
        document?.Content.Deserialize<GateDocument>(GameServerJson.Options) ?? new GateDocument();

    public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, GameServerJson.Options)!;

    /// <summary>Platform names accepted in the <c>X-Platform</c> header and in the gate document.</summary>
    public static bool IsValidPlatform(string? platform) => platform is not null && PlatformPattern().IsMatch(platform);

    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex PlatformPattern();
}

/// <summary>Client versions <c>a.b[.c[.d]]</c> (non-negative integers); missing parts count as 0 (research R3).</summary>
public readonly record struct ClientVersion(int Major, int Minor, int Patch, int Build) : IComparable<ClientVersion>
{
    public static bool TryParse(string? value, out ClientVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || parts[i].Length > 9 || !parts[i].All(char.IsAsciiDigit))
            {
                return false;
            }

            numbers[i] = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        }

        version = new ClientVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public int CompareTo(ClientVersion other) =>
        (Major, Minor, Patch, Build).CompareTo((other.Major, other.Minor, other.Patch, other.Build));

    public static bool operator <(ClientVersion left, ClientVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ClientVersion left, ClientVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ClientVersion left, ClientVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ClientVersion left, ClientVersion right) => left.CompareTo(right) >= 0;
}
