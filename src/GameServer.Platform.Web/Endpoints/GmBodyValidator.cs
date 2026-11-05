using System.Collections;
using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using GameServer.Platform.Web.Commands;

namespace GameServer.Platform.Web.Endpoints;

/// <summary>
/// Validates a deserialized GM body against its <see cref="GmBodyContract"/> (slice 017, research R2/R3): every DataAnnotation
/// the GM document shows, with JSON paths as keys (<c>level</c>, <c>outer.inner</c>, <c>items[1].quantity</c>), the JSON
/// presence of <c>[Required]</c> non-nullable value types, and for a command body the <c>reason</c> rule. The answer is the
/// <c>errors</c> member of a <c>400 BAD_REQUEST</c>; messages are the attributes' messages (no exception text).
/// </summary>
internal static class GmBodyValidator
{
    public const string ReasonMessage = "reason is required (1..500 characters, not only white space).";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Valid = FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>Message source of an absent value type without its own <c>[Required]</c> (same text as the attribute's).</summary>
    private static readonly RequiredAttribute Presence = new();

    /// <summary>
    /// The field errors of <paramref name="body"/> (empty = valid), keys in ordinal order. <paramref name="json"/> is the body the
    /// value was read from; it is parsed only when the contract has a <c>[Required]</c> value type. With
    /// <paramref name="commandBody"/> the <c>reason</c> of an <see cref="IGmCommandBody"/> must hold 1..500 characters, not only
    /// white space (merged with the annotation errors, A5).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(object? body, ReadOnlyMemory<byte> json, bool commandBody)
    {
        if (body is null)
        {
            return Valid;
        }

        var contract = GmBodyContract.For(body.GetType());
        var errors = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        if (contract.HasRules)
        {
            using var document = contract.NeedsPresence ? Parse(json) : null;
            Walk(contract, body, document?.RootElement, prefix: string.Empty, errors);
        }

        if (commandBody && body is IGmCommandBody command && (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > GmCommandEndpointFactory.MaxReasonLength))
        {
            var reason = contract.Properties.FirstOrDefault(p => p.ClrName == nameof(IGmCommandBody.Reason))?.JsonName ?? "reason";
            Add(errors, reason, ReasonMessage);
        }

        if (errors.Count == 0)
        {
            return Valid;
        }

        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, messages) in errors)
        {
            result[key] = messages.ToArray();
        }

        return result;
    }

    private static JsonDocument? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // The value was already read from these bytes; without a JSON view presence cannot be checked, nothing else changes.
            return null;
        }
    }

    private static void Walk(GmBodyContract contract, object instance, JsonElement? element, string prefix, SortedDictionary<string, List<string>> errors)
    {
        foreach (var property in contract.Properties)
        {
            var path = prefix.Length == 0 ? property.JsonName : prefix + "." + property.JsonName;
            var value = property.Get(instance);
            var child = Child(element, property.JsonName);
            if (property.RequiresPresence && element is { ValueKind: JsonValueKind.Object } && child is null)
            {
                // A missing number reads as 0, which [Required] accepts: absence is checked in the JSON (research R3, r1 m3).
                Add(errors, path, (property.Required ?? Presence).FormatErrorMessage(property.JsonName));
                continue;
            }

            if (property.Validations.Count > 0)
            {
                var context = new ValidationContext(instance) { MemberName = property.ClrName, DisplayName = property.JsonName };
                foreach (var attribute in property.Validations)
                {
                    var result = attribute.GetValidationResult(value, context);
                    if (result != ValidationResult.Success)
                    {
                        Add(errors, path, result?.ErrorMessage ?? attribute.FormatErrorMessage(property.JsonName));
                    }
                }
            }

            if (property.Nested is not { HasRules: true } || value is null)
            {
                continue;
            }

            if (!property.IsCollection)
            {
                Walk(property.Nested, value, child, path, errors);
                continue;
            }

            var index = 0;
            foreach (var item in (IEnumerable)value)
            {
                if (item is not null)
                {
                    Walk(property.Nested, item, Item(child, index), $"{path}[{index}]", errors);
                }

                index++;
            }
        }
    }

    /// <summary>The JSON member read into <paramref name="name"/>, matched without case like the GM JSON options.</summary>
    private static JsonElement? Child(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        var comparison = GameServerJson.Options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var member in value.EnumerateObject())
        {
            if (string.Equals(member.Name, name, comparison))
            {
                return member.Value;
            }
        }

        return null;
    }

    private static JsonElement? Item(JsonElement? element, int index) =>
        element is { ValueKind: JsonValueKind.Array } array && index < array.GetArrayLength() ? array[index] : null;

    private static void Add(SortedDictionary<string, List<string>> errors, string path, string message)
    {
        if (!errors.TryGetValue(path, out var messages))
        {
            messages = [];
            errors[path] = messages;
        }

        messages.Add(message);
    }
}
