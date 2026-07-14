using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.IO;

namespace EmuLibrary.RomTypes.Vita
{
    // Platform-specific uninstall (not abstracted, as with the other composite RomTypes). The ux0 tree is
    // shared/user-owned, so this removes only what we installed — the title's ux0/app/<id>, ux0/patch/<id>,
    // ux0/addcont/<id> (PSN DLC) and ux0/license/<id> (PSN licenses) dirs, all keyed by title id and
    // entirely ours. Save data (ux0/user) is NEVER touched.
    class VitaUninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly VitaGameInfo _gameInfo;

        internal VitaUninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;
            _gameInfo = game.GetVitaGameInfo();
            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var mapping = _gameInfo.Mapping;
            if (mapping != null && !string.IsNullOrEmpty(_gameInfo.TitleId))
            {
                DeleteDirectoryIfExists(VitaEmulator.GetAppDir(mapping, _gameInfo.TitleId));
                DeleteDirectoryIfExists(VitaEmulator.GetPatchDir(mapping, _gameInfo.TitleId));
                DeleteDirectoryIfExists(VitaEmulator.GetAddContDir(mapping, _gameInfo.TitleId));
                DeleteDirectoryIfExists(VitaEmulator.GetLicenseDir(mapping, _gameInfo.TitleId));
            }

            Game.Roms?.Clear();
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
