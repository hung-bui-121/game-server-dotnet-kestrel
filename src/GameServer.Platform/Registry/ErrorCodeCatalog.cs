using GameServer.Errors;

namespace GameServer.Platform.Registry;

/// <summary>Central error-code registry (API-11): core codes plus every module's codes.</summary>
public sealed class ErrorCodeCatalog
{
    private readonly Dictionary<string, ErrorCode> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ErrorCode> _byNumber = [];

    internal ErrorCodeCatalog(IEnumerable<ErrorCode> codes)
    {
        foreach (var code in codes)
        {
            _byName.TryAdd(code.Name, code);
            _byNumber.TryAdd(code.Number, code);
        }
    }

    public IReadOnlyCollection<ErrorCode> All => _byName.Values;

    public ErrorCode? FindByName(string name) => _byName.GetValueOrDefault(name);

    public ErrorCode? FindByNumber(int number) => _byNumber.GetValueOrDefault(number);
}
