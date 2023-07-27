using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.Yuzu
{
    internal static class YuzuGameExtensions
    {
        static public YuzuGameInfo GetYuzuGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<YuzuGameInfo>(game);
        }
    }
}
