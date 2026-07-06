namespace EmuLibrary.RomTypes.ThreeDs
{
    // How a 3DS title's base is laid out at the source, which decides the install primitive. ContentStore
    // titles (CIA / CDN folder) are decrypted into Azahar's SD title store; LooseFile titles
    // (.3ds/.cci/.cxi/.3dsx + z-variants) are bootable as-is and just copied to DestinationPath. This is the
    // load-bearing locator stored on ThreeDsGameInfo.
    internal enum ThreeDsBaseKind
    {
        ContentStore = 0,
        LooseFile = 1,
    }

    // The concrete on-disk shape of a single content unit at the source. Not serialized — re-derived each
    // scan/install.
    internal enum ThreeDsSourceKind
    {
        // A loose bootable file Azahar reads directly (.3ds/.cci/.cxi/.3dsx). Copied verbatim.
        LooseFile = 0,

        // A loose Z3DS-wrapped bootable (.zcci/.zcxi/.z3dsx). Azahar sniffs Z3DS, so it is also copied verbatim.
        Z3dsLoose = 1,

        // A .cia / .zcia package (one title). Content-store import.
        Cia = 2,

        // A CDN folder (tmd.<ver> + content files named by hex content id). Content-store import.
        CdnFolder = 3,
    }
}
