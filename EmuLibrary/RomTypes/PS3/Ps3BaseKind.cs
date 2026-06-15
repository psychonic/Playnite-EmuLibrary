namespace EmuLibrary.RomTypes.Ps3
{
    // How a PS3 title's base content is stored/installed. Persisted in Ps3GameInfo — keep values stable.
    internal enum Ps3BaseKind
    {
        Disc = 0, // encrypted/decrypted .iso (+ .dkey) or a PS3_GAME folder, copied to DestinationPath
        Pkg = 1,  // base game .pkg, natively decrypted/extracted into dev_hdd0/game/<TITLE_ID>
    }
}
