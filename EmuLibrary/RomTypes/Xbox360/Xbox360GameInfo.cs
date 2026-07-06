using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.Xbox360
{
    // Locator-only persisted state for one Xbox 360 (Xenia) title. The composite content (base + updates +
    // DLC) is re-derived from the source at scan/install time via Xbox360Scanner.BuildTitle; only the fields
    // needed to find the base and re-group its add-on content are serialized here. [ProtoMember] numbers are
    // load-bearing (offset +10 from RomType.Xbox360 in the wire) — do not renumber.
    [ProtoContract]
    internal sealed class Xbox360GameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.Xbox360;

        // Base title id (from the disc's default.xex execution id, or the digital package). The first half of
        // the (TitleId, MediaId) grouping key.
        [ProtoMember(1)]
        public uint TitleId { get; set; }

        // Base media id — the key a title update is matched against (a disc and a GoD release of the same game
        // share a TitleId but differ in MediaId). The second half of the grouping key.
        [ProtoMember(2)]
        public uint MediaId { get; set; }

        // Path of the base (the .iso file for a disc, the STFS container for a digital base), relative to the
        // mapping's SourcePath.
        [ProtoMember(3)]
        public string SourceName { get; set; }

        [ProtoMember(4)]
        public Xbox360BaseKind BaseKind { get; set; }

        public string SourceFullPath => Path.Combine(Mapping?.SourcePath ?? "", SourceName ?? "");

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new Xbox360InstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new Xbox360UninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)}: {TitleId:X8}";
            yield return $"{nameof(MediaId)}: {MediaId:X8}";
            yield return $"{nameof(SourceName)}: {SourceName}";
            yield return $"{nameof(BaseKind)}: {BaseKind}";
            yield return $"{nameof(SourceFullPath)}*: {SourceFullPath}";
        }

        public override void BrowseToSource()
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(SourceFullPath));
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
    }
}
