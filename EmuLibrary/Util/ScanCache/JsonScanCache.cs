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
        private const int CurrentFormatVersion = 1;

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

        public void Flush()
        {
            lock (_lock)
            {
                if (!_dirty)
                    return;
                try
                {
                    var store = new Store { Version = CurrentFormatVersion, Entries = _entries };
                    var json = JsonConvert.SerializeObject(store, Formatting.Indented);

                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json);
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
