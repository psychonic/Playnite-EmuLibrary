using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.Xbox360
{
    internal static class Xbox360GameInfoExtensions
    {
        public static Xbox360GameInfo GetXbox360GameInfo(this Game game)
        {
            return ELGameInfo.FromGame<Xbox360GameInfo>(game);
        }
    }
}
