namespace GameServer.WireGen;

/// <summary>An input, configuration or contract error (exit 1, contracts/wiregen-cli.md §3): one line per problem, naming what, where and the fix.</summary>
internal sealed class WireGenException : Exception
{
    public WireGenException(IReadOnlyList<string> lines)
        : base(string.Join(Environment.NewLine, lines))
    {
        Lines = lines;
    }

    public WireGenException(string line)
        : this([line])
    {
    }

    public IReadOnlyList<string> Lines { get; }
}
