using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace EmuLibrary.Util.FileCopier
{
    // Thin P/Invoke wrappers for creating filesystem links. .NET Framework 4.6.2 has no managed API for
    // symbolic/hard links (File.CreateSymbolicLink arrives in .NET 6), so we call kernel32 directly.
    internal static class NativeLinks
    {
        private const uint SYMBOLIC_LINK_FLAG_FILE = 0x0;
        private const uint SYMBOLIC_LINK_FLAG_DIRECTORY = 0x1;
        // Lets non-elevated processes create symlinks when Windows Developer Mode is enabled. Older Windows
        // rejects the flag with ERROR_INVALID_PARAMETER, so we retry without it below.
        private const uint SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

        [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLinkNative(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public static void CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
        {
            var typeFlag = isDirectory ? SYMBOLIC_LINK_FLAG_DIRECTORY : SYMBOLIC_LINK_FLAG_FILE;
            if (CreateSymbolicLinkNative(linkPath, targetPath, typeFlag | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE))
            {
                return;
            }

            // Retry without the unprivileged flag for Windows versions that don't understand it.
            if (CreateSymbolicLinkNative(linkPath, targetPath, typeFlag))
            {
                return;
            }

            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to create symbolic link \"{linkPath}\" -> \"{targetPath}\". Symbolic links require Windows Developer Mode to be enabled or Playnite to run as administrator.",
                new Win32Exception(err));
        }

        public static void CreateHardLink(string linkPath, string targetPath)
        {
            if (CreateHardLinkNative(linkPath, targetPath, IntPtr.Zero))
            {
                return;
            }

            var err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to create hard link \"{linkPath}\" -> \"{targetPath}\". Hard links require the source and destination to be files on the same volume.",
                new Win32Exception(err));
        }
    }
}
