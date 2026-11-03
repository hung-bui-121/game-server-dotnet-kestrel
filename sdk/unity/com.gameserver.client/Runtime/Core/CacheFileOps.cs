#nullable enable
using System.IO;

namespace GameServer.Client
{
    /// <summary>
    /// The file operations of <see cref="PersistentConfigCache"/> (FG-019): the real file system by default; tests derive from
    /// it to fail one step (open, write, flush, replace, move, read) and check that the cache keeps "old, new or empty".
    /// </summary>
    internal class CacheFileOps
    {
        public static CacheFileOps Real { get; } = new CacheFileOps();

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
    }
}
