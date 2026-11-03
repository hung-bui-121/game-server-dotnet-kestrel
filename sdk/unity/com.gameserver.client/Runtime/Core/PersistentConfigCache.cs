#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace GameServer.Client
{
    /// <summary>
    /// <see cref="IConfigCache"/> that keeps the last client config version and its files on disk (FG-019): available before login
    /// at the next app start. One container file in <see cref="Folder"/>; saves are atomic (write a temp file, flush it to the
    /// device, then replace the old file in one rename), so a crash leaves the old or the new version, never a mix; a damaged
    /// file reads as empty. Config file names from the server stay strings inside the container and never become paths.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: use it from the thread the client runs on (the Unity main thread). All I/O is synchronous on that thread;
    /// a 1 MiB load or save takes a few milliseconds on a local disk (contracts/sdk-cache.md §6). Use one folder per server
    /// environment. Not encrypted: it holds client-visible config only.
    /// </remarks>
    public sealed class PersistentConfigCache : IConfigCache
    {
        public const string FileName = "gameserver-config.bin";

        private const string TempSuffix = ".tmp";

        /// <summary>Longest cache name <c>UnityConfigCache.Create</c> accepts (U1).</summary>
        private const int MaxFolderNameLength = 64;

        private static readonly IReadOnlyDictionary<string, byte[]> NoFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        private readonly CacheFileOps _ops;
        private readonly string _main;
        private readonly string _temp;

        // True when the main file holds Version: an identical Store is skipped only then (a version read from the temp file is
        // written again, which promotes it).
        private bool _persisted;

        /// <summary>Creates the folder when missing and loads the stored version. Never throws for file problems.</summary>
        public PersistentConfigCache(string folder)
            : this(folder, CacheFileOps.Real)
        {
        }

        internal PersistentConfigCache(string folder, CacheFileOps ops)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new ArgumentException("The cache folder must be a path.", nameof(folder));
            }

            _ops = ops ?? throw new ArgumentNullException(nameof(ops));
            Folder = folder;
            _main = Path.Combine(folder, FileName);
            _temp = _main + TempSuffix;
            Load();
        }

        public string Folder { get; }

        public string? Version { get; private set; }

        public IReadOnlyDictionary<string, byte[]> Files { get; private set; } = NoFiles;

        /// <summary>Why the cache started empty (truncated, corrupt, unknown format, unreadable); null when it loaded or no file existed.</summary>
        public string? LoadProblem { get; private set; }

        /// <summary>The last save failure (disk full, permissions); null after a successful save. The data stays in memory.</summary>
        public string? WriteProblem { get; private set; }

        /// <summary>Uses the new version in memory at once, then writes it atomically; never throws because of the disk.</summary>
        public void Store(string version, IReadOnlyDictionary<string, byte[]> files)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            if (_persisted && version == Version && SameFiles(files))
            {
                return;
            }

            // Memory first: the client uses the new files even when the disk refuses them.
            var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                snapshot[file.Key] = file.Value ?? throw new ArgumentException($"Config file '{file.Key}' has no content.", nameof(files));
            }

            Version = version;
            Files = snapshot;
            _persisted = false;
            Save();
        }

        private static bool IsFileProblem(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is SecurityException;

        private void Load()
        {
            try
            {
                _ops.CreateDirectory(Folder);
                var main = Read(_main, out var mainProblem);
                var stored = main ?? Read(_temp, out _);
                if (stored == null)
                {
                    LoadProblem = mainProblem;
                    return;
                }

                Version = stored.Value.Version;
                Files = stored.Value.Files;
                _persisted = main != null;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                LoadProblem = "unreadable: " + ex.GetType().Name;
            }
        }

        /// <summary>The container at <paramref name="path"/>; null when absent (problem null) or unusable (problem set).</summary>
        private (string Version, Dictionary<string, byte[]> Files)? Read(string path, out string? problem)
        {
            problem = null;
            try
            {
                return _ops.Exists(path) ? ConfigCacheFormat.Parse(_ops.ReadAll(path), out problem) : null;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                problem = "unreadable: " + ex.GetType().Name;
                return null;
            }
        }

        private void Save()
        {
            var parts = ConfigCacheFormat.Serialize(Version!, Files, out var problem);
            if (parts == null)
            {
                WriteProblem = problem;
                return;
            }

            try
            {
                using (var stream = _ops.OpenTemp(_temp))
                {
                    foreach (var part in parts)
                    {
                        _ops.Write(stream, part);
                    }

                    _ops.Flush(stream);
                }

                if (_ops.Exists(_main))
                {
                    _ops.Replace(_temp, _main);
                }
                else
                {
                    _ops.Move(_temp, _main);
                }

                WriteProblem = null;
                _persisted = true;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                WriteProblem = $"not saved: {ex.GetType().Name}: {ex.Message}";
                DeleteTemp();
            }
        }

        private void DeleteTemp()
        {
            try
            {
                if (_ops.Exists(_temp))
                {
                    _ops.Delete(_temp);
                }
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                // Best effort: a leftover temp file is ignored while the main file is valid, and rewritten by the next save.
            }
        }

        /// <summary>
        /// The cache-name rule of <c>UnityConfigCache.Create</c>, identical on every platform (slice 015 T071 U1): 1 to 64 characters
        /// of <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c> and <c>-</c>; not starting or ending with <c>.</c>; the part before the first
        /// <c>.</c> is not a Windows device name. Lowercase only, so case-insensitive (Windows, macOS) and case-sensitive (Android)
        /// file systems give the same folders. Never consults the running platform's invalid file-name characters.
        /// </summary>
        /// <returns>Null when <paramref name="name"/> is valid, otherwise the message of the <see cref="ArgumentException"/>.</returns>
        internal static string? FolderNameProblem(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return IsValidFolderName(name)
                ? null
                : $"'{name}' is not a valid config cache name. Fix: use 1 to {MaxFolderNameLength} lowercase letters, digits, '.', '_' or '-', " +
                  "not starting or ending with '.' and not a Windows device name (con, nul, com1, …), for example 'local', 'live' or 'localhost-5080'.";
        }

        private static bool IsValidFolderName(string name)
        {
            if (name.Length < 1 || name.Length > MaxFolderNameLength || name[0] == '.' || name[name.Length - 1] == '.')
            {
                return false;
            }

            foreach (var c in name)
            {
                var allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            var dot = name.IndexOf('.');
            var stem = dot < 0 ? name : name.Substring(0, dot);
            return !IsWindowsDeviceName(stem);
        }

        /// <summary><c>con</c>, <c>prn</c>, <c>aux</c>, <c>nul</c>, <c>com1</c>–<c>com9</c>, <c>lpt1</c>–<c>lpt9</c> (the Windows Editor cannot create them).</summary>
        private static bool IsWindowsDeviceName(string stem)
        {
            if (stem == "con" || stem == "prn" || stem == "aux" || stem == "nul")
            {
                return true;
            }

            return stem.Length == 4
                && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal))
                && stem[3] >= '1' && stem[3] <= '9';
        }

        private bool SameFiles(IReadOnlyDictionary<string, byte[]> files) =>
            files.Count == Files.Count
            && files.All(f => f.Value != null && Files.TryGetValue(f.Key, out var mine) && mine.AsSpan().SequenceEqual(f.Value));
    }
}
