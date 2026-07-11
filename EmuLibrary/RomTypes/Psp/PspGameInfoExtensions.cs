using Playnite.SDK.Models;

namespace EmuLibrary.RomTypes.Psp
{
    internal static class PspGameInfoExtensions
    {
        public static PspGameInfo GetPspGameInfo(this Game game)
        {
            return ELGameInfo.FromGame<PspGameInfo>(game);
        }
    }
}
