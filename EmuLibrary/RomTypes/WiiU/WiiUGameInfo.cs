using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.WiiU
{
    [ProtoContract]
    internal sealed class WiiUGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.WiiU;

        // Base title id (e.g. 0x0005000010102000). Like Yuzu, this is the only locator persisted: the
        // composite (base + latest update + DLC, across whatever source formats) is re-derived from the
        // mapping's SourcePath at scan and install time, never serialized.
        [ProtoMember(1)]
        public ulong TitleId { get; set; }

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new WiiUInstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new WiiUUninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)} : {TitleId:x16}";
        }

        public override void BrowseToSource()
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetFullPath(Mapping?.SourcePath)}\"");
        }
    }
}
