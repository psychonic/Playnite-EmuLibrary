using Microsoft.VisualBasic.FileIO;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.Util
{
    public class DestinationExistsException : Exception
    {
        public DestinationExistsException(string message) : base(message) { }
    }

    public class DialogClosedException : Exception
    {
        public DialogClosedException(string message, Exception ex) : base(message, ex) { }
    }

    public abstract class BaseCopier
    {
        protected abstract string SourcePath { get; }
        protected abstract string DestinationPath { get; }
        protected abstract bool SourceExists();
        protected abstract bool DestinationExists();
        protected abstract void CopySourceToDestination();
        protected abstract void OnCopyCancellation();

        public async Task CopyAsync(CancellationToken cancellationToken)
        {
            if (SourcePath.IsNullOrEmpty() || !SourceExists())
            {
                throw new Exception($"source path \"{SourcePath}\" does not exist or is invalid");
            }
            if (DestinationPath.IsNullOrEmpty())
            {
                throw new Exception($"destination path \"{DestinationPath}\" is not valid");
            }
            if (DestinationExists())
            {
                throw new DestinationExistsException($"destination path \"{DestinationPath}\" already exists");
            }

            await Task.Run(() =>
                {
                    try
                    {
                        CopySourceToDestination();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            OnCopyCancellation();
                        }
                        catch { }
                        if (ex is OperationCanceledException)
                        {
                            throw new DialogClosedException("the user cancelled the copy request", ex);
                        }
                        throw new Exception($"Unable to copy source {SourcePath} to {DestinationPath}", ex);
                    }
                },
                cancellationToken
            );
        }
    }

    public class FileCopier : BaseCopier
    {
        public FileInfo SourceFile { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        protected override string SourcePath => SourceFile?.FullName;

        protected override string DestinationPath => Path.Combine(DestinationFolder?.FullName, SourceFile?.Name);

        protected override bool SourceExists()
        {
            return SourceFile?.Exists ?? false;
        }

        protected override bool DestinationExists()
        {
            return new FileInfo(DestinationPath)?.Exists ?? false;
        }

        protected override void CopySourceToDestination()
        {
            FileSystem.CopyFile(SourcePath, DestinationPath, UIOption.AllDialogs, UICancelOption.ThrowException);
        }

        protected override void OnCopyCancellation()
        {
            // Remove the file if for some reason it still exists after user cancellation.
            FileSystem.DeleteFile(DestinationPath, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);

            // Now let's also ensure that we clean up any empty directories that were added during copy.
            var parent = Directory.GetParent(DestinationPath);

            if (parent != null && !Directory.EnumerateFileSystemEntries(parent.FullName).Any())
            {
                FileSystem.DeleteDirectory(parent.FullName, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
            }
        }
    }

    public class FolderCopier : BaseCopier
    {
        public DirectoryInfo SourceFolder { get; set; }
        public DirectoryInfo DestinationFolder { get; set; }

        protected override string SourcePath => SourceFolder?.FullName;
        protected override string DestinationPath => DestinationFolder?.FullName;

        protected override bool SourceExists()
        {
            return SourceFolder?.Exists ?? false;
        }

        protected override bool DestinationExists()
        {
            return DestinationFolder?.Exists ?? false;
        }

        protected override void CopySourceToDestination()
        {
            FileSystem.CopyDirectory(SourcePath, DestinationPath, UIOption.AllDialogs, UICancelOption.ThrowException);
        }

        protected override void OnCopyCancellation()
        {
            // Since this is a directory, some of child nodes may have been copied successfully before cancellation.
            // Let's remove them.
            FileSystem.DeleteDirectory(DestinationPath, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
        }
    }
}
