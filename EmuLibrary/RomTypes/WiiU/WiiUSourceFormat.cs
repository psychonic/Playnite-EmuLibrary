namespace EmuLibrary.RomTypes.WiiU
{
    // The on-disk shapes a Wii U title can take in a source/destination folder. Cemu can launch Wua, Wux,
    // Wud and Loadiine directly (see Cemu -g); Nus (encrypted .app/tmd/tik dumps) is not directly playable
    // and must be converted to a .wua to install.
    internal enum WiiUSourceFormat
    {
        // NUS / WUP dump: a folder of encrypted *.app + title.tmd + title.tik (+ title.cert). One folder is
        // one content unit (base, update or DLC), distinguished by the title-id high word.
        Nus = 0,

        // Compressed Wii U disc image (sector-dedup over a .wud) plus a sibling .key (the per-disc key).
        Wux = 1,

        // Raw Wii U disc image plus a sibling .key.
        Wud = 2,

        // Decrypted loadiine layout: a folder containing code/ content/ meta/ (the base game already
        // decrypted; updates/DLC are separate NUS or sibling folders).
        Loadiine = 3,

        // Cemu's compressed single-file archive (ZArchive). Self-contained: bundles base + update + DLC.
        Wua = 4,
    }

    // What a single content unit is, relative to its base title. Derived from the Wii U title-id high word
    // (0x00050000 = game, 0x0005000E = update, 0x0005000C = DLC).
    internal enum WiiUContentKind
    {
        Game = 0,
        Update = 1,
        Dlc = 2,
    }
}
