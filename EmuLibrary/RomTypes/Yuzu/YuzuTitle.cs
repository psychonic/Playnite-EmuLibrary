using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.Yuzu
{
    // In-memory composite for a single Switch title resolved from the source dir (base game + latest update
    // + DLC), mirroring Ps3Scanner.Ps3Title. Re-derived at scan and at install time; nothing here is
    // serialized. Item type is the source file path. Switch is the content-store family (Family A): updates
    // are cumulative so only the latest is kept, and there are no license files.
    internal sealed class YuzuTitle : ICompositeContentSet<string>
    {
        public ulong TitleId;
        public string Name;
        public string Publisher;
        public string Version;

        // Uncompressed NAND footprint of base game + latest update + all DLC.
        public ulong InstallSize;

        // Base game file (XCI or NSP).
        public string ProgramFile;

        // Zero or one (the latest update).
        public string UpdateFile;

        // Zero or many DLC files.
        public List<string> DlcFiles = new List<string>();

        string ICompositeContentSet<string>.Base => ProgramFile;
        IReadOnlyList<string> ICompositeContentSet<string>.Updates =>
            UpdateFile != null ? new[] { UpdateFile } : Array.Empty<string>();
        IReadOnlyList<string> ICompositeContentSet<string>.Dlc => DlcFiles;
        IReadOnlyList<string> ICompositeContentSet<string>.Licenses => Array.Empty<string>();
    }

    // Installed-state info for a single Switch title, derived from the emulator's NAND each scan.
    internal sealed class YuzuInstalledTitle
    {
        public ulong TitleId;
        public string Name;
        public string Publisher;
        public string Version;
        public ulong InstallSize;

        // Path (under the NAND registered dir) of the title's program NCA, used to build the launch rom.
        public string ProgramNcaSubPath;
    }
}
