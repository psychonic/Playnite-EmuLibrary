using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.Psp
{
    // Locator-only persisted state for one PSP (PPSSPP) title. The composite content (base image + the
    // update/DLC payload folder) is re-derived from the source at scan/install time via PspScanner.BuildTitle;
    // only the fields needed to find the base image and its sibling title-id folder are serialized here.
    // [ProtoMember] numbers are load-bearing (offset +10 from RomType.Psp in the wire) — do not renumber.
    [ProtoContract]
    internal sealed class PspGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.Psp;

        // Base disc image path (relative to Mapping's SourcePath), e.g. "Armored Core (USA).chd".
        [ProtoMember(1)]
        public string SourceImagePath { get; set; }

        // Disc id read from the image (e.g. "ULUS10538"); also the name of the sibling update/DLC folder.
        [ProtoMember(2)]
        public string TitleId { get; set; }

        // Absolute path to the base image; re-derived, never serialized.
        public string SourceFullPath => Path.Combine(Mapping?.SourcePath ?? "", SourceImagePath ?? "");

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new PspInstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new PspUninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)}: {TitleId}";
            yield return $"{nameof(SourceImagePath)}: {SourceImagePath}";
            yield return $"{nameof(SourceFullPath)}*: {SourceFullPath}";
        }

        public override void BrowseToSource()
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(SourceFullPath));
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
    }
}
