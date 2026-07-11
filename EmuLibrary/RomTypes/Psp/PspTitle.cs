using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.Psp
{
    // Where a PSP content item installs under the emulator's memory-stick tree. The observed collection is
    // entirely GAME (update/DLC payloads named by title id); SAVEDATA routing is implemented defensively but a
    // no-op for the current data (see WS12 §0/§6b).
    internal enum PspRoute
    {
        Game = 0,
        Savedata = 1,
    }

    // One content item of a PSP title as it sits in the source. This is the composite item type (the analog of
    // Yuzu's source file path / PS3's Ps3FileInfo / Xbox 360's Xbox360ContentRef). Nothing here is serialized —
    // it's re-derived each scan/install. The base is the disc image (a file copied to DestinationPath); an
    // update/DLC item is the sibling title-id folder, copied verbatim into <memstick>/PSP/GAME/<DestSubPath>.
    internal sealed class PspContentRef
    {
        // The image file (base) or the payload folder (update/DLC) on disk.
        public string SourcePath;

        // True when SourcePath is a directory copied whole (update/DLC payload); false for the base image file.
        public bool IsDirectory;

        // GAME or SAVEDATA subtree of the memory stick (always GAME for observed content).
        public PspRoute Route;

        // Leaf directory name under <memstick>/PSP/<route>/ (the title-id folder name). Empty for the base.
        public string DestSubPath;

        // Parsed version (APP_VER/DISC_VERSION) used ONLY to order updates; never the filename/enumeration order.
        public Version Version = new Version(0, 0);

        public ulong ContentSize;
    }

    // In-memory composite for a single PSP title resolved from the source (base image + its update/DLC payload).
    // Re-derived at scan and install time; nothing here is serialized. PSP is the disc+package family (Family B):
    // updates install all-in-order and there are no license files.
    internal sealed class PspTitle : ICompositeContentSet<PspContentRef>
    {
        public string TitleId;
        public string Name;
        public string Version;

        // Absolute path to the base disc image copied to DestinationPath.
        public string SourceImagePath;

        // Uncompressed install footprint: base image + payload folder(s).
        public ulong InstallSize;

        public PspContentRef BaseRef;
        public List<PspContentRef> Updates = new List<PspContentRef>(); // ascending version
        public List<PspContentRef> Dlcs = new List<PspContentRef>();

        PspContentRef ICompositeContentSet<PspContentRef>.Base => BaseRef;
        IReadOnlyList<PspContentRef> ICompositeContentSet<PspContentRef>.Updates => Updates;
        IReadOnlyList<PspContentRef> ICompositeContentSet<PspContentRef>.Dlc => Dlcs;
        IReadOnlyList<PspContentRef> ICompositeContentSet<PspContentRef>.Licenses => Array.Empty<PspContentRef>();
    }
}
