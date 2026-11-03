namespace GameServer.Errors;

/// <summary>
/// Error-code bands (API-05, FR-060). A game (consumer) module picks its range inside <see cref="GameStart"/>..<see cref="GameEnd"/>;
/// framework-owned modules (core, built-in, sample, template) never use that band (architecture test). The framework's own
/// ranges are listed in PCR platform §8.
/// </summary>
public static class ErrorCodeBands
{
    public const int GameStart = 50000;
    public const int GameEnd = 89999;

    /// <summary>Framework territory above the game band (the module template's example range 90000–90099 lives here).</summary>
    public const int FrameworkReservedStart = 90000;
    public const int FrameworkReservedEnd = 99999;
}
