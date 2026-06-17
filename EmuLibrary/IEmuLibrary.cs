using EmuLibrary.RomTypes;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.ScanConcurrency;
using Playnite.SDK;

namespace EmuLibrary
{
    internal interface IEmuLibrary
    {
        ILogger Logger { get; }
        IPlayniteAPI Playnite { get; }
        Settings.Settings Settings { get; }
        IScanCache ScanCache { get; }
        IScanConcurrency ScanConcurrency { get; }
        string GetPluginUserDataPath();
        RomTypeScanner GetScanner(RomType romType);
    }
}
