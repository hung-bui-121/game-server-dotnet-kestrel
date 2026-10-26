#nullable enable
using System;

namespace GameServer.Client
{
    public sealed class GameServerClientOptions
    {
        public GameServerClientOptions(Uri baseUrl, string clientVersion, string platform, string buildId)
        {
            BaseUrl = baseUrl;
            ClientVersion = clientVersion;
            Platform = platform;
            BuildId = buildId;
        }

        /// <summary>Server root, e.g. <c>https://game.example.com/</c>.</summary>
        public Uri BaseUrl { get; }

        /// <summary>Sent on every request as <c>X-Client-Version</c> (GATE-02).</summary>
        public string ClientVersion { get; }

        /// <summary><c>android</c>, <c>ios</c>, <c>editor</c>… sent as <c>X-Platform</c>.</summary>
        public string Platform { get; }

        /// <summary>Sent as <c>X-Build-Id</c>.</summary>
        public string BuildId { get; }

        /// <summary>Retries after the first attempt for network errors and retryable codes (SDK-02).</summary>
        public int MaxRetries { get; set; } = 4;

        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Random spread of each backoff (0.2 = ±20 %), so clients do not retry in lockstep.</summary>
        public double Jitter { get; set; } = 0.2;
    }
}
