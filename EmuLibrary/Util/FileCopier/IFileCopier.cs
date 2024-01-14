using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.Util.FileCopier
{
    interface IFileCopier
    {
        FileSystemInfo Source { get; set; }
        DirectoryInfo Destination { get; set; }
        Task CopyAsync(CancellationToken cancellationToken);
    }
}
