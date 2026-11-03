#if UNITY_5_3_OR_NEWER
#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace GameServer.Client
{
    /// <summary>Unity setup of the persistent config cache (FG-019).</summary>
    public static class UnityConfigCache
    {
        /// <summary>
        /// <c>Application.persistentDataPath/gameserver-config/{environmentName}</c>: one folder per server environment, so a
        /// build that talks to several servers (dev, beta, live) never mixes their configs. On iOS the folder is excluded from
        /// iCloud/iTunes backup (the cache is re-downloadable). Call it on the main thread.
        /// </summary>
        /// <param name="environmentName">
        /// 1 to 64 lowercase letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, not starting or ending with <c>.</c> and not a Windows
        /// device name (<c>con</c>, <c>nul</c>, <c>com1</c>, …): the same rule on every platform, e.g. <c>local</c>, <c>live</c> or
        /// <c>localhost-5080</c>. Keep it in the game's settings; never derive it from the URL.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="environmentName"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> breaks the rule above.</exception>
        public static PersistentConfigCache Create(string environmentName)
        {
            if (environmentName == null)
            {
                throw new ArgumentNullException(nameof(environmentName));
            }

            var problem = PersistentConfigCache.FolderNameProblem(environmentName);
            if (problem != null)
            {
                throw new ArgumentException(problem, nameof(environmentName));
            }

            var folder = Path.Combine(Application.persistentDataPath, "gameserver-config", environmentName);
            var cache = new PersistentConfigCache(folder);
#if UNITY_IOS && !UNITY_EDITOR
            // On the folder, not the file: File.Replace creates a new file, which would lose a per-file flag (m8).
            if (Directory.Exists(folder))
            {
                UnityEngine.iOS.Device.SetNoBackupFlag(folder);
            }
#endif
            return cache;
        }
    }
}
#endif
