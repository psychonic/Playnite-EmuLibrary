using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.Vita
{
    internal static class VitaGameInfoExtensions
    {
        public static VitaGameInfo GetVitaGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<VitaGameInfo>(game);
        }
    }
}
