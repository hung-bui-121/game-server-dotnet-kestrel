using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Layer 2 of FG-010 (FR-003, FR-004, FR-007, research R3, R5): the request pipeline refuses a Dev endpoint where the Dev
/// profile is not allowed, and any profile value it does not know, before any authentication scheme runs.
/// </summary>
public sealed class DevProfileMiddlewareTests
{
    private const string PlayerScheme = "player-test";
    private const string GmScheme = "gm-test";
    private const string Route = "/api/v1/cheat/grant";
    private const string SecretToken = "Bearer secret-token-value";
    private const string SecretCookie = "gm_session=secret-cookie-value";

    [Theory]
    [InlineData("Staging", "beta")]
    [InlineData("Production", "live")]
    [InlineData("Qa", "qa")]
    public async Task Dev_endpoint_is_refused_before_authentication_outside_development(string deploymentEnvironment, string environmentId)
    {
        var run = await RunAsync(SecurityProfile.Dev, deploymentEnvironment, environmentId);

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        run.ErrorCode().ShouldBe("FORBIDDEN");
        run.Authentication.Calls.ShouldBeEmpty();
        run.NextCalled.ShouldBeFalse();
        var warning = run.Logs.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Dev endpoint POST {Route} refused in deployment environment {deploymentEnvironment} ({environmentId}).");
        warning.Message.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Dev_endpoint_in_development_authenticates_with_player_then_gm_schemes()
    {
        var run = await RunAsync(SecurityProfile.Dev, "Development", "dev", authenticatedScheme: GmScheme);

        run.NextCalled.ShouldBeTrue();
        run.Authentication.Calls.ShouldBe([PlayerScheme, GmScheme]);
        ActorClaims.GetActorType(run.Context.User).ShouldBe(ActorType.Gm);
        run.Logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dev_endpoint_in_test_accepts_a_player()
    {
        var run = await RunAsync(SecurityProfile.Dev, "Test", "test", authenticatedScheme: PlayerScheme);

        run.NextCalled.ShouldBeTrue();
        run.Authentication.Calls.ShouldBe([PlayerScheme]);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task Unknown_profile_value_is_refused_without_authentication(string deploymentEnvironment)
    {
        var run = await RunAsync((SecurityProfile)99, deploymentEnvironment, "dev", authenticatedScheme: PlayerScheme);

        run.Context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        run.ErrorCode().ShouldBe("FORBIDDEN");
        run.Authentication.Calls.ShouldBeEmpty();
        run.NextCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Player_and_gm_profiles_are_unchanged_outside_development()
    {
        var player = await RunAsync(SecurityProfile.Player, "Staging", "beta", authenticatedScheme: PlayerScheme);
        var gm = await RunAsync(SecurityProfile.Gm, "Staging", "beta", authenticatedScheme: GmScheme, method: "GET");

        player.NextCalled.ShouldBeTrue();
        player.Authentication.Calls.ShouldBe([PlayerScheme]);
        gm.NextCalled.ShouldBeTrue();
        gm.Authentication.Calls.ShouldBe([GmScheme]);
    }

    /// <summary>
    /// Host part (research R5, plan SD7): a Development host switched to Staging after startup, so the Dev route exists at
    /// request time although the validator would refuse it. Every way to authenticate is refused with no state change.
    /// </summary>
    [Fact]
    public async Task Dev_route_reached_after_startup_in_staging_is_refused_for_every_credential_without_state_change()
    {
        const string StaffId = "admin";
        const string Password = "dev-password-for-the-test";
        var logs = new RecordingLoggerProvider();
        await using var host = await SampleTestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule<CheatModule>().AddModule<GmModule>(),
            settings: s =>
            {
                s["Gm:Bootstrap:Staff:0:StaffId"] = StaffId;
                s["Gm:Bootstrap:Staff:0:DisplayName"] = "Staff admin";
                s["Gm:Bootstrap:Staff:0:Password"] = Password;
                s["Gm:Bootstrap:Staff:0:Roles:0"] = "Administrator";
            },
            services: s => s.AddSingleton<ILoggerProvider>(logs));
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var jar = new CookieContainer();
        var cookieClient = await host.GmLoginAsync(StaffId, Password, jar);
        var cookieSecrets = jar.GetCookies(new Uri("http://localhost/")).Select(c => c.Value)
            .Concat(cookieClient.DefaultRequestHeaders.GetValues(CsrfOptions.HeaderName))
            .ToArray();
        cookieSecrets.ShouldNotBeEmpty();
        var clients = new (string Name, HttpClient Client, string[] Secrets)[]
        {
            ("guest bearer", host.BearerClient(login.SessionToken), [login.SessionToken]),
            ("test-auth player", host.PlayerClient(login.PlayerId), []),
            ("test-auth gm", host.GmClient(StaffId, "economy.grant"), []),
            ("gm cookie", cookieClient, cookieSecrets),
        };

        var options = host.Services.GetRequiredService<GameServerOptions>();
        options.DeploymentEnvironment = "Staging";
        options.EnvironmentId = "beta";
        await host.FlushAnalyticsAsync();
        var loginEvents = host.Analytics.OfEvent("login").Count;
        var before = Snapshot(host);
        logs.Entries.Clear();

        foreach (var (name, client, _) in clients)
        {
            var response = await client.PostProtobufAsync(Route, new GameServer.Modules.Economy.Protocol.V1.CheatGrantRequest { Resource = "gold", Quantity = 5 });

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);
            (await response.ReadErrorAsync()).Code.ShouldBe("FORBIDDEN", name);
        }

        await host.FlushAnalyticsAsync();
        host.Analytics.OfEvent("login").Count.ShouldBe(loginEvents);
        Snapshot(host).ShouldBe(before);
        var warnings = logs.Entries.Where(e => e.Category == typeof(ProfileEnforcementMiddleware).FullName).ToArray();
        warnings.Length.ShouldBe(clients.Length);
        foreach (var warning in warnings)
        {
            warning.Level.ShouldBe(LogLevel.Warning);
            warning.Message.ShouldBe($"Dev endpoint POST {Route} refused in deployment environment Staging (beta).");
        }

        var secrets = clients.SelectMany(c => c.Secrets).Where(s => s.Length > 0).ToArray();
        foreach (var entry in logs.Entries)
        {
            foreach (var secret in secrets)
            {
                entry.Message.ShouldNotContain(secret);
            }
        }
    }

    /// <summary>Every document of every collection the host knows (player aggregates, receipts, sessions, GM sessions, ledger).</summary>
    private static Dictionary<string, string> Snapshot(TestHost host) =>
        host.Services.GetRequiredService<GameServer.Platform.Registry.ModuleRegistry>().RequiredCollections()
            .Append("sessions")
            .Distinct(StringComparer.Ordinal)
            .SelectMany(host.Store.All)
            .ToDictionary(d => d.Key.ToString(), d => d.Content.ToJsonString(), StringComparer.Ordinal);

    private static async Task<Run> RunAsync(
        SecurityProfile profile,
        string deploymentEnvironment,
        string environmentId,
        string? authenticatedScheme = null,
        string method = "POST")
    {
        var options = new GameServerOptions { GameId = "test", EnvironmentId = environmentId, DeploymentEnvironment = deploymentEnvironment };
        var authentication = new RecordingAuthenticationService(authenticatedScheme);
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = Route;
        context.Request.Headers.Authorization = SecretToken;
        context.Request.Headers.Cookie = SecretCookie;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(Route),
            0,
            new EndpointMetadataCollection(new SecurityProfileMetadata(profile)),
            Route));

        var schemes = new ActorSchemeOptions();
        schemes.PlayerSchemes.Add(PlayerScheme);
        schemes.GmSchemes.Add(GmScheme);
        var logger = new RecordingLogger();
        using var metrics = new PlatformMetrics();
        var writer = new ErrorResponseWriter(new FakeClock(), new GameConfig("test", "test+0", new Dictionary<string, byte[]>(), new HashSet<string>()), metrics);
        var nextCalled = false;
        var middleware = new ProfileEnforcementMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            writer,
            options,
            Microsoft.Extensions.Options.Options.Create(schemes),
            Microsoft.Extensions.Options.Options.Create(new CsrfOptions()),
            new UnusedAntiforgery(),
            logger);

        await middleware.InvokeAsync(context);
        return new Run(context, authentication, nextCalled, logger.Entries);
    }

    private sealed record Run(DefaultHttpContext Context, RecordingAuthenticationService Authentication, bool NextCalled, List<(LogLevel Level, string Message)> Logs)
    {
        public string? ErrorCode()
        {
            Context.Response.Body.Position = 0;
            return JsonNode.Parse(Context.Response.Body)!["code"]!.GetValue<string>();
        }
    }

    private sealed class RecordingAuthenticationService(string? succeedingScheme) : IAuthenticationService
    {
        public List<string> Calls { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Calls.Add(scheme!);
            if (scheme != succeedingScheme)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var type = scheme == GmScheme ? ActorClaims.GmType : ActorClaims.PlayerType;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ActorClaims.ActorType, type), new Claim(ActorClaims.ActorId, "a1")], scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, scheme!)));
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    private sealed class UnusedAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) => throw new NotSupportedException();
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CategoryLogger(RecordingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                provider.Entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    private sealed class RecordingLogger : ILogger<ProfileEnforcementMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
