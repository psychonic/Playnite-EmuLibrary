using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.Psp
{
    // Platform-specific uninstall (not abstracted, as with the other composite RomTypes). The memory-stick tree
    // is shared/user-owned, so this removes only what we installed: the copied base image from DestinationPath and
    // the title's GAME payload dir under the memory stick. A GAME payload dir (PSP/GAME/<titleId>) is entirely
    // ours and is removed whole; a SAVEDATA dir is NEVER rm -rf'd (it may hold real user saves).
    class PspUninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly PspGameInfo _gameInfo;
        private readonly PspScanner _scanner;

        internal PspUninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;
            _gameInfo = game.GetPspGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Psp) as PspScanner;
            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var mapping = _gameInfo.Mapping;

            // Delete the copied base image from DestinationPath.
            DeleteFileIfExists(ResolvePath(Game.Roms?.FirstOrDefault()?.Path));

            // Re-derive the composite to know which payload folders were installed, then remove them from the
            // memory stick. Best-effort: if the source has gone we can still have deleted the base copy above.
            if (mapping != null)
            {
                var title = TryBuildTitle(mapping);
                if (title != null)
                {
                    foreach (var item in title.Updates.Concat(title.Dlcs))
                    {
                        // Only GAME payload dirs are entirely ours. Never delete a SAVEDATA dir (user saves).
                        if (item.Route != PspRoute.Game)
                            continue;
                        DeleteDirectoryIfExists(PpssppEmulator.GetContentDir(mapping, item));
                    }
                }
            }

            Game.Roms?.Clear();
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }

        private PspTitle TryBuildTitle(Settings.EmulatorMapping mapping)
        {
            try
            {
                return _scanner?.BuildTitle(mapping, _gameInfo.SourceFullPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[PSP] Could not re-derive \"{Game.Name}\" to clean its memory-stick payload on uninstall.");
                return null;
            }
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path.Replace(ExpandableVariables.PlayniteDirectory, _emuLibrary.Playnite.Paths.ApplicationPath);
        }

        private static void DeleteFileIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
