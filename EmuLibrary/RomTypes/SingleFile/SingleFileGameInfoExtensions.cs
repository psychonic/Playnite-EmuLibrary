using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.SingleFile
{
    internal static class SingleFileGameExtensions
    {
        static public SingleFileGameInfo GetSingleFileGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<SingleFileGameInfo>(game);
        }
    }
}
