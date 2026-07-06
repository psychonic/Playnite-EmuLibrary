namespace EmuLibrary.RomTypes
{
    // How a composite-content platform applies its updates on install. The strategy is independent of the
    // base install primitive: content-store platforms (Switch/3DS/Wii-U) ship cumulative updates, so only the
    // newest matters; disc+package platforms (PS3/PSP/Vita/PS4) ship non-cumulative patches that must all be
    // applied in ascending version order. Xbox 360 (Xenia) is a third combination — a disc/package base (copied
    // to DestinationPath, no license slot) with cumulative latest-only updates — so it pairs a disc-style base
    // with InstallLatestUpdateOnly.
    internal enum UpdateInstallStrategy
    {
        InstallLatestUpdateOnly = 0, // Family A (+ Xbox 360) — updates are cumulative
        InstallAllUpdatesInOrder = 1 // Family B — updates are non-cumulative
    }
}
