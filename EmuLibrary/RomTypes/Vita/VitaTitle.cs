using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.Vita
{
    // Which ux0 subtree a Vita content item installs into. NoNpDrm uses App (base) and Patch (update);
    // PSN/PKG adds AddCont (DLC) and License.
    internal enum VitaContentKind
    {
        App = 0,     // base game -> ux0/app/<titleId>
        Patch = 1,   // update    -> ux0/patch/<titleId>
        AddCont = 2, // DLC       -> ux0/addcont/<titleId>/<contentId>
        License = 3, // license   -> ux0/license/<titleId>/<cid>.rif
    }

    // How a content item is materialized on install. NoNpDrm items are already-decrypted directory trees copied
    // verbatim (Tree); PSN items are encrypted .pkg packages that are decrypted + extracted (Pkg), or a
    // license blob (work.bin/zRIF) written as a .rif (License). This is the per-item source-shape
    // discriminator: it is RE-DERIVED from the source at scan/install time (a .pkg present => PSN/Pkg; an
    // app/<id> tree => NoNpDrm/Tree), never serialized — VitaGameInfo persists only locators.
    internal enum VitaSourceKind
    {
        Tree = 0,    // copy an already-decrypted directory tree (NoNpDrm)
        Pkg = 1,     // decrypt + extract an encrypted .pkg (PSN)
        License = 2, // write a .rif from work.bin/zRIF (PSN)
    }

    // One content item of a Vita title as it sits in the source. This is the composite item type (the analog of
    // Yuzu's source file path / PS3's Ps3FileInfo). Nothing here is serialized — it's re-derived each
    // scan/install.
    internal sealed class VitaContentRef
    {
        // The source directory tree to copy (the "app/<id>" or "patch/<id>" folder). Set for Tree items.
        public string SourceTreeDir;

        public string TitleId;

        // Which ux0 subtree this installs into.
        public VitaContentKind Kind;

        // How the item is materialized (tree copy vs pkg extract vs license write).
        public VitaSourceKind SourceKind = VitaSourceKind.Tree;

        // PSN (Pkg) item: the encrypted .pkg to decrypt + extract.
        public string PkgPath;

        // PSN item content-id (from the pkg header / filename). Used for the DLC dir (addcont/<titleId>/<cid>)
        // and the license file name (<cid>.rif).
        public string ContentId;

        // PSN item license source (work.bin / zRIF), present for base + DLC; null for updates. Consumed by the
        // License item written into ux0/license.
        public string WorkBinPath;

        // Parsed APP_VER (from the tree's sce_sys/param.sfo, or the update pkg filename) used ONLY to order
        // updates; never the folder or enumeration order. Version(0,0) when unreadable.
        public Version AppVerParsed = new Version(0, 0);

        // Raw APP_VER string for display, when available.
        public string AppVer;

        public ulong ContentSize;
    }

    // In-memory composite for a single Vita title resolved from the source. Re-derived at scan and install time;
    // nothing here is serialized. Vita is the disc+package family: updates install all-in-order. For NoNpDrm
    // there are no DLC or separate licenses (the work.bin travels inside the app tree); for PSN the DLC and
    // license lists are populated.
    internal sealed class VitaTitle : ICompositeContentSet<VitaContentRef>
    {
        public string TitleId;
        public string Name;
        public string Version;

        // The source container dir (relative to mapping.SourcePath) that holds this title's content.
        public string SourceFolder;

        // Absolute path to the base "app/<id>" tree (NoNpDrm) — used for the icon read / eboot path. Null for
        // PSN titles (the base is a .pkg).
        public string BaseTreeDir;

        // Uncompressed install footprint estimate: base + updates (+ DLC for PSN).
        public ulong InstallSize;

        public VitaContentRef BaseRef;
        public List<VitaContentRef> Updates = new List<VitaContentRef>(); // ascending APP_VER
        public List<VitaContentRef> DlcList = new List<VitaContentRef>();
        public List<VitaContentRef> LicenseList = new List<VitaContentRef>();

        VitaContentRef ICompositeContentSet<VitaContentRef>.Base => BaseRef;
        IReadOnlyList<VitaContentRef> ICompositeContentSet<VitaContentRef>.Updates => Updates;
        IReadOnlyList<VitaContentRef> ICompositeContentSet<VitaContentRef>.Dlc => DlcList;
        IReadOnlyList<VitaContentRef> ICompositeContentSet<VitaContentRef>.Licenses => LicenseList;
    }
}
