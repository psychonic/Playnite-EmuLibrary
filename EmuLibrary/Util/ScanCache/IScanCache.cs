using System.Collections.Generic;

namespace EmuLibrary.Util.ScanCache
{
    // Persistent, file-identity-keyed metadata cache. Swappable backing (JSON now, P11 SQLite later).
    internal interface IScanCache
    {
        // True only if an entry exists for `key`, its stored stamp matches `stamp`, AND it was stored as T.
        bool TryGet<T>(string key, FileStamp stamp, out T value) where T : class;

        // Store/overwrite the entry for `key` with the given stamp and value.
        void Set<T>(string key, FileStamp stamp, T value) where T : class;

        // Remove all entries whose key falls under directoryPrefix but is not in visitedKeys.
        // Only call after a completed (non-cancelled) scan of the directory.
        void RemoveKeysUnder(string directoryPrefix, ISet<string> visitedKeys);

        // Drop every cached entry and persist the now-empty cache to disk.
        void Clear();

        // Persist to disk if anything changed since last flush. Safe to call when nothing changed.
        void Flush();
    }
}
