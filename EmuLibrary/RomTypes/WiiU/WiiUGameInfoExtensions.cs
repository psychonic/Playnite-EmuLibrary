using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.WiiU
{
    internal static class WiiUGameExtensions
    {
        static public WiiUGameInfo GetWiiUGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<WiiUGameInfo>(game);
        }
    }
}
