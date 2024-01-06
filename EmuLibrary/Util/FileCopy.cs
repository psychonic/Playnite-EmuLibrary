using Microsoft.VisualBasic.FileIO;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.Util
{
    public class FileCopier
    {
        public FileInfo SourceFile { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        public async Task CopyAsync(CancellationToken cancellationToken)
        {
            if (SourceFile.FullName == null)
            {
                throw new NullReferenceException(nameof(SourceFile));
            }
            if (DestinationFolder.FullName == null)
            {
                throw new NullReferenceException(nameof(DestinationFolder));
            }
            if (!SourceFile.Exists)
            {
                throw new FileNotFoundException($"the file {SourceFile.FullName} can not be copied");
            }

            var destinationFileName = Path.Combine(DestinationFolder.FullName, SourceFile.Name);

            await Task.Run(() =>
                {
                    // Copy the file, whilst also displaying the Windows copy dialog.
                    try
                    {
                        FileSystem.CopyFile(SourceFile.FullName, destinationFileName, UIOption.AllDialogs, UICancelOption.ThrowException);
                    }
                    catch (Exception ex)
                    {
                        // Clean up any partial files upon user cancellation.
                        try
                        {
                            FileSystem.DeleteFile(destinationFileName, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
                        }
                        catch { }
                        if (ex is TaskCanceledException)
                        {
                            throw new TaskCanceledException("the user cancelled the copy request", ex);
                        }
                        throw new Exception("Unable to copy file", ex);
                    }
                },
                cancellationToken
            );
        }
    }

    public class FolderCopier
    {
        public DirectoryInfo SourceFolder { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        public async Task CopyAsync(CancellationToken cancellationToken)
        {
            if (SourceFolder.FullName == null)
            {
                throw new NullReferenceException(nameof(SourceFolder));
            }
            if (DestinationFolder.FullName == null)
            {
                throw new NullReferenceException(nameof(DestinationFolder));
            }
            if (!SourceFolder.Exists)
            {
                throw new DirectoryNotFoundException($"the directory {SourceFolder.FullName} can not be copied");
            }

            await Task.Run(() =>
                {
                    // Copy the directory, whilst also displaying the Windows copy dialog.
                    try
                    {
                        FileSystem.CopyDirectory(SourceFolder.FullName, DestinationFolder.FullName, UIOption.AllDialogs, UICancelOption.ThrowException);
                    }
                    catch (Exception ex)
                    {
                        // Clean up any partial files upon user cancellation.
                        try
                        {
                            FileSystem.DeleteDirectory(DestinationFolder.FullName, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
                        }
                        catch { }
                        if (ex is TaskCanceledException)
                        {
                            throw new TaskCanceledException("the user cancelled the copy request", ex);
                        }
                        throw new Exception("Unable to copy directory", ex);
                    }
                },
                cancellationToken
            );
        }
    }
}
