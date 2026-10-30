using System.Reflection;
using System.Text.RegularExpressions;
using GameServer.Platform;
using GameServer.Platform.Analytics;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Testing;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Slice 011 SC-008: every configuration key the operator docs name exists. The back-ticked keys of the environments guide,
/// PCR platform §17 and §18, the analytics guide and the README that start with <c>GameServer:</c>, <c>Couchbase:</c> or
/// <c>FileStore:</c> must each resolve, by reflection, to a bound (publicly settable) property of <see cref="GameServerOptions"/>,
/// <see cref="CouchbaseOptions"/>, <see cref="FileStoreOptions"/> or (slice 012, its own section bound separately)
/// <see cref="AnalyticsOptions"/>; a key resolves against the <b>longest</b> matching section. A key of a <c>GameServer</c> subsection written without its <c>GameServer:</c> prefix
/// (<c>Limits:…</c>, data-model §4) is refused too.
/// </summary>
public sealed partial class DocumentedConfigurationKeysTests
{
    private const string Guide = "docs/guides/environments-and-local-run.md";
    private const string Pcr = "docs/pcr/platform.md";
    private const string PcrSection = "## 17.";
    private const string AnalyticsPcrSection = "## 18.";
    private const string AnalyticsGuide = "docs/guides/analytics.md";
    private const string Readme = "README.md";
    private const int MinimumKeysPerDocument = 10;

    /// <summary>The owner's legacy keys of the guide's mapping table: documented as ignored (startup warning), never bound.</summary>
    private static readonly string[] LegacyKeys =
    [
        "Couchbase:ServerIp",
        "Couchbase:OperationTimeOut",
        "Couchbase:CompressThreshold",
        "Couchbase:MoxiPort",
        "Couchbase:BucketType",
        "Couchbase:DbCache",
    ];

    /// <summary>Documented as "not provided" (research R13): a deferred extension point, not a key of this build.</summary>
    private static readonly string[] NotProvidedKeys = ["GameServer:Staging:AllowCheatModule"];

    private static readonly Dictionary<string, Type> Sections = new(StringComparer.Ordinal)
    {
        [GameServerOptions.SectionName] = typeof(GameServerOptions),
        [CouchbaseOptions.SectionName] = typeof(CouchbaseOptions),
        [FileStoreOptions.SectionName] = typeof(FileStoreOptions),
        [AnalyticsOptions.SectionName] = typeof(AnalyticsOptions),
    };

    private static readonly string Root = RepositoryRoot();

    public static TheoryData<string> Documents() => new(Guide, Pcr + " " + PcrSection, Pcr + " " + AnalyticsPcrSection, AnalyticsGuide, Readme);

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_documented_key_is_a_bound_option_property(string document)
    {
        var unresolved = DocumentedKeys(document)
            .Where(key => !IsExcluded(key) && !Resolves(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        unresolved.ShouldBeEmpty($"{document} names keys that bind to no option property: {string.Join(", ", unresolved)}.");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void No_gameserver_subsection_key_is_written_without_its_prefix(string document)
    {
        var subsections = Bound(typeof(GameServerOptions))
            .Where(p => IsSection(p.PropertyType))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        subsections.ShouldContain("Limits");

        var unprefixed = CodeSpans(document)
            .Where(span => ConfigurationKeyShape().IsMatch(span) && subsections.Contains(span[..span.IndexOf(':', StringComparison.Ordinal)]))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        unprefixed.ShouldBeEmpty($"{document} writes GameServer keys without the GameServer: prefix: {string.Join(", ", unprefixed)}.");
    }

    [Theory]
    [InlineData(Guide)]
    [InlineData(Pcr + " " + PcrSection)]
    public void The_operator_docs_name_every_couchbase_and_file_store_key(string document)
    {
        DocumentedKeys(document).Count().ShouldBeGreaterThanOrEqualTo(MinimumKeysPerDocument, $"{document}: the key extraction found too little.");
        var documented = DocumentedKeys(document).ToHashSet(StringComparer.Ordinal);

        var missing = new[] { typeof(CouchbaseOptions), typeof(FileStoreOptions) }
            .SelectMany(type => Bound(type).Select(p => Sections.Single(s => s.Value == type).Key + ":" + p.Name))
            .Where(key => !documented.Contains(key))
            .ToList();

        missing.ShouldBeEmpty($"{document} does not document: {string.Join(", ", missing)} (data-model §4, §5).");
    }

    /// <summary>Slice 012 T061 (SC-009): the operator docs name every bound analytics setting.</summary>
    [Theory]
    [InlineData(Guide)]
    [InlineData(Pcr + " " + AnalyticsPcrSection)]
    public void The_operator_docs_name_every_analytics_key(string document)
    {
        var documented = DocumentedKeys(document).ToHashSet(StringComparer.Ordinal);

        var missing = Bound(typeof(AnalyticsOptions))
            .Select(p => AnalyticsOptions.SectionName + ":" + p.Name)
            .Where(key => !documented.Contains(key))
            .ToList();

        missing.ShouldBeEmpty($"{document} does not document: {string.Join(", ", missing)} (data-model §1).");
    }

    [Fact]
    public void The_excluded_keys_really_bind_to_nothing()
    {
        foreach (var key in LegacyKeys.Concat(NotProvidedKeys))
        {
            Resolves(key).ShouldBeFalse($"{key} is excluded as legacy or not provided, but it binds to an option property: remove the exclusion.");
        }
    }

    private static IEnumerable<string> DocumentedKeys(string document) => CodeSpans(document).Where(span => KnownKey().IsMatch(span));

    private static IEnumerable<string> CodeSpans(string document) => CodeSpan().Matches(Text(document)).Select(m => m.Groups[1].Value.Trim());

    /// <summary>The whole file, or the PCR section from its heading to the next level-2 heading.</summary>
    private static string Text(string document)
    {
        var parts = document.Split(' ', 2);
        var text = File.ReadAllText(Path.Combine(Root, parts[0]));
        if (parts.Length == 1)
        {
            return text;
        }

        var start = text.IndexOf("\n" + parts[1], StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{parts[0]} has no section '{parts[1]}'.");
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static bool IsExcluded(string key) =>
        LegacyKeys.Concat(NotProvidedKeys).Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Walks the key like the configuration binder: case-insensitive names, dictionary keys, list indexes.</summary>
    private static bool Resolves(string key)
    {
        if (Sections.ContainsKey(key))
        {
            return true;
        }

        // Longest section first: GameServer:Analytics:* binds to AnalyticsOptions, not to a GameServerOptions property.
        var section = Sections.Keys
            .Where(name => key.StartsWith(name + ":", StringComparison.Ordinal))
            .MaxBy(name => name.Length);
        if (section is null)
        {
            return false;
        }

        var type = Sections[section];
        var segments = key[(section.Length + 1)..].Split(':');
        for (var i = 0; i < segments.Length; i++)
        {
            var element = ElementType(type);
            if (DictionaryValueType(type) is { } value)
            {
                type = value;
            }
            else if (element is not null)
            {
                if (!int.TryParse(segments[i], out _))
                {
                    return false;
                }

                type = element;
            }
            else
            {
                var property = Bound(type).FirstOrDefault(p => string.Equals(p.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                if (property is null)
                {
                    return false;
                }

                type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            }
        }

        return true;
    }

    private static IEnumerable<PropertyInfo> Bound(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0);

    private static bool IsSection(Type type) =>
        type.IsClass && type != typeof(string) && ElementType(type) is null && DictionaryValueType(type) is null;

    private static Type? DictionaryValueType(Type type) =>
        type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>) && i.GetGenericArguments()[0] == typeof(string))
            ?.GetGenericArguments()[1];

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || DictionaryValueType(type) is not null)
        {
            return null;
        }

        return type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces()
                .Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
                ?.GetGenericArguments()[0];
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"GameServer.slnx not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex("`([^`\\r\\n]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex("^(GameServer|Couchbase|FileStore):[A-Za-z0-9:]+$")]
    private static partial Regex KnownKey();

    [GeneratedRegex("^[A-Za-z0-9]+:[A-Za-z0-9:]+$")]
    private static partial Regex ConfigurationKeyShape();
}
