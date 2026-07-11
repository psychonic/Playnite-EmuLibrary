using EmuLibrary.Util.Chd;
using EmuLibrary.Util.Cso;
using EmuLibrary.Util.Ps3;
using System;
using System.IO;

namespace EmuLibrary.Util.Psp
{
    // The common entry point for reading a PSP image's title id, regardless of container. Every supported
    // container (.chd / .iso / .cso / .zso / .pbp) resolves to the same PARAM.SFO read: for the disc formats
    // the ISO9660 filesystem is walked to /PSP_GAME/PARAM.SFO (reusing Ps3Iso); PBP holds PARAM.SFO as its
    // first segment. The reported title id is the SFO DISC_ID (the field a UMD carries and that update/DLC
    // payload folders are named after), falling back to TITLE_ID for PBP/PSN content.
    internal static class PspImageReader
    {
        private const string PspGameDir = "PSP_GAME";

        public static bool IsSupportedImageExtension(string ext)
        {
            switch (Normalize(ext))
            {
                case "chd":
                case "iso":
                case "cso":
                case "zso":
                case "pbp":
                    return true;
                default:
                    return false;
            }
        }

        // Reads the title id from an image, or returns false if it can't be determined. Never throws.
        public static bool TryReadTitleId(string path, out string titleId)
        {
            titleId = null;
            if (!TryReadParamSfo(path, out var sfo) || sfo == null)
                return false;

            var id = NullIfEmpty(sfo.Get("DISC_ID")) ?? NullIfEmpty(sfo.TitleId);
            if (id == null)
                return false;

            titleId = id;
            return true;
        }

        // Reads the image's PARAM.SFO (for callers that also want TITLE / CATEGORY / APP_VER). Never throws.
        public static bool TryReadParamSfo(string path, out ParamSfo sfo)
        {
            sfo = null;
            try
            {
                var ext = Normalize(Path.GetExtension(path));
                switch (ext)
                {
                    case "chd":
                        return TryReadFromChd(path, out sfo);
                    case "cso":
                    case "zso":
                        return TryReadFromCso(path, out sfo);
                    case "iso":
                        return TryReadFromIso(path, out sfo);
                    case "pbp":
                        return TryReadFromPbp(path, out sfo);
                    default:
                        return false;
                }
            }
            catch
            {
                sfo = null;
                return false;
            }
        }

        // Reads the image's embedded ICON0.PNG (the PSP title icon) as raw PNG bytes, or returns false if there
        // is none. For the disc formats it lives at /PSP_GAME/ICON0.PNG in the same ISO9660 filesystem as
        // PARAM.SFO (reusing Ps3Iso); for a PBP it's the container's second segment. Never throws.
        public static bool TryReadIcon(string path, out byte[] png)
        {
            png = null;
            try
            {
                var ext = Normalize(Path.GetExtension(path));
                switch (ext)
                {
                    case "chd":
                        {
                            if (!ChdReader.TryOpen(path, out var chd) || chd == null)
                                return false;
                            using (chd)
                            using (var stream = chd.GetStream())
                                return Ps3Iso.TryReadIcon(stream, PspGameDir, out png);
                        }
                    case "cso":
                    case "zso":
                        {
                            if (!CsoReader.TryOpen(path, out var cso) || cso == null)
                                return false;
                            using (cso)
                            using (var stream = cso.GetStream())
                                return Ps3Iso.TryReadIcon(stream, PspGameDir, out png);
                        }
                    case "iso":
                        {
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                                return Ps3Iso.TryReadIcon(fs, PspGameDir, out png);
                        }
                    case "pbp":
                        return PbpReader.TryReadIcon0Bytes(path, out png);
                    default:
                        return false;
                }
            }
            catch
            {
                png = null;
                return false;
            }
        }

        private static bool TryReadFromChd(string path, out ParamSfo sfo)
        {
            sfo = null;
            if (!ChdReader.TryOpen(path, out var chd) || chd == null)
                return false;
            using (chd)
            using (var stream = chd.GetStream())
                return Ps3Iso.TryReadParamSfo(stream, PspGameDir, out sfo);
        }

        private static bool TryReadFromCso(string path, out ParamSfo sfo)
        {
            sfo = null;
            if (!CsoReader.TryOpen(path, out var cso) || cso == null)
                return false;
            using (cso)
            using (var stream = cso.GetStream())
                return Ps3Iso.TryReadParamSfo(stream, PspGameDir, out sfo);
        }

        private static bool TryReadFromIso(string path, out ParamSfo sfo)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Ps3Iso.TryReadParamSfo(fs, PspGameDir, out sfo);
        }

        private static bool TryReadFromPbp(string path, out ParamSfo sfo)
        {
            sfo = null;
            if (!PbpReader.TryReadParamSfoBytes(path, out var bytes))
                return false;
            return ParamSfo.TryParse(bytes, out sfo);
        }

        private static string Normalize(string ext) =>
            string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToLowerInvariant();

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
