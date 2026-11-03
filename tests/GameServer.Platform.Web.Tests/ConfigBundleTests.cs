using System.Net;
using System.Text.Json.Nodes;
using GameServer.Config;
using GameServer.Platform.Web.Config;
using GameServer.Platform.Web.Hosting;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// FR-052, FR-053, FR-054, FR-044, BC-7 (slice 015 T046, T047; research R11, data-model §4.1, §5.6, startup-report §6): the bundle
/// is serialized once and is byte-identical to the whole message; its largest response is checked against the limit at startup;
/// the startup config line names both versions.
/// </summary>
public sealed class ConfigBundleTests
{
    private const string ConfigRoute = "/api/v1/config/get";

    public static TheoryData<int, string, long> Shapes() => new()
    {
        { 0, "client-1+0123456789ab", 1_792_972_800_000 },
        { 1, "client-1+0123456789ab", 0 },
        { 50, "client-1+0123456789ab", -1 },
        { 50, string.Empty, long.MinValue },
        { 1, string.Empty, long.MaxValue },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Prefix_and_tail_are_the_whole_message_serialized(int files, string clientVersion, long time)
    {
        var config = new StubConfig("server-1+ba9876543210", clientVersion, Files(files));
        var bundle = ConfigBundle.Build(config);

        var expected = new GetConfigResponse { ConfigVersion = config.ConfigVersion, ServerTimeUtcMs = time, ClientConfigVersion = clientVersion };
        expected.Files.AddRange(config.ClientVisibleFiles
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new ConfigFile { Name = name, Content = ByteString.CopyFrom(config.GetFileBytes(name).Span) }));

        bundle.Full(time).ShouldBe(expected.ToByteArray());
        bundle.Prefix.ToArray().Concat(bundle.Tail(time)).ShouldBe(expected.ToByteArray());
        GetConfigResponse.Parser.ParseFrom(bundle.Full(time)).ShouldBe(expected);
        bundle.FileCount.ShouldBe(files);
    }

    [Fact]
    public void The_largest_response_covers_every_server_time()
    {
        var bundle = ConfigBundle.Build(new StubConfig("server-1+ba9876543210", "client-1+0123456789ab", Files(3)));

        foreach (var time in new[] { long.MinValue, -1, 0, 1, 1_792_972_800_000, long.MaxValue })
        {
            bundle.Full(time).Length.ShouldBeLessThanOrEqualTo(bundle.MaxResponseBytes, $"time {time}");
        }

        bundle.Full(long.MinValue).Length.ShouldBe(bundle.MaxResponseBytes);
    }

    [Fact]
    public void Not_modified_and_the_version_comparison_follow_wire_changes_section_3()
    {
        var bundle = ConfigBundle.Build(new StubConfig("server-1+ba9876543210", "client-1+0123456789ab", Files(2)));

        GetConfigResponse.Parser.ParseFrom(bundle.NotModified(42)).ShouldBe(new GetConfigResponse
        {
            ConfigVersion = "server-1+ba9876543210",
            NotModified = true,
            ServerTimeUtcMs = 42,
            ClientConfigVersion = "client-1+0123456789ab",
        });
        bundle.IsCurrent("client-1+0123456789ab").ShouldBeTrue();
        bundle.IsCurrent("server-1+ba9876543210").ShouldBeTrue();
        bundle.IsCurrent("CLIENT-1+0123456789AB").ShouldBeFalse();
        bundle.IsCurrent(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public async Task The_host_sends_the_whole_message_serialized()
    {
        await using var host = await SampleTestHost.StartAsync();
        var config = host.Services.GetRequiredService<IGameConfig>();

        var response = await host.PlayerClient("p_bundle").PostProtobufAsync(ConfigRoute, new GetConfigRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = new GetConfigResponse
        {
            ConfigVersion = config.ConfigVersion,
            ServerTimeUtcMs = host.Clock.UtcNow.ToUnixTimeMilliseconds(),
            ClientConfigVersion = config.ClientConfigVersion,
        };
        expected.Files.AddRange(config.ClientVisibleFiles
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new ConfigFile { Name = name, Content = ByteString.CopyFrom(config.GetFileBytes(name).Span) }));
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(expected.ToByteArray());
    }

    [Fact]
    public async Task The_request_time_limit_still_answers_413_when_the_limit_is_lowered_after_the_start()
    {
        await using var host = await SampleTestHost.StartAsync();
        var bundle = host.Services.GetRequiredService<ConfigPhaseSummary>().Bundle;
        host.Services.GetRequiredService<GameServerOptions>().Limits.ConfigBundleBytes = bundle.Prefix.Length;

        var response = await host.PlayerClient("p_limit").PostProtobufAsync(ConfigRoute, new GetConfigRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadErrorAsync()).Code.ShouldBe("PAYLOAD_TOO_LARGE");
    }

    [Fact]
    public async Task A_limit_equal_to_the_largest_response_starts_and_one_byte_less_stops_with_one_line()
    {
        var largest = await LargestResponseAsync();

        await using (var host = await SampleTestHost.StartAsync(settings: s => s["GameServer:Limits:ConfigBundleBytes"] = Text(largest)))
        {
            (await host.PlayerClient("p_at_limit").PostProtobufAsync(ConfigRoute, new GetConfigRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(settings: s => s["GameServer:Limits:ConfigBundleBytes"] = Text(largest - 1));
        });

        ex.Violations.ShouldBe(
        [
            $"[config] the client-visible bundle needs a GetConfig response of up to {largest} bytes (4 files), over " +
            $"GameServer:Limits:ConfigBundleBytes = {largest - 1}. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).",
        ]);
    }

    [Fact]
    public async Task Without_the_player_api_there_is_no_size_check_and_the_bundle_is_not_served()
    {
        await using var host = await SampleTestHost.StartAsync(settings: s =>
        {
            s["GameServer:Hosting:MapPlayerApi"] = "false";
            s["GameServer:Limits:ConfigBundleBytes"] = "1";
        });
        var config = host.Services.GetRequiredService<IGameConfig>();

        ConfigLine(host).Message.ShouldBe(
            $"Config test-1: version {config.ConfigVersion}, client version {config.ClientConfigVersion}, 5 files (4 client-visible, " +
            "bundle not served), 0 rules, 0 catalogs.");
        Should.Throw<InvalidOperationException>(() => host.Services.GetRequiredService<ConfigPhaseSummary>().Bundle);
    }

    [Fact]
    public async Task The_config_line_is_logged_once_after_the_startup_line_with_both_versions()
    {
        await using var host = await SampleTestHost.StartAsync(GameHttp.FullGame);
        var config = host.Services.GetRequiredService<IGameConfig>();
        var bundle = host.Services.GetRequiredService<ConfigPhaseSummary>().Bundle;

        var lines = host.LogEntries
            .Where(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Level == LogLevel.Information)
            .ToArray();

        lines.Length.ShouldBe(2);
        lines[0].Message.ShouldStartWith("Starting ");
        lines[1].Message.ShouldBe(
            $"Config test-1: version {config.ConfigVersion}, client version {config.ClientConfigVersion}, 5 files (4 client-visible, " +
            $"bundle {bundle.MaxResponseBytes} B), 4 rules, 4 catalogs.");
        lines[1].Properties["ConfigVersion"].ShouldBe(config.ConfigVersion);
        lines[1].Properties["ClientConfigVersion"].ShouldBe(config.ClientConfigVersion);
        lines[1].Properties["BundleBytes"].ShouldBe(bundle.MaxResponseBytes);
    }

    private static async Task<int> LargestResponseAsync()
    {
        await using var host = await SampleTestHost.StartAsync();
        return host.Services.GetRequiredService<ConfigPhaseSummary>().Bundle.MaxResponseBytes;
    }

    private static CapturedLog ConfigLine(TestHost host) =>
        host.LogEntries.Single(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Message.StartsWith("Config ", StringComparison.Ordinal));

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, byte[]> Files(int count) =>
        Enumerable.Range(0, count).ToDictionary(
            i => $"table_{count - i:000}.json",
            i => System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["rows"] = new JsonArray(i, i * 7, "row " + i) }.ToJsonString()),
            StringComparer.Ordinal);

    /// <summary>A config with chosen versions (an empty client version included), every file client-visible.</summary>
    private sealed class StubConfig(string serverVersion, string clientVersion, Dictionary<string, byte[]> files) : IGameConfig
    {
        public string ConfigVersion => serverVersion;

        public string ClientConfigVersion => clientVersion;

        public string Label => "stub";

        public IReadOnlyCollection<string> FileNames => files.Keys;

        public IReadOnlyCollection<string> ClientVisibleFiles => files.Keys;

        public bool HasFile(string name) => files.ContainsKey(name);

        public ReadOnlyMemory<byte> GetFileBytes(string name) => files[name];

        public JsonNode GetFileJson(string name) => JsonNode.Parse(files[name])!;

        public T GetFile<T>(string name) => throw new NotSupportedException();
    }
}
