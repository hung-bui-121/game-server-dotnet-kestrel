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

        /// <summary>A server <c>retryAfterMs</c> above this ends the call instead of waiting (the game decides).</summary>
        public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Throws <see cref="ArgumentException"/> for settings that would misroute or break retries.</summary>
        public void Validate()
        {
            if (!BaseUrl.IsAbsoluteUri || !BaseUrl.AbsolutePath.EndsWith('/'))
            {
                throw new ArgumentException("BaseUrl must be absolute and end with '/' (otherwise its last path segment is dropped).", nameof(BaseUrl));
            }

            if (MaxRetries < 0 || MaxRetries > 10)
            {
                throw new ArgumentException("MaxRetries must be 0..10.", nameof(MaxRetries));
            }

            if (Jitter < 0 || Jitter > 1)
            {
                throw new ArgumentException("Jitter must be 0..1.", nameof(Jitter));
            }

            if (InitialBackoff <= TimeSpan.Zero || MaxBackoff < InitialBackoff || MaxRetryAfter < TimeSpan.Zero)
            {
                throw new ArgumentException("Backoff settings must be positive and MaxBackoff >= InitialBackoff.");
            }

            if (string.IsNullOrEmpty(ClientVersion) || string.IsNullOrEmpty(Platform) || string.IsNullOrEmpty(BuildId))
            {
                throw new ArgumentException("ClientVersion, Platform and BuildId are required (GATE-02).");
            }
        }
    }
}
