using Microsoft.VisualBasic.FileIO;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace EmuLibrary.Util.FileCopier
{
    public class WindowsFileCopier : BaseFileCopier, IFileCopier
    {
        public WindowsFileCopier(FileSystemInfo source, DirectoryInfo destination) : base(source, destination) { }

        protected override void Copy()
        {
            try
            {
                if (Source is DirectoryInfo)
                {
                    FileSystem.CopyDirectory(Source.FullName, Destination.FullName, UIOption.AllDialogs);
                }
                else
                {
                    FileSystem.CopyFile(Source.FullName, Destination.FullName, UIOption.AllDialogs);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    OnCopyDialogClosedByUser();
                }
                catch { }
                if (ex is OperationCanceledException)
                {
                    throw new WindowsCopyDialogClosedException("The user cancelled the copy request", ex);
                }
                throw new Exception($"Unable to copy source {Source.FullName} to {Destination.FullName}", ex);
            }
        }


        private void OnCopyDialogClosedByUser()
        {
            // For directories, some child nodes may have been partially copied before cancellation. Clean these up.
            if (Source is DirectoryInfo)
            {
                // Since this is a directory, some of child nodes may have been copied successfully before cancellation.
                // Let's remove them.
                FileSystem.DeleteDirectory(Destination.FullName, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
            }
            // Remove the file if for some reason it still exists after user cancellation.
            // Also ensure that we clean up any empty directories that were added during copy.
            else if (Source is FileInfo)
            {
                FileSystem.DeleteFile(Destination.FullName, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);

                var parent = Directory.GetParent(Destination.FullName);
                if (parent != null && !Directory.EnumerateFileSystemEntries(parent.FullName).Any())
                {
                    FileSystem.DeleteDirectory(parent.FullName, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
                }
            }
        }
    }

    public class WindowsCopyDialogClosedException : Exception
    {
        public WindowsCopyDialogClosedException(string message, Exception ex) : base(message, ex) { }
    }
}
