using System.Globalization;
using GameServer.Platform;

namespace Fixture.Game;

/// <summary>
/// A startup check that crosses config and host settings (FG-023): the save result echoes the note, so the longest note of
/// <c>notes.json</c> must fit <c>GameServer:Commands:ResultCompactMaxBytes</c>. It takes <see cref="GameConfigStatus"/>, never
/// <c>IGameConfig</c>, and skips when the config did not load or its rules failed (those lines already explain it).
/// </summary>
public sealed class NotesResultSizeCheck(GameConfigStatus status, GameServerOptions options) : IStartupCheck
{
    /// <summary>UTF-8 bytes of one UTF-16 code unit at most (a surrogate pair is 4 bytes for 2 units).</summary>
    private const int MaxUtf8BytesPerChar = 3;

    public void Check(StartupCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!status.DeclarationsPassed || !status.TryGetConfig(out var config))
        {
            return;
        }

        var maxLength = config.GetFile<NotesRules>(FixtureModuleDefinition.RulesFile).MaxLength;
        var worst = (long)maxLength * MaxUtf8BytesPerChar;
        var limit = options.Commands.ResultCompactMaxBytes;
        if (worst > limit)
        {
            result.Errors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"[fixture] notes.json maxLength {maxLength} allows a save result of up to {worst} bytes, over GameServer:Commands:ResultCompactMaxBytes = {limit}. " +
                $"Fix: set maxLength to at most {limit / MaxUtf8BytesPerChar} or raise the limit."));
        }
    }
}
