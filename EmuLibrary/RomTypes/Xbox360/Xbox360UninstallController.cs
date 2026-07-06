using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.Xbox360
{
    // Platform-specific uninstall (not abstracted, as with the other composite RomTypes). Removes the base copy from DestinationPath and the
    // title's update/DLC files under the Xenia content tree. Deletes the whole 000B0000 (updates) dir — only
    // our TUs live there — but only the specific DLC files we wrote (00000002 may hold other titles' content),
    // and never the whole {TitleId} dir (saves live under the same TitleId via a real XUID).
    class Xbox360UninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly Xbox360GameInfo _gameInfo;
        private readonly Xbox360Scanner _scanner;

        internal Xbox360UninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;
            _gameInfo = game.GetXbox360GameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Xbox360) as Xbox360Scanner;
            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var mapping = _gameInfo.Mapping;

            // Delete the copied base .iso from DestinationPath.
            DeleteFileIfExists(ResolvePath(Game.Roms?.FirstOrDefault()?.Path));

            // Re-derive the composite to know which update/DLC files were written, then remove them from the
            // content tree. Best-effort: if the source has gone, we can still have deleted the base copy above.
            if (mapping != null)
            {
                var contentRoot = XeniaEmulator.GetContentRoot(mapping);
                if (contentRoot != null)
                {
                    var title = TryBuildTitle(mapping);
                    if (title != null)
                    {
                        // Updates live alone under 000B0000 — drop the whole dir.
                        foreach (var update in title.Updates)
                            DeleteDirectoryIfExists(XeniaEmulator.GetContentTypeDir(contentRoot, update.TitleId, update.ContentType));

                        // DLC shares 00000002 with other titles — delete only the files we wrote.
                        foreach (var dlc in title.DlcRefs)
                            DeleteFileIfExists(Path.Combine(
                                XeniaEmulator.GetContentTypeDir(contentRoot, dlc.TitleId, dlc.ContentType), dlc.ContentName));
                    }
                }
            }

            Game.Roms?.Clear();
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }

        private Xbox360Title TryBuildTitle(Settings.EmulatorMapping mapping)
        {
            try
            {
                return _scanner?.BuildTitle(mapping, _gameInfo.TitleId, _gameInfo.MediaId, _gameInfo.SourceName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[X360] Could not re-derive \"{Game.Name}\" to clean its content-tree files on uninstall.");
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
