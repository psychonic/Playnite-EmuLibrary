using Newtonsoft.Json;
using System;

namespace EmuLibrary.Util.Ps3
{
    // A computed per-title install footprint (bytes), cached in IScanCache keyed by a composite stamp over
    // the title's PKGs. Lets the exact dev_hdd0 union be reused across scans without persisting — or even
    // re-reading — each PKG's full file manifest; the manifests are held only transiently while one title
    // is being measured. Serializable for Newtonsoft (public prop, parameterless ctor).
    internal sealed class Ps3InstallSize
    {
        public long Bytes { get; set; }
    }

    // What a single source file represents within a PS3 title.
    internal enum Ps3ContentType
    {
        Unknown = 0,
        DiscBase = 1, // encrypted/decrypted .iso or a PS3_GAME folder
        PkgGame = 2,  // base game distributed as a .pkg (CATEGORY HG/DG)
        Update = 3,   // patch .pkg (CATEGORY GD)
        Dlc = 4,      // add-on content .pkg (CATEGORY AC)
        Rap = 5,      // .rap license file (filename == content-id)
    }

    // Serializable per-file scan result, cached in IScanCache (Newtonsoft → public props, parameterless ctor).
    // Aggregation of these into a per-title composite happens in-memory in the scanner.
    internal sealed class Ps3FileInfo
    {
        public string FilePath { get; set; }
        public Ps3ContentType ContentType { get; set; }
        public string TitleId { get; set; }
        public string ContentId { get; set; }
        public string Title { get; set; }
        public string AppVer { get; set; }
        public string TargetAppVer { get; set; }
        public string Category { get; set; }
        public bool IsPatch { get; set; }

        // Parsed APP_VER used only for ordering updates; never persisted.
        [JsonIgnore]
        public Version AppVerParsed => ParseAppVer(AppVer);

        // APP_VER is "NN.NN" (e.g. "01.02"). Compare numerically so "01.10" > "01.02".
        // This is the ONLY thing that orders updates — never the filename.
        public static Version ParseAppVer(string appVer)
        {
            if (!string.IsNullOrWhiteSpace(appVer) && Version.TryParse(appVer.Trim(), out var v))
                return v;
            return new Version(0, 0);
        }

        // Classifies a .pkg. The primary update signal is the PKG metadata patch flag (isPatch =
        // id 0x03 bit 0x10), which matched the NoIntro "(Update)" tag on 400/400 real pkgs across disc, PSN
        // and content sets — and needs no decryption. CATEGORY alone is NOT sufficient (verified against a
        // ~9.5k-pkg labeled corpus: GD spans DLC AND disc-updates; HG spans base PSN games AND PSN updates).
        // Rule:
        //   - isPatch (or, as a fallback when metadata is unreadable, TARGET_APP_VER set) → Update
        //   - else no APP_VER → DLC       (DLC/unlock-keys are ~99.6% empty; a bare CATEGORY=DG pkg is a base)
        //   - else            → PkgGame   (base PSN/disc game, or a trial)
        public static Ps3ContentType Classify(bool isPatch, string category, string appVer, string targetAppVer)
        {
            var cat = (category ?? "").Trim().ToUpperInvariant();
            bool hasApp = !string.IsNullOrWhiteSpace(appVer);
            bool hasTarget = !string.IsNullOrWhiteSpace(targetAppVer);

            if (isPatch || hasTarget)
                return Ps3ContentType.Update;

            if (!hasApp)
                return cat == "DG" ? Ps3ContentType.PkgGame : Ps3ContentType.Dlc;

            return Ps3ContentType.PkgGame;
        }

        // Content-id form is "AAAAAA-TITLEID_NN-XXXXXXXXXXXXXXXX" (e.g. "EP0001-BLES01234_00-...").
        // The title id is the chars after the first '-' up to the '_'. RAP filenames ARE the full content-id.
        public static string TitleIdFromContentId(string contentId)
        {
            if (string.IsNullOrWhiteSpace(contentId))
                return null;

            int dash = contentId.IndexOf('-');
            if (dash < 0 || dash + 1 >= contentId.Length)
                return null;

            int underscore = contentId.IndexOf('_', dash + 1);
            int end = underscore < 0 ? contentId.Length : underscore;
            var titleId = contentId.Substring(dash + 1, end - (dash + 1));
            return string.IsNullOrWhiteSpace(titleId) ? null : titleId;
        }
    }
}
