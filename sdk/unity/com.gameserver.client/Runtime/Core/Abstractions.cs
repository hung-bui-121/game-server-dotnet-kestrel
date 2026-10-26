#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer.Client
{
    /// <summary>Device clocks (TIME-01): wall clock for display, monotonic clock for elapsed time.</summary>
    public interface IClientClock
    {
        /// <summary>Device wall clock; the user can change it.</summary>
        DateTimeOffset UtcNow { get; }

        /// <summary>Monotonic time since an arbitrary origin; never jumps. <c>ServerNow</c> advances with it.</summary>
        TimeSpan Monotonic { get; }
    }

    public sealed class SystemClientClock : IClientClock
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        public static SystemClientClock Instance { get; } = new SystemClientClock();

#pragma warning disable RS0030 // The SDK runs on devices: the device clock is the only wall clock.
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
#pragma warning restore RS0030

        public TimeSpan Monotonic => Watch.Elapsed;
    }

    /// <summary>Waiting between retries (replaceable so tests and coroutines need not sleep).</summary>
    public interface IDelay
    {
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class TaskDelay : IDelay
    {
        public static TaskDelay Instance { get; } = new TaskDelay();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Where the session token lives. The default keeps it in memory; a game may persist it (encrypted PlayerPrefs, keychain).
    /// The token is only ever sent in the <c>Authorization</c> header (IDN-10).
    /// </summary>
    public interface ISessionStore
    {
        string? Token { get; set; }

        string? PlayerId { get; set; }

        void Clear();
    }

    public sealed class InMemorySessionStore : ISessionStore
    {
        public string? Token { get; set; }

        public string? PlayerId { get; set; }

        public void Clear()
        {
            Token = null;
            PlayerId = null;
        }
    }

    /// <summary>Client-visible config files of one <c>configVersion</c> (CFG-03).</summary>
    public interface IConfigCache
    {
        string? Version { get; }

        IReadOnlyDictionary<string, byte[]> Files { get; }

        void Store(string version, IReadOnlyDictionary<string, byte[]> files);
    }

    public sealed class InMemoryConfigCache : IConfigCache
    {
        private IReadOnlyDictionary<string, byte[]> _files = new Dictionary<string, byte[]>();

        public string? Version { get; private set; }

        public IReadOnlyDictionary<string, byte[]> Files => _files;

        public void Store(string version, IReadOnlyDictionary<string, byte[]> files)
        {
            Version = version;
            _files = files;
        }
    }
}
