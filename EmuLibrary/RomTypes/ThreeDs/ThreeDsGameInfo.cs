using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.ThreeDs
{
    [ProtoContract]
    internal sealed class ThreeDsGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.ThreeDs;

        // Locator only — the composite (base + latest update + DLC) is re-derived from the mapping's
        // SourcePath at scan and install time via ThreeDsScanner.BuildTitle, never serialized.
        // Field numbers are load-bearing; do not renumber.

        // Base title id for content-store titles (0 for loose files, which are located by SourceName).
        [ProtoMember(1)]
        public ulong TitleId { get; set; }

        // Relative folder name (CDN) or file name (cia/loose) under the mapping's SourcePath; "" if N/A.
        [ProtoMember(2)]
        public string SourceName { get; set; }

        // Whether this title installs into Azahar's SD content store or is copied verbatim to DestinationPath.
        [ProtoMember(3)]
        public ThreeDsBaseKind BaseKind { get; set; }

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new ThreeDsInstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new ThreeDsUninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)} : {TitleId:x16}";
            yield return $"{nameof(BaseKind)} : {BaseKind}";
            yield return $"{nameof(SourceName)} : {SourceName}";
        }

        public override void BrowseToSource()
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetFullPath(Mapping?.SourcePath)}\"");
        }
    }
}
