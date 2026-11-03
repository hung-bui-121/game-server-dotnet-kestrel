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
        /// build that talks to several servers (dev, beta, live) never mixes their configs. Call it on the main thread.
        /// </summary>
        /// <param name="environmentName">A plain folder name such as <c>local</c> or <c>live</c> (no path separators, not <c>..</c>).</param>
        public static PersistentConfigCache Create(string environmentName)
        {
            if (string.IsNullOrWhiteSpace(environmentName)
                || environmentName == "."
                || environmentName == ".."
                || environmentName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || environmentName.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                throw new ArgumentException($"'{environmentName}' is not a plain folder name.", nameof(environmentName));
            }

            return new PersistentConfigCache(Path.Combine(Application.persistentDataPath, "gameserver-config", environmentName));
        }
    }
}
#endif
