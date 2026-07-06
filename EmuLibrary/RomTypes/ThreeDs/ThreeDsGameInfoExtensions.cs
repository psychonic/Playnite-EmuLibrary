using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.ThreeDs
{
    internal static class ThreeDsGameExtensions
    {
        static public ThreeDsGameInfo GetThreeDsGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<ThreeDsGameInfo>(game);
        }
    }
}
