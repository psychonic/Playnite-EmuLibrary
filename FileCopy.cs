using System.IO;
using System.Threading.Tasks;

namespace EmuLibrary
{
    // From https://stackoverflow.com/questions/41138762/asynchronous-directory-copy-with-progress-bar
    public class FileCopier
    {
        public FileInfo SourceFile { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        public void Copy()
        {
            // TODO: throw exceptions if SourceFile / DestinationFile null
            // TODO: throw exception if SourceFile does not exist
            string destinationFileName = Path.Combine(DestinationFolder.FullName, SourceFile.Name);
            // TODO: decide what to do if destinationFile already exists

            // open source file for reading
            using (Stream sourceStream = File.Open(SourceFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // create destination file write
                using (Stream destinationStream = File.Open(destinationFileName, FileMode.CreateNew))
                {
                    Copy(sourceStream, destinationStream);
                }
            }
        }

        public void Copy(Stream Source, Stream Destination)
        {
            byte[] buffer = new byte[0x1000];
            int numRead;
            while ((numRead = Source.Read(buffer, 0, buffer.Length)) != 0)
            {
                Destination.Write(buffer, 0, numRead);
            }
        }
    }

    public class FolderCopier
    {
        public DirectoryInfo SourceFolder { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        public void Copy()
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

                    fileCopier.Copy();
                }
                else if (sourceFsInfo is DirectoryInfo)
                {
                    var folderCopier = new FolderCopier()
                    {
                        SourceFolder = (sourceFsInfo as DirectoryInfo),
                        DestinationFolder = new DirectoryInfo(Path.Combine(DestinationFolder.FullName, sourceFsInfo.Name))
                    };

                    folderCopier.Copy();
                }
            }
        }
    }
}
