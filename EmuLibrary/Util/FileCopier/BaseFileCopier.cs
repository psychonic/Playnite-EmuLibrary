using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Input
// -----
// SingleFile   Sourch.FullName             M:\media\games\Xbox Live Arcade\Banjo Tooie
// SingleFile   Destination.FullName        C:\Roms\XBLA
// MultiFile    Sourch.FullName             M:\media\games\Xbox 360\Brutal Legend
// MultiFile    Destination.FullName        C:\Roms\Xbox 360\Brutal Legend

// Output
// ------------
// SingleFile   WindowsSource               M:\media\games\Xbox Live Arcade\Banjo Tooie
// SingleFile   WindowsDestination          C:\Roms\XBLA\Banjo Tooie
// SingleFile   SimpleSource                M:\media\games\Xbox Live Arcade\Banjo Tooie
// SingleFile   SimpleDestination           C:\Roms\XBLA\Banjo Tooie
// MultiFile    WindowsSource               M:\media\games\Xbox 360\Brutal Legend
// MultiFile    WindowsDestination          C:\Roms\Xbox 360\Brutal Legend
// MultiFile    SimpleSource                M:\media\games\Xbox 360\Brutal Legend
// MultiFile    SimpleDestination           C:\Roms\Xbox 360\Brutal Legend

namespace EmuLibrary.Util.FileCopier
{
    public abstract class BaseFileCopier : IFileCopier
    {
        // e.g. M:\media\games\N64\Mario 64.z64 (file)
        // e.g. M:\media\games\Xbox 360\Brutal Legend (directory)
        public FileSystemInfo Source { get; set; }

        // e.g. C:\Roms\N64 (file to directory copy)
        // e.g. C:\Roms\Xbox 360\Brutal Legend (directory to directory copy)
        public DirectoryInfo Destination { get; set; }

        internal BaseFileCopier(FileSystemInfo source, DirectoryInfo destination)
        {
            Source = source;
            Destination = destination;
        }

        protected abstract void Copy();

        public async Task CopyAsync(CancellationToken cancellationToken)
        {
            if (Source.FullName.IsNullOrEmpty() || !Source.Exists)
            {
                throw new Exception($"source path \"{Source.FullName}\" does not exist or is invalid");
            }
            if (Destination.FullName.IsNullOrEmpty())
            {
                throw new Exception($"destination path \"{Destination.FullName}\" is not valid");
            }

            await Task.Run(() =>
                {
                    Copy();
                },
                cancellationToken
            );
        }
    }
}
