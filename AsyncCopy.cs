using System.IO;
using System.Threading.Tasks;

namespace EmuLibrary
{
    // From https://stackoverflow.com/questions/41138762/asynchronous-directory-copy-with-progress-bar
    public class FileCopier
    {
        public FileInfo SourceFile { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        public async Task CopyAsync()
        {
            // TODO: throw exceptions if SourceFile / DestinationFile null
            // TODO: throw exception if SourceFile does not exist
            string destinationFileName = Path.Combine(DestinationFolder.FullName, SourceFile.Name);
            // TODO: decide what to do if destinationFile already exists

            // open source file for reading
            using (Stream sourceStream = File.Open(SourceFile.FullName, FileMode.Open))
            {
                // create destination file write
                using (Stream destinationStream = File.Open(destinationFileName, FileMode.CreateNew))
                {
                    await CopyAsync(sourceStream, destinationStream);
                }
            }
        }

        public async Task CopyAsync(Stream Source, Stream Destination)
        {
            byte[] buffer = new byte[0x1000];
            int numRead;
            while ((numRead = await Source.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await Destination.WriteAsync(buffer, 0, numRead);
            }
        }
    }

    public class FolderCopier
    {
        public DirectoryInfo SourceFolder { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        public async Task CopyAsync()
        {
            if (!DestinationFolder.Exists)
            {
                Directory.CreateDirectory(DestinationFolder.FullName);
            }

            foreach (var sourceFsInfo in SourceFolder.EnumerateFileSystemInfos())
            {
                if (sourceFsInfo is FileInfo)
                {
                    var fileCopier = new FileCopier()
                    {
                        SourceFile = sourceFsInfo as FileInfo,
                        DestinationFolder = this.DestinationFolder,
                    };
                    await fileCopier.CopyAsync();
                }
                else if (sourceFsInfo is DirectoryInfo)
                {
                    var folderCopier = new FolderCopier()
                    {
                        SourceFolder = (sourceFsInfo as DirectoryInfo),
                        DestinationFolder = new DirectoryInfo(Path.Combine(DestinationFolder.FullName, sourceFsInfo.Name))
                    };
                    await folderCopier.CopyAsync();
                }
            }
        }
    }
}
