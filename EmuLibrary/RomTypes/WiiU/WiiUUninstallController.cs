using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.WiiU
{
    // Installed state is derived from the destination on each scan, so uninstall just removes the per-title
    // destination folder (the merged .wua, or the copied .wux/loadiine tree). No NAND involvement.
    class WiiUUninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly WiiUGameInfo _gameInfo;

        public WiiUUninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;
            _gameInfo = game.GetWiiUGameInfo();
            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var mapping = _gameInfo.Mapping;
            var cemu = new Cemu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);

            var installed = cemu.GetInstalledTitles(mapping.DestinationPathResolved, CancellationToken.None)
                .FirstOrDefault(t => t.TitleId == _gameInfo.TitleId);

            if (installed != null && Directory.Exists(installed.InstalledPath))
                Directory.Delete(installed.InstalledPath, true);

            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }
    }
}
