using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.Ps3
{
    [ProtoContract]
    internal sealed class Ps3GameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.Ps3;

        // PS3 title ids are strings (e.g. "BLES01234"), not numeric.
        [ProtoMember(1)]
        public string TitleId { get; set; }

        // Per-title source folder, relative to Mapping's SourcePath. Empty string for loose-ISO games.
        [ProtoMember(2)]
        public string SourceFolder { get; set; }

        [ProtoMember(3)]
        public Ps3BaseKind BaseKind { get; set; }

        // Set only for loose-ISO games (ISO sits directly in mapping.SourcePath, not in a subfolder).
        [ProtoMember(4)]
        public string SourceIsoFileName { get; set; }

        // The composite content (base/updates/DLC/RAPs) is re-derived from SourceFullDir at install time,
        // exactly as Yuzu re-scans its source — only the locator fields above are persisted.
        public string SourceFullDir => Path.Combine(Mapping?.SourcePath ?? "", SourceFolder ?? "");

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new Ps3InstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new Ps3UninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)}: {TitleId}";
            yield return $"{nameof(SourceFolder)}: {SourceFolder}";
            yield return $"{nameof(BaseKind)}: {BaseKind}";
            if (!string.IsNullOrEmpty(SourceIsoFileName))
                yield return $"{nameof(SourceIsoFileName)}: {SourceIsoFileName}";
            yield return $"{nameof(SourceFullDir)}*: {SourceFullDir}";
        }

        public override void BrowseToSource()
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetFullPath(SourceFullDir)}\"");
        }
    }
}
