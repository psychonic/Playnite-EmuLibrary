namespace EmuLibrary.RomTypes.Xbox360
{
    // How an Xbox 360 title's base content is laid out in the source. Numbering is serialized in the
    // Xbox360GameInfo proto — do not renumber.
    internal enum Xbox360BaseKind
    {
        DiscImage = 0,      // an XGD .iso copied verbatim to DestinationPath
        DigitalPackage = 1, // a digital STFS container (XBLA/GoD/App)
        ZarArchive = 2,     // a Xenia .zar (ZArchive of an extracted, decrypted dump) copied verbatim
    }
}
