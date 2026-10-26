using GameServer.Actors;

namespace GameServer.Commands;

/// <summary>
/// Static description of a state-changing command (DAT-09, PLY-06). The runner loads
/// <see cref="Touches"/> ∪ <see cref="Reads"/> once per attempt and may only write <see cref="Touches"/>.
/// </summary>
public sealed class CommandDescriptor
{
    public const int MaxTouches = 4;
    public const int MaxClaimMarkersLimit = 2;

    public CommandDescriptor(
        string operation,
        IReadOnlyList<string> touches,
        IReadOnlyList<string>? reads = null,
        int maxClaimMarkers = 0,
        SecurityProfile profile = SecurityProfile.Player,
        string? permission = null)
    {
        Operation = operation;
        Touches = touches;
        Reads = reads ?? [];
        MaxClaimMarkers = maxClaimMarkers;
        Profile = profile;
        Permission = permission;
    }

    /// <summary>Unique operation name, <c>&lt;module&gt;.&lt;command&gt;</c>.</summary>
    public string Operation { get; }

    /// <summary>Aggregates the command may write (1..4).</summary>
    public IReadOnlyList<string> Touches { get; }

    /// <summary>Aggregates the command only reads.</summary>
    public IReadOnlyList<string> Reads { get; }

    /// <summary>Maximum number of claim markers inserted per execution (0..2).</summary>
    public int MaxClaimMarkers { get; }

    public SecurityProfile Profile { get; }

    /// <summary>GM permission required when <see cref="Profile"/> is <see cref="SecurityProfile.Gm"/>.</summary>
    public string? Permission { get; }
}
