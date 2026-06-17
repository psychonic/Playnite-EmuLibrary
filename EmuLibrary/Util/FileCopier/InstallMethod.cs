namespace EmuLibrary.Util.FileCopier
{
    // How a mapping installs a ROM into its destination. Copy duplicates the bytes (the original behavior);
    // Symlink/Hardlink instead reference the source on disk to save space (issue #2). Only RomTypes that copy
    // the source verbatim support linking (see RomTypeScanner.SupportsInstallLinking) - Copy is always valid.
    // Copy is 0 so configs saved before this field existed deserialize to Copy.
    public enum InstallMethod
    {
        Copy = 0,
        Symlink = 1,
        Hardlink = 2,
    }
}
