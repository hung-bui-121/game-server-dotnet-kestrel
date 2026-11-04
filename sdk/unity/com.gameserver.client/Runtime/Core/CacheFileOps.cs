#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace GameServer.Client
{
    /// <summary>
    /// The file operations of <see cref="PersistentConfigCache"/> (FG-019) and <see cref="PersistentPendingCommandStore"/> (FG-022):
    /// the real file system by default; tests derive from it to fail one step (open, write, flush, replace, move, delete, read,
    /// enumerate) and check that the store keeps "old, new or empty".
    /// </summary>
    internal class CacheFileOps
    {
        public static CacheFileOps Real { get; } = new CacheFileOps();

        /// <summary>The exceptions a disk, a permission or a path problem raises: kept as a problem text, never thrown to the game.</summary>
        public static bool IsFileProblem(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is SecurityException;

        public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public virtual bool Exists(string path) => File.Exists(path);

        public virtual byte[] ReadAll(string path) => File.ReadAllBytes(path);

        /// <summary>Creates or truncates the temp file for writing.</summary>
        public virtual Stream OpenTemp(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        public virtual void Write(Stream stream, byte[] buffer) => stream.Write(buffer, 0, buffer.Length);

        /// <summary>Flushes to the device, not only to the OS cache, so a crash after the replace never leaves a short file.</summary>
        public virtual void Flush(Stream stream)
        {
            if (stream is FileStream file)
            {
                file.Flush(flushToDisk: true);
            }
            else
            {
                stream.Flush();
            }
        }

        /// <summary>Replaces <paramref name="destination"/> by <paramref name="source"/> in one rename (V10).</summary>
        public virtual void Replace(string source, string destination) => File.Replace(source, destination, null);

        public virtual void Move(string source, string destination) => File.Move(source, destination);

        public virtual void Delete(string path) => File.Delete(path);

        /// <summary>
        /// Full paths of the files of <paramref name="folder"/> matching <paramref name="pattern"/> (FG-022). Windows matches
        /// case-insensitively, so callers check each name exactly before using it.
        /// </summary>
        public virtual IEnumerable<string> EnumerateFiles(string folder, string pattern) => Directory.EnumerateFiles(folder, pattern);
    }
}
