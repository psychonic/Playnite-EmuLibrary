using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;

namespace EmuLibrary.RomTypes.MultiFile
{
    [ProtoContract]
    internal class MultiFileGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.MultiFile;

        // Relative to Mapping's SourcePath
        [ProtoMember(1)]
        public string SourceFilePath { get; set; }

        // Relative to Mapping's SourcePath
        [ProtoMember(2)]
        public string SourceBaseDir { get; set; }

        public string SourceFullBaseDir
        {
            get
            {
                return Path.Combine(Mapping.SourcePath, SourceBaseDir);
            }
        }

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new MultiFileInstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new MultiFileUninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(SourceFilePath)}: {SourceFilePath}";
            yield return $"{nameof(SourceBaseDir)}: {SourceBaseDir}";
            yield return $"{nameof(SourceFullBaseDir)}*: {SourceFullBaseDir}";
        }
    }
}
