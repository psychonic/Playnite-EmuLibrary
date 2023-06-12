using EmuLibrary.RomTypes;
using Playnite.SDK.Models;

internal static class ELGameInfoBaseExtensions
{
    static public ELGameInfo GetELGameInfo(this Game game)
    {
        return ELGameInfo.FromGame<ELGameInfo>(game);
    }
}