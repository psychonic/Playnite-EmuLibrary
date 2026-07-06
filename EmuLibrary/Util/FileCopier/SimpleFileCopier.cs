using System;
using System.IO;
using System.Threading;

namespace EmuLibrary.Util.FileCopier
{
    public class SimpleFileCopier : BaseFileCopier, IFileCopier
    {
        // 1 MiB read/write chunk; also the byte cadence at which progress is reported and cancellation checked.
        private const int BufferSize = 1024 * 1024;

        public SimpleFileCopier(FileSystemInfo source, DirectoryInfo destination) : base(source, destination) { }

        protected override void Copy(CancellationToken cancellationToken, IProgress<CopyProgress> progress)
        {
            // Size the whole copy up front so the bar shows a real percentage.
            long total = Source is DirectoryInfo sizeDir ? DirectorySize(sizeDir) : ((FileInfo)Source).Length;
            long done = 0;
            progress?.Report(new CopyProgress(0, total));

            try
            {
                if (Source is DirectoryInfo sourceDir)
                {
                    CopyDirectoryContents(sourceDir, Destination, total, ref done, cancellationToken, progress);
                }
                else
                {
                    CopyFileStreamed(Source.FullName, Path.Combine(Destination.FullName, Source.Name), total, ref done, cancellationToken, progress);
                }
            }
            catch
            {
                // A canceled or failed copy must not leave a partial title behind (installed-ness for several
                // RomTypes is derived from the destination existing). Remove exactly what this copier targets:
                // the whole per-game destination folder for a directory source, or the single file for a file
                // source (shared destination dirs must not be wiped).
                TryCleanupPartialOutput();
                throw;
            }
        }

        private void TryCleanupPartialOutput()
        {
            try
            {
                if (Source is DirectoryInfo)
                {
                    if (Directory.Exists(Destination.FullName))
                        Directory.Delete(Destination.FullName, true);
                }
                else
                {
                    var destFile = Path.Combine(Destination.FullName, Source.Name);
                    if (File.Exists(destFile))
                        File.Delete(destFile);
                }
            }
            catch { }
        }

        private static long DirectorySize(DirectoryInfo dir)
        {
            long sum = 0;
            foreach (var file in dir.GetFiles())
                sum += file.Length;
            foreach (var subDirectory in dir.GetDirectories())
                sum += DirectorySize(subDirectory);
            return sum;
        }

        private static void CopyDirectoryContents(DirectoryInfo source, DirectoryInfo destination, long total, ref long done, CancellationToken cancellationToken, IProgress<CopyProgress> progress)
        {
            Directory.CreateDirectory(destination.FullName);

            foreach (var file in source.GetFiles())
            {
                CopyFileStreamed(file.FullName, Path.Combine(destination.FullName, file.Name), total, ref done, cancellationToken, progress);
            }

            foreach (var subDirectory in source.GetDirectories())
            {
                CopyDirectoryContents(subDirectory, destination.CreateSubdirectory(subDirectory.Name), total, ref done, cancellationToken, progress);
            }
        }

        private static void CopyFileStreamed(string sourcePath, string destPath, long total, ref long done, CancellationToken cancellationToken, IProgress<CopyProgress> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.None))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    done += read;
                    progress?.Report(new CopyProgress(done, total));
                }
            }

            // Preserve the source's last-write time, matching File.Copy's behaviour.
            File.SetLastWriteTimeUtc(destPath, File.GetLastWriteTimeUtc(sourcePath));
        }
    }
}
