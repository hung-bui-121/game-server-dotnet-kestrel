#if UNITY_5_3_OR_NEWER
#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace GameServer.Client
{
    /// <summary>Unity setup of the persistent pending-command store (FG-022).</summary>
    public static class UnityPendingCommandStore
    {
        /// <summary>
        /// <c>Application.persistentDataPath/gameserver-pending/{environmentName}</c>: one folder per server environment, so a build
        /// that talks to several servers (dev, beta, live) never resends a command to the wrong one. On iOS the folder is excluded
        /// from iCloud/iTunes backup (a restored backup must not resend old commands on another device). Use one client per
        /// folder and call it, and the client, on the Unity main thread (the store is not thread-safe).
        /// </summary>
        /// <param name="environmentName">
        /// The rule of <c>UnityConfigCache.Create</c>: 1 to 64 lowercase letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, not starting
        /// or ending with <c>.</c> and not a Windows device name (<c>con</c>, <c>nul</c>, <c>com1</c>, …), e.g. <c>local</c>, <c>live</c>
        /// or <c>localhost-5080</c>. Keep it in the game's settings; never derive it from the URL.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="environmentName"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="environmentName"/> breaks the rule above.</exception>
        public static PersistentPendingCommandStore Create(string environmentName)
        {
            if (environmentName == null)
            {
                throw new ArgumentNullException(nameof(environmentName));
            }

            var problem = PersistentConfigCache.FolderNameProblem(environmentName, "pending command store");
            if (problem != null)
            {
                throw new ArgumentException(problem, nameof(environmentName));
            }

            var folder = Path.Combine(Application.persistentDataPath, "gameserver-pending", environmentName);
            var store = new PersistentPendingCommandStore(folder);
#if UNITY_IOS && !UNITY_EDITOR
            // On the folder, not the files: every Record creates a new file, which would not carry a per-file flag.
            if (Directory.Exists(folder))
            {
                UnityEngine.iOS.Device.SetNoBackupFlag(folder);
            }
#endif
            return store;
        }
    }
}
#endif
