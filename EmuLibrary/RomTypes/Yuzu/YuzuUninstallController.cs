using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace EmuLibrary.RomTypes.Yuzu
{
    class YuzuUninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly SourceDirCache _cache;
        private readonly YuzuGameInfo _gameInfo;

        public YuzuUninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            _emuLibrary = emuLibrary;

            _gameInfo = game.GetYuzuGameInfo();
            _cache = (_emuLibrary.GetScanner(RomType.Yuzu) as YuzuScanner).GetCacheForMapping(_gameInfo.MappingId);

            Name = string.Format("Uninstall from {0}", _gameInfo.Mapping.Emulator?.Name ?? "Emulator");
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var yuzu = new Yuzu(_gameInfo.Mapping.EmulatorBasePathResolved, _emuLibrary.Logger);
            yuzu.UninstallTitleFromNand(Game.GameId);
            _cache.TheCache.InstalledGames.Remove(_gameInfo.TitleId);

            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }
    }
}
