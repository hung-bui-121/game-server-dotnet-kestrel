extern alias sdk;

using System.IO.Compression;
using System.Net.Http.Headers;
using GameServer.Config;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// FR-055, US5-6 (slice 015 T049, research R13): the SDK gets a parseable bundle whether or not its transport asks for compression.
/// The transport decodes (HttpClient with a decompressing handler, UnityWebRequest on Editor and Windows player per T008), so the
/// SDK has no decoder of its own.
/// </summary>
public sealed class ConfigCompressionClientTests
{
    [Fact]
    public async Task The_sdk_stores_the_same_files_over_a_gzip_transport_and_over_the_default_transport()
    {
        await using var harness = await SdkHarness.StartAsync();
        var host = harness.Host.Services.GetRequiredService<IGameConfig>();
        var gzip = new GzipTransportHandler(harness.Host.App.GetTestServer().CreateHandler());
        using var gzipHttp = new HttpClient(gzip) { BaseAddress = harness.Host.Client().BaseAddress };

        var (plainClient, plainTransport, _) = harness.NewClient();
        var (gzipClient, _, _) = harness.NewClient(http: gzipHttp);
        foreach (var client in new[] { plainClient, gzipClient })
        {
            var (localId, secret) = SdkHarness.NewDevice();
            await client.LoginGuestAsync(localId, secret);
            (await client.RefreshConfigAsync()).ShouldBeTrue();
            client.Config.Version.ShouldBe(host.ClientConfigVersion);
        }

        // The decoding transport asked for gzip and got it; the default one sent no Accept-Encoding and got identity.
        gzip.ConfigEncodings.ShouldBe(["gzip"]);
        plainTransport.RequestsTo(GameServerClient.ConfigRoute).Single().Headers.Keys.ShouldNotContain("Accept-Encoding");
        gzipClient.Config.Files.Keys.Order(StringComparer.Ordinal).ShouldBe(plainClient.Config.Files.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, content) in plainClient.Config.Files)
        {
            gzipClient.Config.Files[name].ShouldBe(content, name);
            content.ShouldBe(host.GetFileBytes(name).ToArray(), name);
        }
    }

    /// <summary>
    /// What an HTTP stack with transparent decompression does (HttpClient <c>AutomaticDecompression</c>, UnityWebRequest): it sends
    /// <c>Accept-Encoding: gzip</c> and hands the decoded body up. TestServer has no such option, hence this handler.
    /// </summary>
    private sealed class GzipTransportHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> ConfigEncodings { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            var response = await base.SendAsync(request, cancellationToken);
            var encodings = response.Content.Headers.ContentEncoding.ToArray();
            if (request.RequestUri!.AbsolutePath.EndsWith(GameServerClient.ConfigRoute, StringComparison.Ordinal))
            {
                ConfigEncodings.AddRange(encodings);
            }

            if (!encodings.Contains("gzip"))
            {
                return response;
            }

            await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var decoder = new GZipStream(compressed, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            await decoder.CopyToAsync(decoded, cancellationToken);
            var content = new ByteArrayContent(decoded.ToArray());
            content.Headers.ContentType = response.Content.Headers.ContentType;
            response.Content = content;
            return response;
        }
    }
}
