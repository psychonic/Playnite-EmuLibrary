using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // Installed state is derived from disk each scan, so uninstall just removes what was placed at install
    // time. A LooseFile title deletes the copied bootable file from DestinationPath; a content-store title
    // deletes the SD title dirs for base + update + DLC.
    class ThreeDsUninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly ThreeDsGameInfo _gameInfo;

        internal ThreeDsUninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;
            _gameInfo = game.GetThreeDsGameInfo();
            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            if (_gameInfo.BaseKind == ThreeDsBaseKind.ContentStore)
            {
                UninstallContentStore();
                Game.Roms?.Clear();
                InvokeOnUninstalled(new GameUninstalledEventArgs());
                return;
            }

            var rom = Game.Roms?.FirstOrDefault();
            if (rom != null)
            {
                var romPathResolved = rom.Path.Replace(ExpandableVariables.PlayniteDirectory, _emuLibrary.Playnite.Paths.ApplicationPath);
                if (File.Exists(romPathResolved))
                    File.Delete(romPathResolved);
                else
                    _emuLibrary.Playnite.Dialogs.ShowMessage($"\"{Game.Name}\" does not appear to be installed. Marking as uninstalled.", "Game not installed", MessageBoxButton.OK);
            }

            Game.Roms?.Clear();
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }

        private void UninstallContentStore() =>
            RemoveContentStoreTitleDirs(_gameInfo.Mapping, _gameInfo.TitleId, _emuLibrary.Logger);

        // Removes the SD title dirs for the base and its derived update (0004000E) and DLC (0004008C) ids.
        // Best-effort: a missing dir just means that piece was never installed. Shared with the install
        // controller's partial-install cleanup so a canceled/failed content-store import leaves nothing behind.
        internal static void RemoveContentStoreTitleDirs(Settings.EmulatorMapping mapping, ulong baseTitleId, ILogger logger)
        {
            if (mapping == null)
                throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            var paths = AzaharPaths.Resolve(mapping.EmulatorBasePathResolved);

            ulong updateId = baseTitleId | (0x000EUL << 32);
            ulong dlcId = baseTitleId | (0x008CUL << 32);

            foreach (var id in new[] { baseTitleId, updateId, dlcId })
            {
                try
                {
                    var dir = paths.TitleDir(id);
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"[3DS] Failed to remove SD title dir for {id:X16}.");
                }
            }
        }
    }
}
