#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer.Client
{
    /// <summary>Device wall clock; the SDK derives <c>ServerNow</c> from it (TIME-01).</summary>
    public interface IClientClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClientClock : IClientClock
    {
        public static SystemClientClock Instance { get; } = new SystemClientClock();

#pragma warning disable RS0030 // The SDK runs on devices: the device clock is the only clock (server time = device + offset).
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
#pragma warning restore RS0030
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
