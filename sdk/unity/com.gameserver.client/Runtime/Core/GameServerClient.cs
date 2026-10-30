#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Protocol.Core.V1;

namespace GameServer.Client
{
    /// <summary>
    /// Outcome of a command: <c>OK</c> or a business code (HTTP 200, no retry). Transport and protocol errors before the
    /// server answered throw; once the server answered, the result is always returned (the command may have committed).
    /// </summary>
    public sealed class CommandResult<TResult>
        where TResult : class, IMessage<TResult>
    {
        public CommandResult(CommandResponse response, TResult? result, bool sectionsStale, bool resultUnreadable)
        {
            Response = response;
            Result = result;
            SectionsStale = sectionsStale;
            ResultUnreadable = resultUnreadable;
        }

        public CommandResponse Response { get; }

        public string Code => Response.Code;

        public bool IsOk => Response.Code == ErrorCodes.Ok;

        /// <summary>Empty when the command was rejected (no receipt).</summary>
        public string ReceiptId => Response.ReceiptId;

        public bool Replayed => Response.Replayed;

        /// <summary>Module result; null when rejected or unreadable.</summary>
        public TResult? Result { get; }

        /// <summary>Some sections could not be refreshed after the command; call <c>RefreshSectionsAsync</c> later.</summary>
        public bool SectionsStale { get; }

        /// <summary>The command was answered but its result bytes did not parse with the given parser.</summary>
        public bool ResultUnreadable { get; }
    }

    /// <summary>
    /// Framework client SDK (SDK-01..03): Protobuf transport, session header, gate headers, one <c>requestId</c> per command with
    /// retries that resend the exact cached bytes, error mapping with account/session events, per-section revisions, server
    /// time and config version tracking. It holds no game rules: state comes only from server responses.
    /// </summary>
    public sealed class GameServerClient
    {
        public const string ProtobufContentType = "application/x-protobuf";
        public const string LoginRoute = "api/v1/identity/login/guest";
        public const string ProviderLoginRoute = "api/v1/identity/login/provider";
        public const string LinkRoute = "api/v1/identity/link";
        public const string LogoutRoute = "api/v1/identity/logout";
        public const string DeleteAccountRoute = "api/v1/identity/delete-account";
        public const string SnapshotRoute = "api/v1/player/snapshot";
        public const string ConfigRoute = "api/v1/config/get";
        public const string TimeRoute = "v1/time";

        public const string RequestIdHeader = "X-Request-Id";
        public const string ClientVersionHeader = "X-Client-Version";
        public const string PlatformHeader = "X-Platform";
        public const string BuildIdHeader = "X-Build-Id";

        /// <summary>Detail key of ACCOUNT_DELETED: the date the account is erased (UTC, <c>yyyy-MM-ddTHH:mm:ssZ</c>).</summary>
        public const string PurgeAfterDetail = "purgeAfter";

        private readonly GameServerClientOptions _options;
        private readonly IHttpTransport _transport;
        private readonly IClientClock _clock;
        private readonly IDelay _delay;
        private readonly ISessionStore _session;
        private readonly IConfigCache _config;
        private readonly Random _random;
        private string? _announcedConfigVersion;

        // Server time anchor on the monotonic clock (device wall-clock changes do not move ServerNow).
        private DateTimeOffset _serverAnchor;
        private TimeSpan _monotonicAnchor;
        private TimeSpan _anchorRoundTrip = TimeSpan.MaxValue;
        private bool _hasTime;

        public GameServerClient(
            GameServerClientOptions options,
            IHttpTransport transport,
            IClientClock? clock = null,
            IDelay? delay = null,
            ISessionStore? session = null,
            IConfigCache? config = null,
            Random? random = null)
        {
            options.Validate();
            _options = options;
            _transport = transport;
            _clock = clock ?? SystemClientClock.Instance;
            _delay = delay ?? TaskDelay.Instance;
            _session = session ?? new InMemorySessionStore();
            _config = config ?? new InMemoryConfigCache();
            _random = random ?? new Random();
            Sections.HandlerFailed += ex => HandlerFailed?.Invoke(ex);
        }

        /// <summary>How a request may be retried when its outcome is unknown.</summary>
        private enum RetryMode
        {
            /// <summary>Reads and commands (same bytes + requestId → server replay): retry every retryable failure.</summary>
            Idempotent,

            /// <summary>
            /// Login is not deduplicated by the server and every login revokes older sessions: retry only answers that
            /// prove the server did not process it (RATE_LIMITED), never a lost or ambiguous attempt (review M1).
            /// </summary>
            RejectedOnly,
        }

        /// <summary>The token was revoked by a newer login elsewhere (IDN-10). The SDK cleared it and will not log in again.</summary>
        public event Action<GameServerError>? SessionReplaced;

        /// <summary>
        /// ACCOUNT_SUSPENDED, ACCOUNT_DELETED or SWITCH_ACCOUNT. For SWITCH_ACCOUNT the current session is kept: ask the
        /// player, then call <see cref="LoginWithProviderAsync"/> again with <c>switchAccount: true</c> and a fresh proof.
        /// Raised by <see cref="LoginWithProviderAsync"/> these codes describe the account being entered, and the current
        /// session is kept (the exception tells the caller which call failed).
        /// </summary>
        public event Action<GameServerError>? AccountBlocked;

        public event Action<GameServerError>? Maintenance;

        public event Action<GameServerError>? UpdateRequired;

        /// <summary>A response carried a config version the cache does not have; call <see cref="RefreshConfigAsync"/>.</summary>
        public event Action<string>? ConfigVersionChanged;

        /// <summary>A game event handler threw; the SDK caught it so request processing was not affected.</summary>
        public event Action<Exception>? HandlerFailed;

        public SectionStore Sections { get; } = new SectionStore();

        public IConfigCache Config => _config;

        public string? PlayerId => _session.PlayerId;

        public bool HasSession => !string.IsNullOrEmpty(_session.Token);

        /// <summary>Server clock, advanced with the device's monotonic clock since the best (lowest round-trip) sample (TIME-01).</summary>
        public DateTimeOffset ServerNow => _hasTime ? _serverAnchor + (_clock.Monotonic - _monotonicAnchor) : _clock.UtcNow;

        /// <summary>Server clock minus device wall clock, as of now.</summary>
        public TimeSpan ServerTimeOffset => ServerNow - _clock.UtcNow;

        /// <summary>Last config version announced by the server.</summary>
        public string? ServerConfigVersion { get; private set; }

        public bool ConfigOutdated => ServerConfigVersion != null && ServerConfigVersion != _config.Version;

        /// <summary>
        /// Guest login (IDN-01): stores the token and the returned sections. Not retried after a lost or ambiguous attempt
        /// (a late duplicate would revoke the session the client keeps); on <c>NETWORK_ERROR</c> the game calls it again.
        /// </summary>
        public async Task<LoginResponse> LoginGuestAsync(string localId, byte[] secret, CancellationToken cancellationToken = default)
        {
            var request = new GuestLoginRequest { LocalId = localId, Secret = ByteString.CopyFrom(secret) };
            var response = await SendAsync("POST", LoginRoute, request.ToByteArray(), null, RetryMode.RejectedOnly, LoginResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            return ApplyLogin(response);
        }

        /// <summary>
        /// Login with Play Games / Game Center / a game provider (IDN-11): returns the player linked to that identity on any
        /// device, or a new player for an unknown identity. The current session (if any) is sent so the server can answer
        /// SWITCH_ACCOUNT (session kept, <see cref="AccountBlocked"/> raised) instead of silently leaving this player; pass
        /// <paramref name="switchAccount"/> = true once the player confirmed. Not retried after a lost attempt (like guest
        /// login); Play Games codes are single use, so request a new one for every call.
        /// </summary>
        public async Task<LoginResponse> LoginWithProviderAsync(ProviderCredential credential, bool switchAccount = false, CancellationToken cancellationToken = default)
        {
            var request = new ProviderLoginRequest { Credential = credential, SwitchAccount = switchAccount };
            var current = string.IsNullOrEmpty(_session.Token) ? null : _session.Token;
            var response = await SendAsync("POST", ProviderLoginRoute, request.ToByteArray(), current, RetryMode.RejectedOnly, LoginResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            return ApplyLogin(response);
        }

        /// <summary>
        /// Links a provider identity to the logged-in player (IDN-05) so a reinstall can log in with it. Result codes: OK
        /// (<c>AlreadyLinked</c> when it was), PROVIDER_ALREADY_LINKED. Throws IDENTITY_CONFLICT when another player owns
        /// the identity: offer <see cref="LoginWithProviderAsync"/> instead; nothing is merged. Retries resend the same
        /// bytes: a retry after a commit is replayed, but a retry while the provider was failing re-presents a single-use
        /// Play Games code and may end in IDENTITY_PROOF_INVALID — fetch a new code and call again.
        /// </summary>
        public Task<CommandResult<LinkIdentityResult>> LinkProviderAsync(ProviderCredential credential, CancellationToken cancellationToken = default) =>
            SendCommandAsync(LinkRoute, new LinkIdentityRequest { Credential = credential }, LinkIdentityResult.Parser, cancellationToken);

        /// <summary>
        /// Requests deletion of the logged-in player's account (PRIV-01). The account is locked at once and erased after the
        /// grace period unless support cancels it. On OK the session and sections are cleared and <see cref="AccountBlocked"/>
        /// is raised with code ACCOUNT_DELETED and details <c>purgeAfter</c> (the erase date, <c>yyyy-MM-ddTHH:mm:ssZ</c>).
        /// A <see cref="GameServerException"/> ACCOUNT_DELETED from this call also means the deletion is committed (e.g. a
        /// retry after a lost response): the session and sections are cleared and the error details carry <c>purgeAfter</c>.
        /// </summary>
        public async Task<CommandResult<RequestAccountDeletionResult>> RequestAccountDeletionAsync(CancellationToken cancellationToken = default)
        {
            CommandResult<RequestAccountDeletionResult> result;
            try
            {
                result = await SendCommandAsync(DeleteAccountRoute, new RequestAccountDeletionRequest(), RequestAccountDeletionResult.Parser, cancellationToken);
            }
            catch (GameServerException ex) when (ex.Error.Code == ErrorCodes.AccountDeleted)
            {
                // The error path already cleared the session and raised AccountBlocked.
                Sections.Clear();
                throw;
            }

            if (result.IsOk)
            {
                _session.Clear();
                Sections.Clear();
                var details = new Dictionary<string, string>(StringComparer.Ordinal);
                if (result.Result != null)
                {
                    details[PurgeAfterDetail] = DateTimeOffset.FromUnixTimeMilliseconds(result.Result.PurgeAfterUtcMs).UtcDateTime
                        .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }

                Raise(AccountBlocked, new GameServerError(ErrorCodes.AccountDeleted, -1, 0, "Account deletion requested.", false, 0, null, details));
            }

            return result;
        }

        private LoginResponse ApplyLogin(LoginResponse response)
        {
            if (string.IsNullOrEmpty(response.SessionToken) || string.IsNullOrEmpty(response.PlayerId))
            {
                throw new GameServerException(GameServerError.Client(ErrorCodes.InvalidResponse, "Login response without a session."));
            }

            _session.Token = response.SessionToken;
            _session.PlayerId = response.PlayerId;
            Sections.Clear();
            foreach (var section in response.Sections)
            {
                Sections.Set(section);
            }

            TrackConfig(response.ConfigVersion);
            return response;
        }

        /// <summary>Ends the session on the server and forgets it locally.</summary>
        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await SendAsync("POST", LogoutRoute, new LogoutRequest().ToByteArray(), RequireToken(), RetryMode.Idempotent, LogoutResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            }
            finally
            {
                _session.Clear();
                Sections.Clear();
            }
        }

        /// <summary>Refetches sections (all client-visible when <paramref name="sections"/> is empty); never moves a section back in time.</summary>
        public async Task<SnapshotResponse> RefreshSectionsAsync(IEnumerable<string>? sections = null, CancellationToken cancellationToken = default)
        {
            var request = new SnapshotRequest();
            if (sections != null)
            {
                request.Sections.AddRange(sections);
            }

            var response = await SendAsync("POST", SnapshotRoute, request.ToByteArray(), RequireToken(), RetryMode.Idempotent, SnapshotResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            foreach (var section in response.Sections)
            {
                Sections.Set(section);
            }

            TrackConfig(response.ConfigVersion);
            return response;
        }

        /// <summary>
        /// Sends a state-changing command. The body is serialized once and resent unchanged with the same
        /// <c>X-Request-Id</c> (and the same session token) on every retry, so the server replays instead of applying twice
        /// (SDK-02, CMD-02). Once the server has answered, this method does not throw: local follow-up problems are reported
        /// in the result.
        /// </summary>
        public async Task<CommandResult<TResult>> SendCommandAsync<TResult>(
            string route,
            IMessage request,
            MessageParser<TResult> resultParser,
            CancellationToken cancellationToken = default)
            where TResult : class, IMessage<TResult>
        {
            var body = request.ToByteArray();
            var response = await SendAsync("POST", route, body, RequireToken(), RetryMode.Idempotent, CommandResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            if (string.IsNullOrEmpty(response.Code))
            {
                throw new GameServerException(GameServerError.Client(ErrorCodes.InvalidResponse, "Command response without a code."));
            }

            // From here on the command is known to the server: report problems, never throw (a caller retrying with a new
            // request id would apply it twice).
            TrackConfig(response.ConfigVersion);
            var stale = response.Updates.Where(u => !Sections.Apply(u)).Select(u => u.Id).ToList();
            var sectionsStale = false;
            if (stale.Count > 0)
            {
                try
                {
                    await RefreshSectionsAsync(stale, cancellationToken);
                }
                catch (Exception ex) when (ex is GameServerException || ex is OperationCanceledException)
                {
                    sectionsStale = true;
                }
            }

            TResult? result = null;
            var unreadable = false;
            if (response.Code == ErrorCodes.Ok)
            {
                try
                {
                    result = resultParser.ParseFrom(response.Result);
                }
                catch (InvalidProtocolBufferException)
                {
                    unreadable = true;
                }
            }

            return new CommandResult<TResult>(response, result, sectionsStale, unreadable);
        }

        /// <summary>Downloads the client-visible config files when the server has a newer version (CFG-03). True when updated.</summary>
        public async Task<bool> RefreshConfigAsync(CancellationToken cancellationToken = default)
        {
            var request = new GetConfigRequest { ConfigVersion = _config.Version ?? string.Empty };
            var response = await SendAsync("POST", ConfigRoute, request.ToByteArray(), RequireToken(), RetryMode.Idempotent, GetConfigResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            ServerConfigVersion = response.ConfigVersion;
            if (response.NotModified)
            {
                return false;
            }

            _config.Store(response.ConfigVersion, response.Files.ToDictionary(f => f.Name, f => f.Content.ToByteArray(), StringComparer.Ordinal));
            _announcedConfigVersion = response.ConfigVersion;
            return true;
        }

        /// <summary>Measures the server time without a session (TIME-01).</summary>
        public async Task<DateTimeOffset> SyncTimeAsync(CancellationToken cancellationToken = default)
        {
            await SendAsync("GET", TimeRoute, null, null, RetryMode.Idempotent, TimeResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            return ServerNow;
        }

        private string RequireToken()
        {
            var token = _session.Token;
            if (string.IsNullOrEmpty(token))
            {
                throw new GameServerException(GameServerError.Client(ErrorCodes.Unauthenticated, "No session: log in first."));
            }

            return token!;
        }

        private async Task<T> SendAsync<T>(
            string method,
            string route,
            byte[]? body,
            string? token,
            RetryMode mode,
            MessageParser<T> parser,
            Func<T, long> serverTime,
            CancellationToken cancellationToken)
            where T : IMessage<T>
        {
            var url = new Uri(_options.BaseUrl, route);

            // Built once: every attempt sends the same request id, token and body bytes.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RequestIdHeader] = Guid.NewGuid().ToString("D"),
                [ClientVersionHeader] = _options.ClientVersion,
                [PlatformHeader] = _options.Platform,
                [BuildIdHeader] = _options.BuildId,
            };
            if (token != null)
            {
                headers["Authorization"] = "Bearer " + token;
            }

            var request = new TransportRequest(method, url, headers, body);
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sentAt = _clock.Monotonic;
                TransportResponse response;
                try
                {
                    response = await _transport.SendAsync(request, cancellationToken);
                }
                catch (TransportException ex)
                {
                    // No response: a command may or may not have run; resending the same bytes is safe (replay). A login is not.
                    if (mode == RetryMode.Idempotent && attempt < _options.MaxRetries)
                    {
                        await _delay.DelayAsync(Backoff(attempt), cancellationToken);
                        continue;
                    }

                    throw new GameServerException(GameServerError.Client(ErrorCodes.NetworkError, "No response from the server.", retryable: true), ex);
                }

                var receivedAt = _clock.Monotonic;
                var protobuf = response.ContentType != null && response.ContentType.StartsWith(ProtobufContentType, StringComparison.OrdinalIgnoreCase);
                if (response.Status == 200 && protobuf)
                {
                    T message;
                    try
                    {
                        message = parser.ParseFrom(response.Body);
                    }
                    catch (InvalidProtocolBufferException ex)
                    {
                        throw new GameServerException(GameServerError.Client(ErrorCodes.InvalidResponse, "Malformed response."), ex);
                    }

                    UpdateTime(serverTime(message), sentAt, receivedAt);
                    return message;
                }

                var (error, envelope) = ParseError(response, protobuf);
                if (envelope != null)
                {
                    UpdateTime(envelope.ServerTimeUtcMs, sentAt, receivedAt);
                    TrackConfig(envelope.ConfigVersion);
                }

                if (Notify(error, route))
                {
                    throw new GameServerException(error);
                }

                var retryable = mode == RetryMode.Idempotent ? error.Retryable : error.Code == ErrorCodes.RateLimited;
                var retryAfter = TimeSpan.FromMilliseconds(Math.Max(0, error.RetryAfterMs));
                if (retryable && attempt < _options.MaxRetries && retryAfter <= _options.MaxRetryAfter)
                {
                    var backoff = Backoff(attempt);
                    await _delay.DelayAsync(backoff > retryAfter ? backoff : retryAfter, cancellationToken);
                    continue;
                }

                throw new GameServerException(error);
            }
        }

        /// <summary>Raises the account/session/gate events. True when the error must not be retried by the SDK.</summary>
        private bool Notify(GameServerError error, string route)
        {
            // A provider login reports the state of the account it would enter, not of the session this client holds:
            // ACCOUNT_DELETED / ACCOUNT_SUSPENDED / SWITCH_ACCOUNT there leave the current session alone (review m4).
            var aboutTarget = route == ProviderLoginRoute;
            switch (error.Code)
            {
                case ErrorCodes.SessionReplaced:
                    // IDN-10: never log in again automatically; the game decides (e.g. "logged in on another device").
                    _session.Clear();
                    Raise(SessionReplaced, error);
                    return true;
                case ErrorCodes.AccountSuspended:
                case ErrorCodes.AccountDeleted:
                case ErrorCodes.SwitchAccount:
                    if (error.Code == ErrorCodes.AccountDeleted && !aboutTarget)
                    {
                        _session.Clear();
                    }

                    Raise(AccountBlocked, error);
                    return true;
                case ErrorCodes.Maintenance:
                    Raise(Maintenance, error);
                    return true;
                case ErrorCodes.UpdateRequired:
                    Raise(UpdateRequired, error);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Game handlers must not break request processing (review M2).</summary>
        private void Raise<TArg>(Action<TArg>? handler, TArg argument)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var single in handler.GetInvocationList().Cast<Action<TArg>>())
            {
                try
                {
                    single(argument);
                }
#pragma warning disable CA1031 // Any game exception is caught by design and reported through HandlerFailed.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    try
                    {
                        HandlerFailed?.Invoke(ex);
                    }
#pragma warning disable CA1031
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        // A failing error handler is ignored.
                    }
                }
            }
        }

        private static (GameServerError Error, ErrorEnvelope? Envelope) ParseError(TransportResponse response, bool protobuf)
        {
            if (protobuf && response.Status != 200)
            {
                try
                {
                    var envelope = ErrorEnvelope.Parser.ParseFrom(response.Body);
                    if (!string.IsNullOrEmpty(envelope.Code))
                    {
                        return (GameServerError.FromEnvelope(envelope, response.Status), envelope);
                    }
                }
                catch (InvalidProtocolBufferException)
                {
                }
            }

            // Not our server's answer (proxy, WAF, captive portal, HTML 200): 200/502/503/504 are worth retrying for
            // idempotent requests, everything else is not.
            var retryable = response.Status == 200 || response.Status == 502 || response.Status == 503 || response.Status == 504;
            return (new GameServerError(ErrorCodes.InvalidResponse, -1, response.Status, "Unexpected HTTP " + response.Status + " response", retryable, 0, null, new Dictionary<string, string>()), null);
        }

        private TimeSpan Backoff(int attempt)
        {
            var exponential = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt);
            var capped = Math.Min(exponential, _options.MaxBackoff.TotalMilliseconds);
            var jittered = capped * (1 + (((_random.NextDouble() * 2) - 1) * _options.Jitter));
            return TimeSpan.FromMilliseconds(Math.Max(0, jittered));
        }

        /// <summary>
        /// Keeps the sample with the smallest round trip (tightest bound on the server time); a sample at most 50 % (+50 ms)
        /// worse replaces it, so the anchor follows server-side clock corrections without trusting slow round trips.
        /// </summary>
        private void UpdateTime(long serverTimeUtcMs, TimeSpan sentAt, TimeSpan receivedAt)
        {
            if (serverTimeUtcMs <= 0)
            {
                return;
            }

            var roundTrip = receivedAt - sentAt;
            var tolerance = _anchorRoundTrip == TimeSpan.MaxValue
                ? TimeSpan.MaxValue
                : TimeSpan.FromTicks((long)(_anchorRoundTrip.Ticks * 1.5)) + TimeSpan.FromMilliseconds(50);
            if (_hasTime && roundTrip > tolerance)
            {
                return;
            }

            _serverAnchor = DateTimeOffset.FromUnixTimeMilliseconds(serverTimeUtcMs);
            _monotonicAnchor = sentAt + TimeSpan.FromTicks(roundTrip.Ticks / 2);
            _anchorRoundTrip = roundTrip < _anchorRoundTrip ? roundTrip : _anchorRoundTrip;
            _hasTime = true;
        }

        private void TrackConfig(string? version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return;
            }

            ServerConfigVersion = version;
            if (version != _config.Version && version != _announcedConfigVersion)
            {
                _announcedConfigVersion = version;
                Raise(ConfigVersionChanged, version!);
            }
        }
    }
}
