using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.Ps3
{
    internal static class Ps3GameInfoExtensions
    {
        public static Ps3GameInfo GetPs3GameInfo(this Game game)
        {
            return ELGameInfo.FromGame<Ps3GameInfo>(game);
        }
    }
}
