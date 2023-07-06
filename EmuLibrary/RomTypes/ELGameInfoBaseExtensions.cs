using EmuLibrary.RomTypes;
using Playnite.SDK.Models;

internal static class ELGameInfoBaseExtensions
{
    static public ELGameInfo GetELGameInfo(this Game game)
    {
        return ELGameInfo.FromGame<ELGameInfo>(game);
    }

    static public ELGameInfo GetELGameInfo(this GameMetadata game)
    {
        return ELGameInfo.FromGameMetadata<ELGameInfo>(game);
    }
}