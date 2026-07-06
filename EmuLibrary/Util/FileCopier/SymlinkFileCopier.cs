using System;
using System.IO;
using System.Threading;

namespace EmuLibrary.Util.FileCopier
{
    // Installs by creating a single symbolic link that points at the source instead of copying bytes. A file
    // source becomes a file symlink inside the destination directory; a directory source becomes a directory
    // symlink (the destination path itself). Mirrors SimpleFileCopier's source/destination contract so it can
    // be swapped in by the install controllers.
    public class SymlinkFileCopier : BaseFileCopier, IFileCopier
    {
        public SymlinkFileCopier(FileSystemInfo source, DirectoryInfo destination) : base(source, destination) { }

        // Creating a link is instant, so there are no bytes to report.
        protected override void Copy(CancellationToken cancellationToken, IProgress<CopyProgress> progress)
        {
            if (Source is DirectoryInfo)
            {
                // Destination is the link itself; make sure its parent exists but don't create the link dir.
                Destination.Parent?.Create();
                NativeLinks.CreateSymbolicLink(Destination.FullName, Source.FullName, isDirectory: true);
                return;
            }

            Directory.CreateDirectory(Destination.FullName);
            NativeLinks.CreateSymbolicLink(Path.Combine(Destination.FullName, Source.Name), Source.FullName, isDirectory: false);
        }
    }
}
