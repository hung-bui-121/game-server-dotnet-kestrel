using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Domain = GameServer.Modules.Identity;
using WireCustom = GameServer.Modules.Identity.Protocol.V1.CustomCredential;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 009: game adapters through <see cref="IIdentityProviderAdapter"/> (EXT-01, US4), single-use proofs and replay
/// before verification (IDN-05, CMD-04), and provider settings checked at startup.
/// </summary>
public sealed class ProviderExtensionTests
{
    /// <summary>Game adapter whose proofs are single use, like a Play Games server auth code.</summary>
    private sealed class SingleUseAdapter : IIdentityProviderAdapter
    {
        public ConcurrentDictionary<string, int> Used { get; } = new(StringComparer.Ordinal);

        public string Provider => "acme";

        public ValueTask<VerifiedIdentity> VerifyAsync(Domain.IdentityCredential credential, CancellationToken cancellationToken)
        {
            var subject = Encoding.UTF8.GetString(((Domain.CustomCredential)credential).Data);
            if (Used.AddOrUpdate(subject, 1, (_, n) => n + 1) > 1)
            {
                throw new GameServerException(IdentityErrors.IdentityProofInvalid, "Code already redeemed.");
            }

            return ValueTask.FromResult(new VerifiedIdentity(Provider, subject, new Dictionary<string, string> { ["source"] = "acme" }));
        }
    }

    private static ProviderCredential Acme(string subject) =>
        new() { Custom = new WireCustom { Provider = "acme", Data = ByteString.CopyFromUtf8(subject) } };

    [Fact]
    public async Task Game_adapter_serves_login_and_link_and_a_committed_link_is_replayed_without_verifying_again()
    {
        var adapter = new SingleUseAdapter();
        await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IIdentityProviderAdapter>(adapter));
        var guest = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var requestId = ProtobufHttp.NewRequestId();
        var body = new LinkIdentityRequest { Credential = Acme("user-1") };

        var first = await (await host.BearerClient(guest.SessionToken).PostProtobufAsync(IdentityModule.LinkRoute, body, requestId)).ReadProtobufAsync<CommandResponse>();
        var retry = await (await host.BearerClient(guest.SessionToken).PostProtobufAsync(IdentityModule.LinkRoute, body, requestId)).ReadProtobufAsync<CommandResponse>();

        first.Code.ShouldBe("OK");
        retry.Code.ShouldBe("OK");
        retry.Replayed.ShouldBeTrue();
        retry.ReceiptId.ShouldBe(first.ReceiptId);
        adapter.Used["user-1"].ShouldBe(1);
        var mapping = host.Store.All(IdentityCollections.Identities).Single(d => d.Key.Key == "acme:user-1").Content;
        mapping["playerId"]!.GetValue<string>().ShouldBe(guest.PlayerId);
        mapping["attributes"]!["source"]!.GetValue<string>().ShouldBe("acme");

        // A different body under the same request id is still caught before the provider is asked.
        var mismatch = await host.BearerClient(guest.SessionToken).PostProtobufAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Acme("user-2") }, requestId);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await mismatch.ReadErrorAsync()).Code.ShouldBe("IDEMPOTENCY_PAYLOAD_MISMATCH");
        adapter.Used.ContainsKey("user-2").ShouldBeFalse();

        var login = await (await host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = Acme("user-3") }))
            .ReadProtobufAsync<LoginResponse>();
        login.PlayerId.ShouldNotBe(guest.PlayerId);
    }

    /// <summary>Adapter that answers only when released (slow provider).</summary>
    private sealed class SlowAdapter : IIdentityProviderAdapter
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Provider => "slow";

        public async ValueTask<VerifiedIdentity> VerifyAsync(Domain.IdentityCredential credential, CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken);
            return new VerifiedIdentity(Provider, "s1", new Dictionary<string, string>());
        }
    }

    [Fact]
    public async Task Slow_providers_hit_the_deadline_and_the_bulkhead_instead_of_holding_requests()
    {
        var slow = new SlowAdapter();
        await using var host = await TestHost.StartAsync(
            settings: s =>
            {
                s["Identity:Providers:TimeoutSeconds"] = "1";
                s["Identity:Providers:MaxConcurrentVerifications"] = "1";
            },
            services: s => s.AddSingleton<IIdentityProviderAdapter>(slow));
        var credential = new ProviderCredential { Custom = new WireCustom { Provider = "slow" } };

        var first = host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential });
        await Task.Delay(100);
        var second = await host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential });
        second.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await second.ReadErrorAsync()).Code.ShouldBe("PROVIDER_UNAVAILABLE");

        var timedOut = await first;
        timedOut.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await timedOut.ReadErrorAsync()).Code.ShouldBe("PROVIDER_UNAVAILABLE");
    }

    [Fact]
    public async Task Provider_http_client_does_not_follow_redirects()
    {
        await using var host = await TestHost.StartAsync();
        HttpMessageHandler handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(IdentityProviders.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        handler.ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task Fakes_are_on_by_default_only_in_development()
    {
        await using var test = await TestHost.StartAsync(settings: s => s["GameServer:DeploymentEnvironment"] = "Test");
        test.Services.GetRequiredService<IdentityProviders>().Names.ShouldBeEmpty();

        await using var development = await TestHost.StartAsync();
        development.Services.GetRequiredService<IdentityProviders>().Names.ShouldBe(["pgs", "gamecenter"], ignoreOrder: true);
    }

    [Fact]
    public async Task Two_adapters_for_one_provider_fail_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(services: s =>
            {
                s.AddSingleton<IIdentityProviderAdapter>(new SingleUseAdapter());
                s.AddSingleton<IIdentityProviderAdapter>(new SingleUseAdapter());
            });
        });

        ex.Message.ShouldContain("Two identity provider adapters for 'acme'");
    }

    [Fact]
    public async Task Adapter_cannot_replace_a_built_in_or_use_the_guest_name()
    {
        (await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IIdentityProviderAdapter>(new FakeIdentityAdapter("pgs")));
        })).Message.ShouldContain("'pgs'");

        (await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IIdentityProviderAdapter>(new FakeIdentityAdapter("guest")));
        })).Message.ShouldContain("invalid name 'guest'");
    }

    [Theory]
    [InlineData("Identity:Providers:UseFake", "true", "Production", "UseFake is true in 'Production'")]
    [InlineData("Identity:Providers:PlayGames:Enabled", "true", "Production", "PlayGames is enabled without ClientId/ClientSecret")]
    [InlineData("Identity:Providers:GameCenter:Enabled", "true", "Production", "GameCenter is enabled without BundleIds")]
    [InlineData("Identity:Providers:GameCenter:MaxTimestampSkewMinutes", "11", "Development", "MaxTimestampSkewMinutes must be 1..10")]
    [InlineData("Identity:Providers:GameCenter:CertificateCacheHours", "25", "Development", "CertificateCacheHours must be 0..24")]
    [InlineData("Identity:Providers:TimeoutSeconds", "0", "Development", "TimeoutSeconds must be 1..30")]
    [InlineData("Identity:Providers:GameCenter:TrustedRootsBase64:0", "not-base64!", "Development", "TrustedRootsBase64 has an entry that is not a base64 DER certificate")]
    [InlineData("Identity:Providers:GameCenter:PublicKeyHosts:0", "evil.example", "Development", "PublicKeyHosts must list apple.com hosts")]
    [InlineData("Identity:Providers:MaxConcurrentVerifications", "0", "Development", "MaxConcurrentVerifications and LinkPerPlayerPerMinute must be 1..1000")]
    public async Task Unsafe_provider_settings_fail_startup(string key, string value, string environment, string message)
    {
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            await using var host = await TestHost.StartAsync(settings: s =>
            {
                s[key] = value;
                s["GameServer:DeploymentEnvironment"] = environment;
            });
        });

        ex.ToString().ShouldContain(message);
    }
}
