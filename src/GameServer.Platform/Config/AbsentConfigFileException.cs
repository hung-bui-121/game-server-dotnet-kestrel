namespace GameServer.Platform.Config;

/// <summary>
/// A declaration read a config file that is not in the effective config (<c>ConfigContext.Json</c>; slice 015 T071 round 2, m-1).
/// It is the <see cref="KeyNotFoundException"/> that <c>ConfigContext.Json</c> documents, with the file named, so the runner gives
/// the "absent optional file" catalog line only when the build actually read an absent file, and names that file. Lines report it
/// as <see cref="KeyNotFoundException"/>: this type is not part of the public contract.
/// </summary>
internal sealed class AbsentConfigFileException(string file)
    : KeyNotFoundException($"Config file '{file}' is not in the effective config. Fix: check Config.HasFile(\"{file}\") first.")
{
    /// <summary>The config file name that was read.</summary>
    public string File { get; } = file;
}
