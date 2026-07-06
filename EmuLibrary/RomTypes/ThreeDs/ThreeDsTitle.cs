using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // One content unit of a 3DS title as it sits in the source: a base, an update, or a DLC, in one of the
    // supported source shapes. This is the composite item type (the analog of WiiU's WiiUContentRef / PS3's
    // Ps3FileInfo). Nothing here is serialized — it's re-derived each scan/install.
    internal sealed class ThreeDsContentRef
    {
        // The file (cia/loose) or folder (CDN) on disk for this content unit.
        public string SourcePath;
        public ThreeDsSourceKind SourceKind;

        // Full title id of THIS unit (base/update/DLC differ in the high word); the base title id is shared
        // across the set and lives on ThreeDsTitle. 0 for loose files (not parsed).
        public ulong TitleId;

        // Title version from the tmd. The only thing that orders updates (latest-only for 3DS, Family A).
        public uint Version;

        // Resolved .tik/cetk titlekey source for content-store units; null for loose files.
        public string TicketPath;

        // Best-effort byte footprint of this unit's content (informational).
        public ulong ContentSize;
    }

    // In-memory composite for a single 3DS title resolved from the source (base + latest update + DLC),
    // mirroring WiiUTitle. Re-derived at scan and install time; nothing here is serialized. 3DS is the
    // content-store family (Family A): updates are cumulative so only the latest is kept, and there are no
    // license files. For a LooseFile title, Base is the single file ref and Updates/Dlc are empty.
    internal sealed class ThreeDsTitle : ICompositeContentSet<ThreeDsContentRef>
    {
        public ulong TitleId;
        public string Name;
        public uint Version;
        public ThreeDsBaseKind BaseKind;
        public ulong InstallSize;

        // True when the base is a CDN folder whose ticket (.tik) is missing, so its titlekey can't be resolved.
        // The title still scans (like a PS3 pkg missing its RAP); the install controller refuses it with a
        // clear message until the .tik is supplied. CIA bases carry their own ticket, so this is never set.
        public bool BaseLicenseMissing;

        public ThreeDsContentRef BaseRef;
        public ThreeDsContentRef UpdateRef;
        public List<ThreeDsContentRef> DlcRefs = new List<ThreeDsContentRef>();

        ThreeDsContentRef ICompositeContentSet<ThreeDsContentRef>.Base => BaseRef;
        IReadOnlyList<ThreeDsContentRef> ICompositeContentSet<ThreeDsContentRef>.Updates =>
            UpdateRef != null ? new[] { UpdateRef } : Array.Empty<ThreeDsContentRef>();
        IReadOnlyList<ThreeDsContentRef> ICompositeContentSet<ThreeDsContentRef>.Dlc => DlcRefs;
        IReadOnlyList<ThreeDsContentRef> ICompositeContentSet<ThreeDsContentRef>.Licenses => Array.Empty<ThreeDsContentRef>();
    }
}
