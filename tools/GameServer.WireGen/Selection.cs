namespace GameServer.WireGen;

/// <summary>
/// Which catalog entries a run generates (data-model §3.1, FR-031, FR-032, research R13): routes of the configured origin on the
/// player API or host surface with profile Player, Public or Dev and an owner that is not service-only (the GM surface never,
/// Public GM routes included); sections of the configured origin.
/// </summary>
internal static class Selection
{
    private static readonly HashSet<string> Surfaces = new(StringComparer.Ordinal) { "PlayerApi", "Host" };

    private static readonly HashSet<string> Profiles = new(StringComparer.Ordinal) { "Player", "Public", "Dev" };

    public static IReadOnlyList<ContractRoute> Routes(WireContract contract, string origin)
    {
        var selected = contract.Routes
            .Where(r => r.Origin == origin && Surfaces.Contains(r.Surface) && Profiles.Contains(r.Profile) && !r.ServiceOnly)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
        var errors = selected
            .Where(r => r.Methods.Count > 1)
            .Select(r => $"[route {r.Name}] declares {string.Join(",", r.Methods)}: a player-facing route has one method in the generated code. Fix: " +
                "declare one route per method (FG-026, plan N4).")
            .ToArray();
        return errors.Length > 0 ? throw new WireGenException(errors) : selected;
    }

    public static IReadOnlyList<ContractSection> Sections(WireContract contract, string origin) =>
        contract.Sections.Where(s => s.Origin == origin).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
}
