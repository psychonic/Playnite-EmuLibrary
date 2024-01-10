using System.IO;

namespace EmuLibrary.Util.FileCopier
{
    public class SimpleFileCopier : BaseFileCopier, IFileCopier
    {
        public SimpleFileCopier(FileSystemInfo source, DirectoryInfo destination) : base(source, destination) { }

        protected override void Copy()
        {
            if (Source is DirectoryInfo)
            {
                CopyAll(Source as DirectoryInfo, Destination);
                return;
            }

            File.Copy(Source.FullName, Path.Combine(Destination.FullName, Source.Name), true);
        }

        private static void CopyAll(DirectoryInfo source, DirectoryInfo destination)
        {
            Directory.CreateDirectory(destination.FullName);

            foreach (var file in source.GetFiles())
            {
                file.CopyTo(Path.Combine(destination.FullName, file.Name), true);
            }

            foreach (var subDirectory in source.GetDirectories())
            {
                CopyAll(subDirectory, destination.CreateSubdirectory(subDirectory.Name));
            }
        }
    }
}
