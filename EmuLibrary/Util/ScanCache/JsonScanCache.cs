using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.Util.ScanCache
{
    internal sealed class JsonScanCache : IScanCache
    {
        // Bump to invalidate ALL cached data after an incompatible change to entry shapes.
        // v2: added InstallSize (Yuzu ExternalGameFileInfo) and ExtractedSize (Ps3FileInfo).
        // v3: Ps3 caches a per-title install size (Ps3InstallSize); Ps3FileInfo no longer stores a manifest.
        // v4: Ps3Pkg read the AES-CTR IV from the wrong header offset (0x60 instead of 0x70), so every PKG
        //     scanned by an earlier build cached empty PARAM.SFO fields and a wrong classification. Bump to
        //     discard those poisoned entries and force a re-read with the corrected decrypt.
        private const int CurrentFormatVersion = 4;

        private sealed class Entry
        {
            public long Size { get; set; }
            public long MTicks { get; set; }
            public string TypeName { get; set; }
            public string Json { get; set; }
        }

        private sealed class Store
        {
            public int Version { get; set; }
            public Dictionary<string, Entry> Entries { get; set; }
        }

        private readonly string _path;
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries;
        private bool _dirty;

        public JsonScanCache(string path, ILogger logger)
        {
            _path = path;
            _logger = logger;
            _entries = Load(path, logger);
        }

        private static Dictionary<string, Entry> Load(string path, ILogger logger)
        {
            try
            {
                if (File.Exists(path))
                {
                    var store = JsonConvert.DeserializeObject<Store>(File.ReadAllText(path));
                    if (store != null && store.Version == CurrentFormatVersion && store.Entries != null)
                        return store.Entries;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[ScanCache] Failed to load cache at {path}; starting empty.");
            }
            return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGet<T>(string key, FileStamp stamp, out T value) where T : class
        {
            value = null;
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var e))
                    return false;
                if (e.Size != stamp.SizeBytes || e.MTicks != stamp.ModifiedUtcTicks)
                    return false;
                if (e.TypeName != typeof(T).FullName)
                    return false;
                try
                {
                    value = JsonConvert.DeserializeObject<T>(e.Json);
                    return value != null;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[ScanCache] Failed to deserialize entry for {key}; treating as miss.");
                    return false;
                }
            }
        }

        public void Set<T>(string key, FileStamp stamp, T value) where T : class
        {
            var json = JsonConvert.SerializeObject(value);
            lock (_lock)
            {
                _entries[key] = new Entry
                {
                    Size = stamp.SizeBytes,
                    MTicks = stamp.ModifiedUtcTicks,
                    TypeName = typeof(T).FullName,
                    Json = json,
                };
                _dirty = true;
            }
        }

        public void RemoveKeysUnder(string directoryPrefix, ISet<string> visitedKeys)
        {
            var prefix = directoryPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            var toRemove = new List<string>();
            lock (_lock)
            {
                foreach (var key in _entries.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !visitedKeys.Contains(key))
                        toRemove.Add(key);
                }
                if (toRemove.Count > 0)
                {
                    foreach (var key in toRemove)
                        _entries.Remove(key);
                    _dirty = true;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                if (_entries.Count == 0)
                    return;
                _entries.Clear();
                _dirty = true;
            }
            Flush();
        }

        public void Flush()
        {
            lock (_lock)
            {
                if (!_dirty)
                    return;
                try
                {
                    var store = new Store { Version = CurrentFormatVersion, Entries = _entries };

                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    // Stream straight to the file. Serializing the whole store to a single string first
                    // builds one giant (Large Object Heap) string, which can OutOfMemory on a large cache.
                    var serializer = new JsonSerializer();
                    var tmp = _path + ".tmp";
                    using (var sw = new StreamWriter(tmp, false))
                    using (var jw = new JsonTextWriter(sw))
                        serializer.Serialize(jw, store);

                    if (File.Exists(_path))
                        File.Replace(tmp, _path, null);
                    else
                        File.Move(tmp, _path);

                    _dirty = false;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"[ScanCache] Failed to persist cache to {_path}.");
                }
            }
        }
    }
}
