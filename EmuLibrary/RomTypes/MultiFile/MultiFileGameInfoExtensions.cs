using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.MultiFile
{
    internal static class MultiFileGameInfoExtensions
    {
        static public MultiFileGameInfo GetMultiFileGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<MultiFileGameInfo>(game);
        }
    }
}
