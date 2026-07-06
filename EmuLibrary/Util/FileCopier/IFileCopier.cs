using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.Util.FileCopier
{
    interface IFileCopier
    {
        FileSystemInfo Source { get; set; }
        DirectoryInfo Destination { get; set; }

        // progress is optional; copiers that can't report bytes (Windows shell dialog, symlink/hardlink) ignore it.
        Task CopyAsync(CancellationToken cancellationToken, IProgress<CopyProgress> progress = null);
    }
}
