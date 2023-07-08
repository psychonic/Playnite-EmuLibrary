using EmuLibrary.RomTypes;
using Playnite.SDK;

namespace EmuLibrary
{
    internal interface IEmuLibrary
    {
        ILogger Logger { get; }
        IPlayniteAPI Playnite { get; }
        Settings.Settings Settings { get; }
        string GetPluginUserDataPath();
        RomTypeScanner GetScanner(RomType romType);
    }
}
