using System.Net;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Security;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// US3 + US4 on two instances sharing one store and clock: revocation within 60 s (SEC-10, ACC-11) and one shared Data
/// Protection key ring, including rotation (OPS-04, ACC-49).
/// </summary>
public sealed class RevocationAndKeyRingTests : IAsyncLifetime
{
    private const string MasterKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private readonly InMemoryDocumentStore _store;
    private readonly FakeClock _clock = new();
    private TestHost _a = null!;
    private TestHost _b = null!;
    private string _playerId = null!;

    public RevocationAndKeyRingTests() => _store = new InMemoryDocumentStore(_clock);

    private static void Settings(Dictionary<string, string?> values)
    {
        values["DataProtection:MasterKey"] = MasterKey;
        values["DataProtection:RotationDelaySeconds"] = "0";
    }

    public async ValueTask InitializeAsync()
    {
        // Instances starting together: only one first key may win the shared document, the other loads it.
        var starting = await Task.WhenAll(
            GmTestHost.StartAsync(Settings, _store, _clock),
            GmTestHost.StartAsync(Settings, _store, _clock));
        (_a, _b) = (starting[0], starting[1]);
        await _a.Services.GetRequiredService<KeyRingService>().RefreshNowAsync(CancellationToken.None);
        await _b.Services.GetRequiredService<KeyRingService>().RefreshNowAsync(CancellationToken.None);
        var player = await _a.NewPlayerAsync();
        _playerId = player.PlayerId;
        await player.CheatAsync("gold", 100);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private string Adjust => EconomyModule.AdjustRoute.Replace("{playerId}", _playerId, StringComparison.Ordinal);

    /// <summary>The same browser (cookie jar) talking to the other instance: session and CSRF token travel along.</summary>
    private static async Task<HttpClient> SameBrowserOnAsync(TestHost host, CookieContainer jar, HttpClient original)
    {
        var client = host.Browser(jar);
        client.DefaultRequestHeaders.Add(CsrfOptions.HeaderName, original.DefaultRequestHeaders.GetValues(CsrfOptions.HeaderName).Single());
        await Task.CompletedTask;
        return client;
    }

    [Fact]
    public async Task A_cookie_issued_by_one_instance_works_on_the_other()
    {
        var jar = new CookieContainer();
        var onA = await _a.GmClientAsync("econ", jar);
        var onB = await SameBrowserOnAsync(_b, jar, onA);
        await onB.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
        await onB.PostCommandAsync(Adjust, new { resource = "gold", delta = 1, reason = "cross instance" }).ShouldBeAsync(200);
    }

    [Fact]
    public async Task Disabling_staff_ends_its_sessions_everywhere_within_60_seconds()
    {
        var jar = new CookieContainer();
        var onA = await _a.GmClientAsync("support", jar);
        var onB = await SameBrowserOnAsync(_b, jar, onA);
        await onB.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        var admin = await _a.GmClientAsync("admin");
        await admin.PostCommandAsync("/gm/v1/staff/support/disable", new { reason = "left" }).ShouldBeAsync(200);

        await onA.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await onB.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        (await _b.Browser().PostLoginAsync("support")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Removing_a_permission_applies_everywhere_within_60_seconds_without_signing_out()
    {
        var jar = new CookieContainer();
        var onA = await _a.GmClientAsync("econ", jar);
        var onB = await SameBrowserOnAsync(_b, jar, onA);
        await onB.PostCommandAsync(Adjust, new { resource = "gold", delta = 1, reason = "before" }).ShouldBeAsync(200);

        var admin = await _a.GmClientAsync("admin");
        var grant = new HttpRequestMessage(HttpMethod.Put, "/gm/v1/staff/econ/grant")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { roles = new[] { "Support" }, reason = "no more economy" }),
        };
        grant.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        await admin.SendAsync(grant).ShouldBeAsync(200);

        await onA.PostCommandAsync(Adjust, new { resource = "gold", delta = 1, reason = "after" }).ShouldBeAsync(403);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await onB.PostCommandAsync(Adjust, new { resource = "gold", delta = 1, reason = "after" }).ShouldBeAsync(403);
        await onB.GetAsync($"/gm/v1/players/{_playerId}").ShouldBeAsync(200);
    }

    [Fact]
    public async Task Revoking_all_sessions_ends_every_copy_but_allows_signing_in_again()
    {
        var first = await _a.GmClientAsync("econ");
        var second = await _b.GmClientAsync("econ");
        await first.PostCommandAsync("/gm/v1/staff/econ/sessions/revoke", new { reason = "lost laptop" }).ShouldBeAsync(200);

        await first.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await second.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        await (await _b.GmClientAsync("econ")).GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
    }

    [Fact]
    public async Task Rotation_keeps_old_cookies_valid_and_new_cookies_work_on_both_instances()
    {
        var oldJar = new CookieContainer();
        var oldOnA = await _a.GmClientAsync("support", oldJar);

        var admin = await _a.GmClientAsync("admin");
        var rotated = await admin.PostCommandAsync(GmModule.RotateKeysRoute, new { reason = "scheduled rotation" }).ShouldBeAsync(200);
        rotated["keyId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        await _b.Services.GetRequiredService<KeyRingService>().RefreshNowAsync(CancellationToken.None);
        _store.Peek(KeyRingRepository.DocumentKey)!["keys"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(2);

        await oldOnA.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
        await (await SameBrowserOnAsync(_b, oldJar, oldOnA)).GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        var newJar = new CookieContainer();
        var newOnA = await _a.GmClientAsync("econ", newJar);
        await (await SameBrowserOnAsync(_b, newJar, newOnA)).GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        // Audited in the same transaction, and a retried request id does not rotate twice.
        _store.All(GmModule.AuditCollection).ShouldContain(d => d.Content["operation"]!.GetValue<string>() == "ops.keys.rotate");
        var requestId = Guid.NewGuid().ToString("D");
        await admin.PostCommandAsync(GmModule.RotateKeysRoute, new { reason = "again" }, requestId).ShouldBeAsync(200);
        var keys = _store.Peek(KeyRingRepository.DocumentKey)!["keys"]!.AsArray().Count;
        (await admin.PostCommandAsync(GmModule.RotateKeysRoute, new { reason = "again" }, requestId).ShouldBeAsync(200))["replayed"]!.GetValue<bool>().ShouldBeTrue();
        _store.Peek(KeyRingRepository.DocumentKey)!["keys"]!.AsArray().Count.ShouldBe(keys);
    }

    [Fact]
    public void Instances_starting_together_keep_a_single_first_key()
    {
        _store.Peek(KeyRingRepository.DocumentKey)!["keys"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_key_activating_makes_data_protection_rebuild_its_cached_ring()
    {
        var repository = _a.Services.GetRequiredService<KeyRingRepository>();
        var keyManager = _a.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
        var service = _a.Services.GetRequiredService<KeyRingService>();
        await service.RefreshNowAsync(CancellationToken.None);
        var token = repository.ChangeToken;

        var activation = DateTimeOffset.UtcNow.AddMinutes(2);
        keyManager.CreateNewKey(activation, activation.AddDays(90));
        await service.RefreshNowAsync(CancellationToken.None);
        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Keys_are_stored_encrypted_under_the_master_key()
    {
        var stored = _store.Peek(KeyRingRepository.DocumentKey)!["keys"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        stored.ShouldNotBeEmpty();
        stored.ShouldAllBe(k => k.Contains("<encryptedKey", StringComparison.Ordinal) && !k.Contains("<masterKey", StringComparison.Ordinal));
    }
}
