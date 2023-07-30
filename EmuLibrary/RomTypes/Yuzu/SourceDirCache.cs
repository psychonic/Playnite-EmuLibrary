using EmuLibrary.Settings;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EmuLibrary.RomTypes.Yuzu
{
    // TODO: this whole class is crusty and busted, and barely even caches during runtime, let alone persistently. It also caches
    // some information about installed games, making it not only relevant to the "SourceDir". Leaving as-is for now since it was
    // functional enough in the separate YuzuLibrary extension before folding into here
    //
    // Full rewrite is necessary, keeping in mind caching needs for this RomType as well as others that benefit from a cache
    //
    // For this RomType and some others, we source some of our necessary data from the file content, rather than just the file name.
    // Combined with them being large files, often at a remote location (best case local network over SMB, worst case on future
    // supported alternative storage type), perf can get bad
    //
    // If all of the necessary data is static, consider showing into YuzuGameInfo
    internal class SourceDirCache
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly EmulatorMapping _mapping;
        private readonly string _configPath;
        public bool IsLoaded { get; private set; }

        public Cache TheCache { get; private set; }

        public bool IsDirty { get; private set; }
        public void MarkDirty() => IsDirty = true;

        public SourceDirCache(IEmuLibrary emuLibrary, EmulatorMapping mapping)
        {
            _emuLibrary = emuLibrary;
            _mapping = mapping;
            _configPath = Path.Combine(_emuLibrary.GetPluginUserDataPath(), "sourceCache.json");

            IsLoaded = false;
            IsDirty = true;

            Load();
        }

        public void Clear()
        {
            TheCache = new Cache();
            MarkDirty();
        }

        private void Load()
        {
            if (File.Exists(_configPath))
            {
                TheCache = JsonConvert.DeserializeObject<Cache>(File.ReadAllText(_configPath));
            }
            else
            {
                TheCache = new Cache();
                _emuLibrary.Logger.Warn("[CACHE] _cache file not found");
            }

            IsLoaded = true;
        }

        public void Save()
        {
            File.WriteAllText(_configPath, JsonConvert.SerializeObject(TheCache, Formatting.Indented));
        }

        public void Refresh(CancellationToken tk)
        {
            var yuzu = new Yuzu(_mapping.EmulatorBasePathResolved, _emuLibrary.Logger);

            var igs = yuzu.GetInstalledGames(tk);
            foreach (var ig in igs)
            {
                TheCache.InstalledGames[ig.TitleId] = ig;
            }

            _emuLibrary.Logger.Info("Finished cache refresh of installed games");

            var ugs = yuzu.GetUninstalledGamesFromDir(_mapping.SourcePath, tk);
            foreach (var ug in ugs)
            {
                TheCache.UninstalledGames[ug.TitleId] = ug;
            }

            _emuLibrary.Logger.Info("Finished cache refresh of uninstalled games");

            IsDirty = false;
        }

        public class Cache
        {
            public Dictionary<ulong, CacheGameInstalled> InstalledGames { get; private set; }
            public Dictionary<ulong, CacheGameUninstalled> UninstalledGames { get; private set; }

            public Cache()
            {
                InstalledGames = new Dictionary<ulong, CacheGameInstalled>();
                UninstalledGames = new Dictionary<ulong, CacheGameUninstalled>();
            }
        }

        public class CacheGameBase
        {
            public ulong TitleId { get; set; }

            public string Title { get; set; }
            public string Publisher { get; set; }
            public string Version { get; set; }
        }

        public class CacheGameUninstalled : CacheGameBase
        {
            // Can be XCI or NSP
            public string ProgramFile { get; set; }

            // What was this for? Just sorting to take best before saving to cache?
            //[JsonIgnore]
            //List<string> AllProgramFiles { get; set; }

            // Zero or one
            public string UpdateFile { get; set; }

            // What was this for? Just sorting to take best before saving to cache?
            //[JsonIgnore]
            //List<string> AllUpdateFiles { get; set; }

            // Zero or many
            public List<string> DlcFiles { get; set; }

            public CacheGameUninstalled()
            {
                DlcFiles = new List<string>();
            }
        }

        public class CacheGameInstalled : CacheGameBase
        {
            public string ProgramNcaSubPath { get; set; }
        }
    }
}
