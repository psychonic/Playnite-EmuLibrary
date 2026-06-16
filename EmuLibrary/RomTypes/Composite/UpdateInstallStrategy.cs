namespace EmuLibrary.RomTypes
{
    // How a composite-content platform applies its updates on install. The split is per-family:
    // content-store platforms (Switch/3DS/Wii-U) ship cumulative updates, so only the newest matters;
    // disc+package platforms (PS3/PSP/Vita/PS4) ship non-cumulative patches that must all be applied in
    // ascending version order.
    internal enum UpdateInstallStrategy
    {
        InstallLatestUpdateOnly = 0, // Family A — updates are cumulative
        InstallAllUpdatesInOrder = 1 // Family B — updates are non-cumulative
    }
}
