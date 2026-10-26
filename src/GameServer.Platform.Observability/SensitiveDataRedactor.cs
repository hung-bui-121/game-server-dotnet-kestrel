using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace GameServer.Platform.Observability;

/// <summary>
/// LOG-05: replaces the value of any log property (also nested in structures and dictionaries) whose name looks
/// sensitive — token, password, secret, pepper, authorization, cookie, private key, store receipt payload — with
/// <c>***</c>. The framework does not log such values; this is the safety net for module code.
/// </summary>
public sealed partial class SensitiveDataRedactor : ILogEventEnricher
{
    public const string Mask = "***";

    private static readonly ScalarValue Masked = new(Mask);

    public static bool IsSensitive(string name) => SensitiveName().IsMatch(name);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            var redacted = IsSensitive(name) ? Masked : Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    /// <summary>Returns the same instance when nothing needed masking.</summary>
    private static LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case StructureValue structure:
            {
                var changed = false;
                var properties = structure.Properties.Select(p =>
                {
                    var next = IsSensitive(p.Name) ? Masked : Redact(p.Value);
                    changed |= !ReferenceEquals(next, p.Value);
                    return new LogEventProperty(p.Name, next);
                }).ToArray();
                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var elements = dictionary.Elements.Select(e =>
                {
                    var next = e.Key.Value is string key && IsSensitive(key) ? Masked : Redact(e.Value);
                    changed |= !ReferenceEquals(next, e.Value);
                    return new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, next);
                }).ToArray();
                return changed ? new DictionaryValue(elements) : value;
            }

            case SequenceValue sequence:
            {
                var changed = false;
                var elements = sequence.Elements.Select(e =>
                {
                    var next = Redact(e);
                    changed |= !ReferenceEquals(next, e);
                    return next;
                }).ToArray();
                return changed ? new SequenceValue(elements) : value;
            }

            default:
                return value;
        }
    }

    [GeneratedRegex("token|password|passwd|secret|pepper|authorization|cookie|privatekey|private_key|receiptpayload|signeddata|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();
}
