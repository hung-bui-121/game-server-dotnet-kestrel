using System.Reflection;
using GameServer.Platform.Analytics;
using GameServer.Platform.Observability;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T028 (FR-012): every key of <see cref="LocalFlavorSettings.Keys"/> binds to an option property (walked like the
/// configuration binder: case-insensitive names, the longest matching section), is named in the consumer guide (T070), and
/// <see cref="LocalFlavorSettings.For"/> sets exactly that list.
/// </summary>
public sealed class LocalFlavorSettingsTests
{
    private static readonly Dictionary<string, Type> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        [GameServerOptions.SectionName] = typeof(GameServerOptions),
        [FileStoreOptions.SectionName] = typeof(FileStoreOptions),
        [AnalyticsOptions.SectionName] = typeof(AnalyticsOptions),
        [ObservabilityOptions.SectionName] = typeof(ObservabilityOptions),
    };

    [Fact]
    public void Every_key_binds_to_an_option_property()
    {
        LocalFlavorSettings.Keys.Count.ShouldBeGreaterThanOrEqualTo(8);
        LocalFlavorSettings.Keys.ShouldBeUnique();

        var unresolved = LocalFlavorSettings.Keys.Where(key => !Resolves(key)).ToList();

        unresolved.ShouldBeEmpty($"LocalFlavorSettings.Keys names keys that bind to no option property: {string.Join(", ", unresolved)}.");
    }

    [Fact]
    public void Every_key_is_named_in_the_consumer_guide()
    {
        var guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "guides", "consumer-guide.md"));

        var missing = LocalFlavorSettings.Keys.Where(key => !guide.Contains($"`{key}`", StringComparison.Ordinal)).ToList();

        missing.ShouldBeEmpty($"docs/guides/consumer-guide.md does not name these LocalFlavorSettings keys: {string.Join(", ", missing)}.");
    }

    [Theory]
    [InlineData("FileStore:Folderr")]
    [InlineData("GameServer:Jobs:Enabledd")]
    [InlineData("Observability:Logging:Nope")]
    [InlineData("GameServer:Analytics:Directory:Extra")]
    [InlineData("Unknown:Key")]
    public void The_walk_refuses_a_key_that_binds_nowhere(string key) => Resolves(key).ShouldBeFalse();

    [Fact]
    public void For_sets_exactly_the_listed_keys_under_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-local-test");

        var settings = LocalFlavorSettings.For(root, "lf-0000abcd");

        settings.Keys.ShouldBe(LocalFlavorSettings.Keys, ignoreOrder: true);
        settings["FileStore:Folder"].ShouldBe(Path.Combine(root, "data"));
        settings["GameServer:Analytics:Directory"].ShouldBe(Path.Combine(root, "analytics"));
        settings["Observability:Logging:Directory"].ShouldBe(Path.Combine(root, "logs"));
        settings["Observability:InstanceId"].ShouldBe("lf-0000abcd");
        settings["GameServer:Outbox:SweeperEnabled"].ShouldBe("false");
        settings["GameServer:Jobs:Enabled"].ShouldBe("false");
        settings["GameServer:Hosting:DrainDelaySeconds"].ShouldBe("0");
        settings["Observability:Logging:Console:Enabled"].ShouldBe("false");
    }

    [Fact]
    public void The_sweeper_switch_flips_only_the_sweeper()
    {
        var off = LocalFlavorSettings.For("root", "lf-1");
        var on = LocalFlavorSettings.For("root", "lf-1", outboxSweeper: true);

        on["GameServer:Outbox:SweeperEnabled"].ShouldBe("true");
        on.Where(p => p.Key != "GameServer:Outbox:SweeperEnabled").ShouldBe(off.Where(p => p.Key != "GameServer:Outbox:SweeperEnabled"), ignoreOrder: true);
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

        throw new InvalidOperationException("GameServer.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool Resolves(string key)
    {
        var section = Sections.Keys
            .Where(name => key.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .MaxBy(name => name.Length);
        if (section is null)
        {
            return false;
        }

        var type = Sections[section];
        foreach (var segment in key[(section.Length + 1)..].Split(':'))
        {
            var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.GetSetMethod() is not null && string.Equals(p.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                return false;
            }

            type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        }

        return true;
    }
}
