using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;

namespace EmuLibrary.RomTypes.Ps3
{
    class Ps3UninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly Ps3GameInfo _gameInfo;

        internal Ps3UninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;
            _gameInfo = game.GetPs3GameInfo();
            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var mapping = _gameInfo.Mapping;

            if (_gameInfo.BaseKind == Ps3BaseKind.Disc)
            {
                var romPath = ResolvePath(Game.Roms?.FirstOrDefault()?.Path);

                if (!string.IsNullOrEmpty(romPath) && romPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    // Delete the copied ISO and its .dkey/.key sidecar; leave other discs in the folder alone.
                    DeleteFileIfExists(romPath);
                    DeleteFileIfExists(Path.ChangeExtension(romPath, ".dkey"));
                    DeleteFileIfExists(Path.ChangeExtension(romPath, ".key"));
                }
                else
                {
                    // PS3_GAME folder base: InstallDirectory is the specific copied folder.
                    DeleteDirectoryIfExists(ResolvePath(Game.InstallDirectory));
                }
            }

            // Updates/DLC (and the pkg base, if any) live under dev_hdd0/game/<TITLE_ID>. Saves live under
            // dev_hdd0/home/<user>/savedata, NOT here, so deleting the game dir is safe.
            if (mapping != null && !string.IsNullOrEmpty(_gameInfo.TitleId))
            {
                DeleteDirectoryIfExists(Rpcs3Emulator.GetGameDir(mapping, _gameInfo.TitleId));
            }

            Game.Roms?.Clear();
            InvokeOnUninstalled(new GameUninstalledEventArgs());
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
