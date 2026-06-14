using EmuLibrary.Settings;
using System.Collections.Generic;
using System.Threading;

namespace EmuLibrary.RomTypes.Yuzu
{
    internal class SourceDirCache
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly EmulatorMapping _mapping;

        public bool IsLoaded { get; private set; }
        public Cache TheCache { get; private set; }
        public bool IsDirty { get; private set; }
        public void MarkDirty() => IsDirty = true;

        public SourceDirCache(IEmuLibrary emuLibrary, EmulatorMapping mapping)
        {
            _emuLibrary = emuLibrary;
            _mapping = mapping;

            IsLoaded = false;
            IsDirty = true;
            TheCache = new Cache();
        }

        public void Clear()
        {
            TheCache = new Cache();
            MarkDirty();
        }

        public void Refresh(CancellationToken tk)
        {
            var yuzu = new Yuzu(_mapping.EmulatorBasePathResolved, _mapping.SwitchEmulator, _emuLibrary.Logger, _emuLibrary.ScanCache);

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

            // Zero or one
            public string UpdateFile { get; set; }

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
