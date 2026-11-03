using System.Runtime.CompilerServices;

// The SDK tests outside Unity inject file faults into PersistentConfigCache through the internal CacheFileOps seam (FG-019).
[assembly: InternalsVisibleTo("GameServer.Client.Tests")]
