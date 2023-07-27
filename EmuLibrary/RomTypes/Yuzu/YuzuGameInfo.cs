using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.Yuzu
{
    [ProtoContract]
    internal sealed class YuzuGameInfo : ELGameInfo
    {
        public override RomType RomType => RomType.Yuzu;

        [ProtoMember(1)]
        public ulong TitleId { get; set; }

        public override InstallController GetInstallController(Game game, IEmuLibrary emuLibrary) =>
            new YuzuInstallController(game, emuLibrary);

        public override UninstallController GetUninstallController(Game game, IEmuLibrary emuLibrary) =>
            new YuzuUninstallController(game, emuLibrary);

        protected override IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(TitleId)} : {TitleId:x16}";
        }
    }
}
