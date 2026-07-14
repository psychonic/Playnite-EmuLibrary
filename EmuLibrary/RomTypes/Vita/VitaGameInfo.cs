using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.Vita
{
    // Locator-only persisted state for one PS Vita (Vita3K) title. The composite content (base app tree plus
    // any patch trees) is re-derived from the source at scan/install time via VitaScanner.BuildTitle; only the
    // fields needed to find the source trees are serialized here — never the resolved content set (mirrors
    // Ps3/Yuzu). [ProtoMember] numbers are load-bearing (offset +10 from RomType.Vita on the wire) — do not
    // renumber.
    [ProtoContract]
    internal sealed class VitaGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.Vita;

        // Vita title id (e.g. "PCSE00890"); also the app/patch/addcont folder name under both source and ux0.
        [ProtoMember(1)]
        public string TitleId { get; set; }

        // The source container dir (relative to Mapping's SourcePath) that holds the "app/<id>" (and any
        // "patch/<id>") trees for this title. Empty string for the ux0-style Shape 1 layout, where app/ and
        // patch/ sit directly under SourcePath; the per-title folder name for the Shape 2 layout.
        [ProtoMember(2)]
        public string SourceFolder { get; set; }

        // Absolute path to the source container; re-derived, never serialized.
        public string SourceFullDir => Path.Combine(Mapping?.SourcePath ?? "", SourceFolder ?? "");

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new VitaInstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new VitaUninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)}: {TitleId}";
            yield return $"{nameof(SourceFolder)}: {SourceFolder}";
            yield return $"{nameof(SourceFullDir)}*: {SourceFullDir}";
        }

        public override void BrowseToSource()
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetFullPath(SourceFullDir)}\"");
        }
    }
}
