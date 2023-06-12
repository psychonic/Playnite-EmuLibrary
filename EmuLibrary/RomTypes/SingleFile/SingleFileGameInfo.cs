using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmuLibrary.RomTypes.SingleFile
{
    [ProtoContract]
    internal sealed class SingleFileGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.SingleFile;

        // Relative to Mapping's SourcePath
        [ProtoMember(1)]
        public string SourcePath { get; set; }  

        public string SourceFullPath
        {
            get
            {
                var mapping = EmuLibrarySettings.Instance.Mappings.First(m => m.MappingId == MappingId);
                return Path.Combine(mapping.SourcePath, SourcePath);
            }
        }

        public override InstallController GetInstallController(Game game, EmuLibrarySettings settings, IPlayniteAPI playniteAPI)
        {
            return new SingleFileInstallController(game, settings, playniteAPI);
        }

        public override UninstallController GetUninstallController(Game game, IPlayniteAPI playniteAPI)
        {
            return new SingleFileUninstallController(game, playniteAPI);
        }

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(SourcePath)} : {SourcePath}";
            yield return $"{nameof(SourceFullPath)}* : {SourceFullPath}";
        }
    }
}
