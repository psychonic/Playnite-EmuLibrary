using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.WiiU
{
    // One content unit of a Wii U title as it sits in the source: a base game, an update, or a DLC, in one
    // of the supported source formats. This is the composite item type (the analog of Yuzu's source file
    // path / PS3's Ps3FileInfo). Nothing here is serialized — it's re-derived each scan/install.
    internal sealed class WiiUContentRef
    {
        // The file (.wux/.wud/.wua) or folder (NUS / loadiine) on disk for this content unit.
        public string SourcePath;
        public WiiUSourceFormat Format;
        public WiiUContentKind Kind;

        // Full title id of THIS unit (base/update/DLC differ in the high word); the base title id is shared
        // across the set and lives on WiiUTitle.
        public ulong TitleId;

        // Title version from the TMD. The only thing that orders updates (latest-only for Wii U).
        public uint Version;
    }

    // In-memory composite for a single Wii U title resolved from the source dir (base game + latest update +
    // DLC), mirroring YuzuTitle / Ps3Scanner.Ps3Title. Re-derived at scan and install time; nothing here is
    // serialized. Wii U is the content-store family (Family A): updates are cumulative so only the latest is
    // kept, and there are no license files.
    internal sealed class WiiUTitle : ICompositeContentSet<WiiUContentRef>
    {
        public ulong TitleId;
        public string Name;
        public string Publisher;
        public string Version;

        // Raw Wii U product code from meta.xml (e.g. "WUP-P-AVEE"); kept for reference/debugging.
        public string ProductCode;

        // The GameTDB Wii U join key (6 chars: game code + maker, e.g. "AVEE0W"), derived from product_code +
        // company_code via Cemu.BuildGameTdbId. Null when meta.xml lacks the pieces. Keyed against "wiiutdb".
        public string GameTdbId;

        // Best-effort uncompressed footprint of the title's content (informational; conversion to .wua
        // compresses it).
        public ulong InstallSize;

        // Base game content unit.
        public WiiUContentRef BaseRef;

        // Zero or one (the latest update).
        public WiiUContentRef UpdateRef;

        // Zero or many DLC content units.
        public List<WiiUContentRef> DlcRefs = new List<WiiUContentRef>();

        WiiUContentRef ICompositeContentSet<WiiUContentRef>.Base => BaseRef;
        IReadOnlyList<WiiUContentRef> ICompositeContentSet<WiiUContentRef>.Updates =>
            UpdateRef != null ? new[] { UpdateRef } : Array.Empty<WiiUContentRef>();
        IReadOnlyList<WiiUContentRef> ICompositeContentSet<WiiUContentRef>.Dlc => DlcRefs;
        IReadOnlyList<WiiUContentRef> ICompositeContentSet<WiiUContentRef>.Licenses => Array.Empty<WiiUContentRef>();

        // True when the base can be installed by copying the source verbatim (Cemu loads it directly) rather
        // than decrypting/merging into a .wua: a single self-contained file/folder with no separate update or
        // DLC to merge in.
        public bool IsSelfContained =>
            UpdateRef == null
            && DlcRefs.Count == 0
            && BaseRef != null
            && (BaseRef.Format == WiiUSourceFormat.Wua
                || BaseRef.Format == WiiUSourceFormat.Wux
                || BaseRef.Format == WiiUSourceFormat.Wud
                || BaseRef.Format == WiiUSourceFormat.Loadiine);
    }

    // Installed-state info for a single Wii U title, derived from the destination folder on disk each scan
    // (no install state is cached). Mirrors YuzuInstalledTitle.
    internal sealed class WiiUInstalledTitle
    {
        public ulong TitleId;
        public string Name;
        public string Version;
        public ulong InstallSize;

        // The launchable path passed to Cemu (-g): the installed .wua / .wux / .rpx.
        public string LaunchPath;

        // What was installed at the destination, so uninstall knows whether to delete a file or a folder.
        public WiiUSourceFormat Format;

        // The top-level file or folder at the destination to remove on uninstall.
        public string InstalledPath;
    }
}
