extern alias sdk;

using System.Reflection;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// SC-008 (slice 014 T063): the SDK assembly keeps every public type and member it had before the slice (additions allowed).
/// <c>TestData/sdk-public-api.txt</c> is the snapshot recorded at the slice base (T001), one sorted line per type and member.
/// </summary>
public sealed class SdkPublicApiTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Sdk = typeof(sdk::GameServer.Client.GameServerClient).Assembly;

    [Fact]
    public void No_public_member_of_the_recorded_sdk_api_is_removed_or_changed()
    {
        Removed(Baseline(), Snapshot(Sdk)).ShouldBeEmpty();
    }

    [Fact]
    public void A_removed_or_changed_member_is_reported()
    {
        var baseline = Baseline();
        var current = Snapshot(Sdk).ToList();
        var dropped = current.First(line => line.Contains("GameServerClient :: field static const System.String LoginRoute", StringComparison.Ordinal));
        current.Remove(dropped);
        current.Add(dropped.Replace("api/v1/identity/login/guest", "api/v2/identity/login/guest", StringComparison.Ordinal));

        Removed(baseline, current).ShouldBe([dropped]);
    }

    internal static IEnumerable<string> Removed(IEnumerable<string> baseline, IEnumerable<string> current) =>
        baseline.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal);

    private static string[] Baseline() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "sdk-public-api.txt")).Where(line => line.Length > 0).ToArray();

    /// <summary>The recording rule of T001: exported types (base type, sorted interfaces) and their declared public members.</summary>
    internal static IReadOnlyList<string> Snapshot(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes())
        {
            lines.Add($"type {type.FullName} : {type.BaseType?.FullName} [{string.Join(",", type.GetInterfaces().Select(i => i.FullName).Order(StringComparer.Ordinal))}]");
            foreach (var member in type.GetMembers(Declared))
            {
                if (Describe(member) is { } text)
                {
                    lines.Add($"  {type.FullName} :: {text}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string? Describe(MemberInfo member) => member switch
    {
        MethodInfo { IsSpecialName: true } => null,
        MethodInfo method =>
            $"{(method.IsStatic ? "static " : string.Empty)}{method.ReturnType} {method.Name}<{string.Join(",", method.GetGenericArguments().Select(a => a.Name))}>" +
            $"({string.Join(", ", method.GetParameters().Select(p => p.ParameterType + " " + p.Name + (p.HasDefaultValue ? "=" + p.DefaultValue : string.Empty)))})",
        ConstructorInfo constructor => $".ctor({string.Join(", ", constructor.GetParameters().Select(p => p.ParameterType + " " + p.Name))})",
        PropertyInfo property =>
            $"prop {property.PropertyType} {property.Name} {{{(property.GetMethod?.IsPublic == true ? "get;" : string.Empty)}{(property.SetMethod?.IsPublic == true ? "set;" : string.Empty)}}}",
        FieldInfo field =>
            $"field {(field.IsStatic ? "static " : string.Empty)}{(field.IsLiteral ? "const " : string.Empty)}{field.FieldType} {field.Name}" +
            (field.IsLiteral ? " = " + field.GetRawConstantValue() : string.Empty),
        EventInfo @event => $"event {@event.EventHandlerType} {@event.Name}",
        Type => null,
        _ => member.ToString(),
    };
}
