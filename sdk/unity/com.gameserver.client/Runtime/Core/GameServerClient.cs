#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Protocol.Core.V1;

namespace GameServer.Client
{
    /// <summary>Outcome of a command: <c>OK</c> or a business code (HTTP 200, no retry). Transport and protocol errors throw.</summary>
    public sealed class CommandResult<TResult>
        where TResult : class, IMessage<TResult>
    {
        public CommandResult(CommandResponse response, TResult? result)
        {
            Response = response;
            Result = result;
        }

        public CommandResponse Response { get; }

        public string Code => Response.Code;

        public bool IsOk => Response.Code == ErrorCodes.Ok;

        /// <summary>Empty when the command was rejected (no receipt).</summary>
        public string ReceiptId => Response.ReceiptId;

        public bool Replayed => Response.Replayed;

        /// <summary>Module result; null when rejected.</summary>
        public TResult? Result { get; }
    }

    /// <summary>
    /// Framework client SDK (SDK-01..03): Protobuf transport, session header, gate headers, one <c>requestId</c> per command with
    /// retries that resend the exact cached bytes, error mapping with account/session events, per-section revisions, server
    /// time offset and config version tracking. It holds no game rules: state comes only from server responses.
    /// </summary>
    public sealed class GameServerClient
    {
        public const string ProtobufContentType = "application/x-protobuf";
        public const string LoginRoute = "api/v1/identity/login/guest";
        public const string LogoutRoute = "api/v1/identity/logout";
        public const string SnapshotRoute = "api/v1/player/snapshot";
        public const string ConfigRoute = "api/v1/config/get";
        public const string TimeRoute = "v1/time";

        public const string RequestIdHeader = "X-Request-Id";
        public const string ClientVersionHeader = "X-Client-Version";
        public const string PlatformHeader = "X-Platform";
        public const string BuildIdHeader = "X-Build-Id";

        private readonly GameServerClientOptions _options;
        private readonly IHttpTransport _transport;
        private readonly IClientClock _clock;
        private readonly IDelay _delay;
        private readonly ISessionStore _session;
        private readonly IConfigCache _config;
        private readonly Random _random;
        private string? _announcedConfigVersion;

        public GameServerClient(
            GameServerClientOptions options,
            IHttpTransport transport,
            IClientClock? clock = null,
            IDelay? delay = null,
            ISessionStore? session = null,
            IConfigCache? config = null,
            Random? random = null)
        {
            _options = options;
            _transport = transport;
            _clock = clock ?? SystemClientClock.Instance;
            _delay = delay ?? TaskDelay.Instance;
            _session = session ?? new InMemorySessionStore();
            _config = config ?? new InMemoryConfigCache();
            _random = random ?? new Random();
        }

        /// <summary>The token was revoked by a newer login elsewhere (IDN-10). The SDK cleared it and will not log in again.</summary>
        public event Action<GameServerError>? SessionReplaced;

        /// <summary>ACCOUNT_SUSPENDED, ACCOUNT_DELETED or SWITCH_ACCOUNT.</summary>
        public event Action<GameServerError>? AccountBlocked;

        public event Action<GameServerError>? Maintenance;

        public event Action<GameServerError>? UpdateRequired;

        /// <summary>A response carried a config version the cache does not have; call <see cref="RefreshConfigAsync"/>.</summary>
        public event Action<string>? ConfigVersionChanged;

        public SectionStore Sections { get; } = new SectionStore();

        public IConfigCache Config => _config;

        public string? PlayerId => _session.PlayerId;

        public bool HasSession => !string.IsNullOrEmpty(_session.Token);

        /// <summary>Server clock minus device clock, from the midpoint of the last round trip (TIME-01).</summary>
        public TimeSpan ServerTimeOffset { get; private set; }

        public DateTimeOffset ServerNow => _clock.UtcNow + ServerTimeOffset;

        /// <summary>Last config version announced by the server.</summary>
        public string? ServerConfigVersion { get; private set; }

        public bool ConfigOutdated => ServerConfigVersion != null && ServerConfigVersion != _config.Version;

        /// <summary>Guest login (IDN-01): stores the token and the returned sections.</summary>
        public async Task<LoginResponse> LoginGuestAsync(string localId, byte[] secret, CancellationToken cancellationToken = default)
        {
            var request = new GuestLoginRequest { LocalId = localId, Secret = ByteString.CopyFrom(secret) };
            var response = await PostAsync(LoginRoute, request.ToByteArray(), NewRequestId(), authenticated: false, LoginResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
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
                await PostAsync(LogoutRoute, new LogoutRequest().ToByteArray(), NewRequestId(), authenticated: true, LogoutResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            }
            finally
            {
                _session.Clear();
                Sections.Clear();
            }
        }

        /// <summary>Refetches sections (all client-visible when <paramref name="sections"/> is empty) at their server revision.</summary>
        public async Task<SnapshotResponse> RefreshSectionsAsync(IEnumerable<string>? sections = null, CancellationToken cancellationToken = default)
        {
            var request = new SnapshotRequest();
            if (sections != null)
            {
                request.Sections.AddRange(sections);
            }

            var response = await PostAsync(SnapshotRoute, request.ToByteArray(), NewRequestId(), authenticated: true, SnapshotResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            foreach (var section in response.Sections)
            {
                Sections.Set(section);
            }

            TrackConfig(response.ConfigVersion);
            return response;
        }

        /// <summary>
        /// Sends a state-changing command. The body is serialized once and resent unchanged with the same
        /// <c>X-Request-Id</c> on every retry, so the server replays instead of applying twice (SDK-02, CMD-02).
        /// </summary>
        public async Task<CommandResult<TResult>> SendCommandAsync<TResult>(
            string route,
            IMessage request,
            MessageParser<TResult> resultParser,
            CancellationToken cancellationToken = default)
            where TResult : class, IMessage<TResult>
        {
            var body = request.ToByteArray();
            var response = await PostAsync(route, body, NewRequestId(), authenticated: true, CommandResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            TrackConfig(response.ConfigVersion);

            var stale = response.Updates.Where(u => !Sections.Apply(u)).Select(u => u.Id).ToList();
            if (stale.Count > 0)
            {
                await RefreshSectionsAsync(stale, cancellationToken);
            }

            var result = response.Code == ErrorCodes.Ok ? resultParser.ParseFrom(response.Result) : null;

            return new CommandResult<TResult>(response, result);
        }

        /// <summary>Downloads the client-visible config files when the server has a newer version (CFG-03). True when updated.</summary>
        public async Task<bool> RefreshConfigAsync(CancellationToken cancellationToken = default)
        {
            var request = new GetConfigRequest { ConfigVersion = _config.Version ?? string.Empty };
            var response = await PostAsync(ConfigRoute, request.ToByteArray(), NewRequestId(), authenticated: true, GetConfigResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            ServerConfigVersion = response.ConfigVersion;
            if (response.NotModified)
            {
                return false;
            }

            _config.Store(response.ConfigVersion, response.Files.ToDictionary(f => f.Name, f => f.Content.ToByteArray(), StringComparer.Ordinal));
            _announcedConfigVersion = response.ConfigVersion;
            return true;
        }

        /// <summary>Measures the server time offset without a session (TIME-01).</summary>
        public async Task<DateTimeOffset> SyncTimeAsync(CancellationToken cancellationToken = default)
        {
            await SendAsync("GET", TimeRoute, null, NewRequestId(), authenticated: false, TimeResponse.Parser, r => r.ServerTimeUtcMs, cancellationToken);
            return ServerNow;
        }

        private Task<T> PostAsync<T>(string route, byte[] body, string requestId, bool authenticated, MessageParser<T> parser, Func<T, long> serverTime, CancellationToken cancellationToken)
            where T : IMessage<T> =>
            SendAsync("POST", route, body, requestId, authenticated, parser, serverTime, cancellationToken);

        private async Task<T> SendAsync<T>(
            string method,
            string route,
            byte[]? body,
            string requestId,
            bool authenticated,
            MessageParser<T> parser,
            Func<T, long> serverTime,
            CancellationToken cancellationToken)
            where T : IMessage<T>
        {
            var url = new Uri(_options.BaseUrl, route);
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [RequestIdHeader] = requestId,
                    [ClientVersionHeader] = _options.ClientVersion,
                    [PlatformHeader] = _options.Platform,
                    [BuildIdHeader] = _options.BuildId,
                };
                if (authenticated)
                {
                    var token = _session.Token;
                    if (string.IsNullOrEmpty(token))
                    {
                        throw new GameServerException(GameServerError.Client(ErrorCodes.Unauthenticated, "No session: log in first."));
                    }

                    headers["Authorization"] = "Bearer " + token;
                }

                var sentAt = _clock.UtcNow;
                TransportResponse response;
                try
                {
                    response = await _transport.SendAsync(new TransportRequest(method, url, headers, body), cancellationToken);
                }
                catch (TransportException ex)
                {
                    // No response: the command may or may not have run; resending the same bytes is safe (replay).
                    if (attempt < _options.MaxRetries)
                    {
                        await _delay.DelayAsync(Backoff(attempt, 0), cancellationToken);
                        continue;
                    }

                    throw new GameServerException(GameServerError.Client(ErrorCodes.NetworkError, "No response from the server.", retryable: true), ex);
                }

                var receivedAt = _clock.UtcNow;
                if (response.Status == 200)
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

                var error = ParseError(response);
                if (error.Envelope != null)
                {
                    UpdateTime(error.Envelope.ServerTimeUtcMs, sentAt, receivedAt);
                    TrackConfig(error.Envelope.ConfigVersion);
                }

                var reported = error.Error;
                if (!Notify(reported) && reported.Retryable && attempt < _options.MaxRetries)
                {
                    await _delay.DelayAsync(Backoff(attempt, reported.RetryAfterMs), cancellationToken);
                    continue;
                }

                throw new GameServerException(reported);
            }
        }

        /// <summary>Raises the account/session/gate events. True when the error must not be retried by the SDK.</summary>
        private bool Notify(GameServerError error)
        {
            switch (error.Code)
            {
                case ErrorCodes.SessionReplaced:
                    // IDN-10: never log in again automatically; the game decides (e.g. "logged in on another device").
                    _session.Clear();
                    SessionReplaced?.Invoke(error);
                    return true;
                case ErrorCodes.AccountSuspended:
                case ErrorCodes.AccountDeleted:
                case ErrorCodes.SwitchAccount:
                    if (error.Code == ErrorCodes.AccountDeleted)
                    {
                        _session.Clear();
                    }

                    AccountBlocked?.Invoke(error);
                    return true;
                case ErrorCodes.Maintenance:
                    Maintenance?.Invoke(error);
                    return true;
                case ErrorCodes.UpdateRequired:
                    UpdateRequired?.Invoke(error);
                    return true;
                default:
                    return false;
            }
        }

        private static (GameServerError Error, ErrorEnvelope? Envelope) ParseError(TransportResponse response)
        {
            if (response.ContentType != null && response.ContentType.StartsWith(ProtobufContentType, StringComparison.OrdinalIgnoreCase))
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

            // A proxy or load balancer answered: 502/503/504 are worth retrying, everything else is not.
            var retryable = response.Status == 502 || response.Status == 503 || response.Status == 504;
            return (new GameServerError(ErrorCodes.InvalidResponse, -1, response.Status, "HTTP " + response.Status, retryable, 0, null, new Dictionary<string, string>()), null);
        }

        private TimeSpan Backoff(int attempt, int retryAfterMs)
        {
            var exponential = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt);
            var capped = Math.Min(exponential, _options.MaxBackoff.TotalMilliseconds);
            var jittered = capped * (1 + (((_random.NextDouble() * 2) - 1) * _options.Jitter));
            return TimeSpan.FromMilliseconds(Math.Max(jittered, retryAfterMs));
        }

        private void UpdateTime(long serverTimeUtcMs, DateTimeOffset sentAt, DateTimeOffset receivedAt)
        {
            if (serverTimeUtcMs <= 0)
            {
                return;
            }

            var midpoint = sentAt + TimeSpan.FromTicks((receivedAt - sentAt).Ticks / 2);
            ServerTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(serverTimeUtcMs) - midpoint;
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
                ConfigVersionChanged?.Invoke(version!);
            }
        }

        private static string NewRequestId() => Guid.NewGuid().ToString("D");
    }
}
