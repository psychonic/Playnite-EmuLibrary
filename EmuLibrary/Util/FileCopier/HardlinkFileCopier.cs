using System;
using System.IO;
using System.Threading;

namespace EmuLibrary.Util.FileCopier
{
    // Installs by hard-linking each source file into the destination instead of copying bytes. Hard links are
    // per-file and can't span directories, so a directory source is mirrored as a real directory tree of hard
    // links (one link per file). All links must land on the same volume as the source. The resulting
    // destination is an ordinary directory of links, so the normal recursive uninstall deletes only the links.
    public class HardlinkFileCopier : BaseFileCopier, IFileCopier
    {
        public HardlinkFileCopier(FileSystemInfo source, DirectoryInfo destination) : base(source, destination) { }

        // Creating hard links is instant, so there are no bytes to report.
        protected override void Copy(CancellationToken cancellationToken, IProgress<CopyProgress> progress)
        {
            if (Source is DirectoryInfo sourceDir)
            {
                HardlinkDirectory(sourceDir, Destination);
                return;
            }

            Directory.CreateDirectory(Destination.FullName);
            NativeLinks.CreateHardLink(Path.Combine(Destination.FullName, Source.Name), Source.FullName);
        }

        private static void HardlinkDirectory(DirectoryInfo source, DirectoryInfo destination)
        {
            Directory.CreateDirectory(destination.FullName);

            foreach (var file in source.GetFiles())
            {
                NativeLinks.CreateHardLink(Path.Combine(destination.FullName, file.Name), file.FullName);
            }

            foreach (var subDirectory in source.GetDirectories())
            {
                HardlinkDirectory(subDirectory, destination.CreateSubdirectory(subDirectory.Name));
            }
        }
    }
}
