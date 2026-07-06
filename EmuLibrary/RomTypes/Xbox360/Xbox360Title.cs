using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.Xbox360
{
    // What kind of content unit a Xbox360ContentRef represents.
    internal enum Xbox360ContentKind
    {
        DiscImage,    // XGD .iso base (copied to DestinationPath)
        DigitalBase,  // digital STFS base
        TitleUpdate,  // STFS TU package (000B0000), copied into the Xenia content tree
        Dlc,          // STFS DLC/addon package (00000002), copied into the Xenia content tree
        ZarImage,     // Xenia .zar base — a ZArchive of an extracted dump (copied to DestinationPath)
    }

    // One content unit of an Xbox 360 title as it sits in the source. This is the composite item type (the
    // analog of Yuzu's source file path / PS3's Ps3FileInfo / Wii U's WiiUContentRef). Nothing here is
    // serialized — it's re-derived each scan/install.
    internal sealed class Xbox360ContentRef
    {
        // The .iso (disc base) or STFS container file on disk for this unit.
        public string SourcePath;
        public Xbox360ContentKind Kind;

        public uint TitleId;
        public uint MediaId;

        // STFS/XEX version field. The ONLY thing that orders updates (latest-only for Xbox 360).
        public uint Version;

        // STFS content type (000B0000 update, 00000002 DLC, ...). Used to build the content-tree dest subpath
        // for updates/DLC; unused for a disc base.
        public uint ContentType;

        // Leaf name to write under the content tree (the STFS container's file name). Unused for a disc base.
        public string ContentName;

        public ulong ContentSize;
    }

    // In-memory composite for a single Xbox 360 title resolved from the source (base + latest update + DLC),
    // mirroring Ps3Title / WiiUTitle / YuzuTitle. Re-derived at scan and install time; nothing here is
    // serialized. Xbox 360 is a third composite-content combination: disc/package base (like Family B) + cumulative
    // latest-only updates (like Family A) + no license slot. Every install primitive is a plain file copy.
    internal sealed class Xbox360Title : ICompositeContentSet<Xbox360ContentRef>
    {
        public uint TitleId;
        public uint MediaId;
        public string Name;
        public string Version;
        public Xbox360BaseKind BaseKind;

        // Local path to the extracted STFS package thumbnail PNG (the title's icon), or null. Digital bases
        // only — re-derived at scan time, never serialized.
        public string IconPath;

        // Uncompressed install footprint: base + selected update + DLC.
        public ulong InstallSize;

        public Xbox360ContentRef BaseRef;
        public List<Xbox360ContentRef> Updates = new List<Xbox360ContentRef>(); // latest-only (0 or 1)
        public List<Xbox360ContentRef> DlcRefs = new List<Xbox360ContentRef>();

        Xbox360ContentRef ICompositeContentSet<Xbox360ContentRef>.Base => BaseRef;
        IReadOnlyList<Xbox360ContentRef> ICompositeContentSet<Xbox360ContentRef>.Updates => Updates;
        IReadOnlyList<Xbox360ContentRef> ICompositeContentSet<Xbox360ContentRef>.Dlc => DlcRefs;
        IReadOnlyList<Xbox360ContentRef> ICompositeContentSet<Xbox360ContentRef>.Licenses =>
            Array.Empty<Xbox360ContentRef>();
    }
}
