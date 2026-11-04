#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace GameServer.Client
{
    /// <summary>
    /// A command whose outcome the client does not know yet (FG-022): recorded before the first attempt, removed when the server
    /// gave a final answer. Resending it sends <see cref="Body"/> with <see cref="RequestId"/> to <see cref="Route"/>, so the server
    /// replays a committed command instead of running it twice (CMD-02).
    /// </summary>
    public sealed class PendingCommand
    {
        private readonly byte[] _body;

        /// <exception cref="ArgumentException"><paramref name="requestId"/> is not a lowercase UUID in format D, or the player id or route is empty.</exception>
        public PendingCommand(string requestId, string playerId, string route, byte[] body, DateTimeOffset recordedAtServerUtc)
        {
            if (!PendingCommandFormat.IsLowercaseUuid(requestId))
            {
                throw new ArgumentException("The request id must be a lowercase UUID in format D.", nameof(requestId));
            }

            if (string.IsNullOrEmpty(playerId))
            {
                throw new ArgumentException("The player id is required.", nameof(playerId));
            }

            if (string.IsNullOrEmpty(route))
            {
                throw new ArgumentException("The route is required.", nameof(route));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            RequestId = requestId;
            PlayerId = playerId;
            Route = route;
            _body = (byte[])body.Clone();
            RecordedAtServerUtc = recordedAtServerUtc.ToUniversalTime();
        }

        public string RequestId { get; }

        /// <summary>The player of the session that sent it; another player's entry is never resent.</summary>
        public string PlayerId { get; }

        public string Route { get; }

        /// <summary>The exact bytes sent; never re-serialized. A copy: changing it does not change the entry.</summary>
        public byte[] Body => (byte[])_body.Clone();

        /// <summary>Server clock (<c>ServerNow</c>) when it was recorded; ages are measured on the server clock, not the device's.</summary>
        public DateTimeOffset RecordedAtServerUtc { get; }

        /// <summary>The stored bytes without a copy (the SDK sends them unchanged).</summary>
        internal byte[] BodyBytes => _body;
    }

    /// <summary>What <c>GameServerClient.StatusOf</c> says about an entry.</summary>
    public enum PendingCommandStatus
    {
        /// <summary>The session's player, younger than <c>PendingCommandMaxAge</c>: <c>ResendPendingAsync</c> may send it.</summary>
        Resendable,

        /// <summary>Older than <c>PendingCommandMaxAge</c> on the server clock: the server may no longer replay it; decide from state.</summary>
        Expired,

        /// <summary>Recorded for another player, or there is no session.</summary>
        OtherPlayer,
    }

    /// <summary>
    /// Keeps the commands whose outcome is unknown (FG-022), for <c>GameServerClientOptions.PendingCommands</c>. Not thread-safe:
    /// use it from the client's thread. One client per store. Never throws for disk problems: they are reported in
    /// <see cref="LoadProblem"/> and <see cref="WriteProblem"/> while the entries stay in memory.
    /// </summary>
    public interface IPendingCommandStore
    {
        /// <summary>
        /// The entries, ordered by recorded time, then request id. A snapshot: <see cref="Record"/> and <see cref="Remove"/> do not
        /// change a list already returned (the two SDK stores return a new read-only list after each change).
        /// </summary>
        IReadOnlyList<PendingCommand> Entries { get; }

        /// <summary>Adds the entry, or replaces the entry with the same request id.</summary>
        void Record(PendingCommand command);

        /// <summary>Removes the entry with this request id; unknown ids are ignored.</summary>
        void Remove(string requestId);

        /// <summary>Entries skipped at load (damaged, unknown format); null when everything loaded.</summary>
        string? LoadProblem { get; }

        /// <summary>The last write failure; null after a successful write.</summary>
        string? WriteProblem { get; }
    }

    /// <summary>Entries in memory only: for tests, or for a game that wants the API without surviving an app restart.</summary>
    public sealed class InMemoryPendingCommandStore : IPendingCommandStore
    {
        private readonly PendingCommandList _entries = new PendingCommandList();

        public IReadOnlyList<PendingCommand> Entries => _entries.Items;

        public string? LoadProblem => null;

        public string? WriteProblem => null;

        public void Record(PendingCommand command) => _entries.Put(command ?? throw new ArgumentNullException(nameof(command)));

        public void Remove(string requestId) => _entries.Remove(requestId);
    }

    /// <summary>
    /// One file per entry in <see cref="Folder"/> (contracts/sdk-pending-store.md): <c>{requestId}.pending</c>, written atomically (temp
    /// file, flush to the device, rename), so a crash leaves the entry whole or absent; a damaged entry is skipped, deleted and named
    /// in <see cref="LoadProblem"/> while the others load. File names come only from validated request ids, never from server data.
    /// </summary>
    /// <remarks>
    /// Synchronous I/O on the calling thread (the Unity main thread): one small write, one device flush and one rename per command,
    /// one delete when it is answered. Use one folder per server environment and per client. Not encrypted: an entry holds the
    /// command body, the route, the player id and the request id, never the session token.
    /// </remarks>
    public sealed class PersistentPendingCommandStore : IPendingCommandStore
    {
        internal const string EntrySuffix = ".pending";

        private const string TempSuffix = EntrySuffix + ".tmp";

        private readonly CacheFileOps _ops;
        private readonly PendingCommandList _entries = new PendingCommandList();

        /// <summary>Creates the folder when missing and loads the stored entries. Never throws for file problems.</summary>
        public PersistentPendingCommandStore(string folder)
            : this(folder, CacheFileOps.Real)
        {
        }

        internal PersistentPendingCommandStore(string folder, CacheFileOps ops)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new ArgumentException("The store folder must be a path.", nameof(folder));
            }

            _ops = ops ?? throw new ArgumentNullException(nameof(ops));
            Folder = folder;
            Load();
        }

        public string Folder { get; }

        public IReadOnlyList<PendingCommand> Entries => _entries.Items;

        public string? LoadProblem { get; private set; }

        public string? WriteProblem { get; private set; }

        public void Record(PendingCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            // Memory first: the client can list and resend the entry even when the disk refuses it.
            _entries.Put(command);
            var parts = PendingCommandFormat.Serialize(command, out var problem);
            if (parts == null)
            {
                WriteProblem = problem;
                return;
            }

            var final = EntryPath(command.RequestId);
            var temp = final + ".tmp";
            try
            {
                using (var stream = _ops.OpenTemp(temp))
                {
                    foreach (var part in parts)
                    {
                        _ops.Write(stream, part);
                    }

                    _ops.Flush(stream);
                }

                // A new id is a rename; the same id recorded again replaces its file in one step.
                if (_ops.Exists(final))
                {
                    _ops.Replace(temp, final);
                }
                else
                {
                    _ops.Move(temp, final);
                }

                WriteProblem = null;
            }
            catch (Exception ex) when (CacheFileOps.IsFileProblem(ex))
            {
                WriteProblem = $"not saved: {ex.GetType().Name}: {ex.Message}";
                DeleteQuietly(temp);
            }
        }

        public void Remove(string requestId)
        {
            if (!_entries.Remove(requestId) || !PendingCommandFormat.IsLowercaseUuid(requestId))
            {
                return;
            }

            // No flush: a delete lost by a power cut brings the entry back, and resending it replays (safe).
            try
            {
                var path = EntryPath(requestId);
                if (_ops.Exists(path))
                {
                    _ops.Delete(path);
                }

                WriteProblem = null;
            }
            catch (Exception ex) when (CacheFileOps.IsFileProblem(ex))
            {
                WriteProblem = $"not removed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private string EntryPath(string requestId) => Path.Combine(Folder, requestId + EntrySuffix);

        private void Load()
        {
            var problems = new List<string>();
            try
            {
                _ops.CreateDirectory(Folder);

                // A temp file is a Record that never reached its rename: the command was never sent, so it is dropped unread.
                foreach (var path in _ops.EnumerateFiles(Folder, "*" + TempSuffix))
                {
                    if (StemOf(Path.GetFileName(path), TempSuffix) != null)
                    {
                        DeleteQuietly(path);
                    }
                }

                foreach (var path in _ops.EnumerateFiles(Folder, "*" + EntrySuffix))
                {
                    var name = Path.GetFileName(path);

                    // Windows matches the pattern case-insensitively (VERIFY V9): only an exact entry name is ours to read or delete.
                    var requestId = StemOf(name, EntrySuffix);
                    if (requestId == null)
                    {
                        problems.Add($"{name}: not an entry file name");
                        continue;
                    }

                    var entry = Read(path, requestId, out var problem);
                    if (entry == null)
                    {
                        problems.Add($"{name}: {problem}");
                        DeleteQuietly(path);
                        continue;
                    }

                    _entries.Put(entry);
                }

                LoadProblem = problems.Count == 0 ? null : string.Join("; ", problems);
            }
            catch (Exception ex) when (CacheFileOps.IsFileProblem(ex))
            {
                _entries.Clear();
                LoadProblem = "unreadable: " + ex.GetType().Name;
            }
        }

        private PendingCommand? Read(string path, string requestId, out string? problem)
        {
            try
            {
                return PendingCommandFormat.Parse(_ops.ReadAll(path), requestId, out problem);
            }
            catch (Exception ex) when (CacheFileOps.IsFileProblem(ex))
            {
                problem = "unreadable: " + ex.GetType().Name;
                return null;
            }
        }

        /// <summary>The request id of an exact entry (or temp) file name: a lowercase UUID followed by <paramref name="suffix"/> (ordinal).</summary>
        private static string? StemOf(string name, string suffix)
        {
            if (name.Length != PendingCommandFormat.RequestIdLength + suffix.Length || !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return null;
            }

            var stem = name.Substring(0, PendingCommandFormat.RequestIdLength);
            return PendingCommandFormat.IsLowercaseUuid(stem) ? stem : null;
        }

        private void DeleteQuietly(string path)
        {
            try
            {
                if (_ops.Exists(path))
                {
                    _ops.Delete(path);
                }
            }
            catch (Exception ex) when (CacheFileOps.IsFileProblem(ex))
            {
                // Best effort: a leftover temp file is deleted at the next load, a damaged entry is skipped again.
            }
        }
    }

    /// <summary>Entries ordered by recorded time, then request id (ordinal); one entry per request id.</summary>
    internal sealed class PendingCommandList
    {
        private static readonly IReadOnlyList<PendingCommand> NoItems = Array.AsReadOnly(Array.Empty<PendingCommand>());

        private readonly List<PendingCommand> _items = new List<PendingCommand>();
        private IReadOnlyList<PendingCommand> _snapshot = NoItems;

        /// <summary>
        /// A read-only snapshot taken after the last change: a Record, Remove or Discard during a <c>foreach</c> over it (e.g. a
        /// resend loop) never changes the list being enumerated, and a caller cannot write into it (it is no array; T082 round 2,
        /// m6). Read it again for the current entries.
        /// </summary>
        public IReadOnlyList<PendingCommand> Items => _snapshot;

        public void Put(PendingCommand command)
        {
            RemoveItem(command.RequestId);
            var index = 0;
            while (index < _items.Count && Compare(_items[index], command) <= 0)
            {
                index++;
            }

            _items.Insert(index, command);
            _snapshot = Array.AsReadOnly(_items.ToArray());
        }

        /// <summary>True when an entry with <paramref name="requestId"/> was removed.</summary>
        public bool Remove(string? requestId)
        {
            if (!RemoveItem(requestId))
            {
                return false;
            }

            _snapshot = Array.AsReadOnly(_items.ToArray());
            return true;
        }

        public void Clear()
        {
            _items.Clear();
            _snapshot = NoItems;
        }

        private bool RemoveItem(string? requestId)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].RequestId, requestId, StringComparison.Ordinal))
                {
                    _items.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private static int Compare(PendingCommand a, PendingCommand b)
        {
            var byTime = a.RecordedAtServerUtc.CompareTo(b.RecordedAtServerUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.RequestId, b.RequestId);
        }
    }
}
