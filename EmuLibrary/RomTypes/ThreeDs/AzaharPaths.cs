using System;
using System.IO;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // Resolves Azahar's on-disk data locations (SD title store, NAND, sysdata) from the emulator's install
    // directory, mirroring Azahar's own order:
    //   1. user dir = portable "<exe>\user\" if it exists, else "%APPDATA%\Azahar\"
    //   2. if "<userDir>\config\qt-config.ini" has [Data%20Storage] use_custom_storage=true, use its
    //      sdmc_directory / nand_directory; otherwise "<userDir>\sdmc" and "<userDir>\nand".
    //
    // The SD title store path is fully deterministic — ID0/ID1 are hardcoded to 32 zeros in Azahar.
    internal sealed class AzaharPaths
    {
        private const string ZeroId = "00000000000000000000000000000000";

        public string UserDir { get; private set; }
        public string SdmcDir { get; private set; }
        public string NandDir { get; private set; }

        // SD title store root: "<sdmc>\Nintendo 3DS\<id0>\<id1>\title".
        public string SdTitleRoot { get; private set; }

        // "<userDir>\sysdata\aes_keys.txt" — the primary key-file location.
        public string SysDataKeysPath { get; private set; }

        public static AzaharPaths Resolve(string emulatorBaseDir)
        {
            var paths = new AzaharPaths();

            string userDir = null;
            if (!string.IsNullOrEmpty(emulatorBaseDir))
            {
                var portable = Path.Combine(emulatorBaseDir, "user");
                if (Directory.Exists(portable))
                    userDir = portable;
            }
            if (userDir == null)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                userDir = Path.Combine(appData, "Azahar");
            }

            paths.UserDir = userDir;
            paths.SysDataKeysPath = Path.Combine(userDir, "sysdata", "aes_keys.txt");

            string sdmc = Path.Combine(userDir, "sdmc");
            string nand = Path.Combine(userDir, "nand");
            ApplyCustomStorage(Path.Combine(userDir, "config", "qt-config.ini"), ref sdmc, ref nand);

            paths.SdmcDir = sdmc;
            paths.NandDir = nand;
            paths.SdTitleRoot = Path.Combine(sdmc, "Nintendo 3DS", ZeroId, ZeroId, "title");
            return paths;
        }

        // The content directory for a title id: "<SdTitleRoot>\<high8>\<low8>\content".
        public string ContentDir(ulong titleId)
        {
            uint high = (uint)(titleId >> 32);
            uint low = (uint)(titleId & 0xFFFFFFFF);
            return Path.Combine(SdTitleRoot, high.ToString("x8"), low.ToString("x8"), "content");
        }

        // The title directory (parent of content) for a title id.
        public string TitleDir(ulong titleId)
        {
            uint high = (uint)(titleId >> 32);
            uint low = (uint)(titleId & 0xFFFFFFFF);
            return Path.Combine(SdTitleRoot, high.ToString("x8"), low.ToString("x8"));
        }

        // The NAND ticket path for a title: "<nand>\dbs\ticket.db\<titleId16>.<ticketId16>.tik".
        public string TicketPath(ulong titleId, ulong ticketId) =>
            Path.Combine(NandDir, "dbs", "ticket.db", $"{titleId:X16}.{ticketId:X16}.tik");

        // A title is considered installed when its content dir holds the 00000000.tmd written at install time.
        public bool IsTitleInstalled(ulong titleId) =>
            File.Exists(Path.Combine(ContentDir(titleId), "00000000.tmd"));

        // Minimal qt-config.ini reader for the [Data%20Storage] custom-storage override. qt-config writes URL
        // -encoded section names and may quote values; only the three keys we need are handled.
        private static void ApplyCustomStorage(string configPath, ref string sdmc, ref string nand)
        {
            if (!File.Exists(configPath))
                return;

            try
            {
                bool inSection = false;
                bool useCustom = false;
                string customSdmc = null;
                string customNand = null;

                foreach (var rawLine in File.ReadAllLines(configPath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith(";"))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        var section = line.Substring(1, line.Length - 2);
                        inSection = section.Equals("Data%20Storage", StringComparison.OrdinalIgnoreCase)
                            || section.Equals("Data Storage", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inSection)
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim().Trim('"');

                    if (key.Equals("use_custom_storage", StringComparison.OrdinalIgnoreCase))
                        useCustom = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("sdmc_directory", StringComparison.OrdinalIgnoreCase))
                        customSdmc = value;
                    else if (key.Equals("nand_directory", StringComparison.OrdinalIgnoreCase))
                        customNand = value;
                }

                if (useCustom)
                {
                    if (!string.IsNullOrEmpty(customSdmc))
                        sdmc = customSdmc;
                    if (!string.IsNullOrEmpty(customNand))
                        nand = customNand;
                }
            }
            catch { /* fall back to the default sdmc/nand under the user dir */ }
        }
    }
}
