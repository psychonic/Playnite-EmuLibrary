using Playnite.SDK;

namespace EmuLibrary
{
    public class EmuLibraryClient : LibraryClient
    {
        public override bool IsInstalled => true;

        public override void Open() { }

        public override string Icon => EmuLibrary.Icon;
    }
}