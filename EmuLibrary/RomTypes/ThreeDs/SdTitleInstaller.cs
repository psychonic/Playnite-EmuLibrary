using EmuLibrary.RomTypes.ThreeDs.Crypto;
using Playnite.SDK;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // Writes a decrypted title into Azahar's SD content store. For each tmd
    // content: strip the CIA titlekey CBC layer, fully decrypt the NCCH to no_crypto, and write
    // "<contentDir>\<contentId:08x>.app" (DLC optional content goes under a "00000000" subfolder). The tmd is
    // saved as "00000000.tmd"; the ticket is written to NAND (best-effort, not load-bearing for boot).
    // Idempotent — re-installing overwrites.
    internal sealed class SdTitleInstaller
    {
        private readonly AzaharPaths _paths;
        private readonly ThreeDsKeys _keys;
        private readonly ILogger _logger;

        public SdTitleInstaller(AzaharPaths paths, ThreeDsKeys keys, ILogger logger)
        {
            _paths = paths;
            _keys = keys;
            _logger = logger;
        }

        // The result of installing one content unit: the title id it landed under and, for a base, the bootable
        // .app path (the index-0 content) Azahar launches.
        public sealed class Result
        {
            public ulong TitleId;
            public string BootAppPath;
        }

        // Installs one source content unit (a .cia file or a CDN folder) into the SD store. Dispatches on the
        // unit's source shape to open the right IDecryptableTitle, then runs the shared install. When supplied,
        // progress reports INCREMENTAL bytes across the titlekey-CBC strip and the NCCH decrypt of every content
        // (they sum to ~2× the unit's total content size — the total the caller sizes the bar to).
        public Result InstallUnit(ThreeDsContentRef unit, CancellationToken ct, IProgress<long> progress = null)
        {
            using (var title = OpenTitle(unit))
                return Install(title, ct, progress);
        }

        // Opens the right IDecryptableTitle for a source content unit. Internal so the scanner can reuse it to
        // read a title's SMDH without duplicating the dispatch.
        internal static IDecryptableTitle OpenTitle(ThreeDsContentRef unit)
        {
            switch (unit.SourceKind)
            {
                case ThreeDsSourceKind.Cia:
                    return Cia.Open(unit.SourcePath);
                case ThreeDsSourceKind.CdnFolder:
                    return CdnTitle.Open(unit.SourcePath, unit.TicketPath);
                default:
                    throw new NotSupportedException($"Source kind {unit.SourceKind} is not a content-store unit.");
            }
        }

        private Result Install(IDecryptableTitle source, CancellationToken ct, IProgress<long> progress = null)
        {
            var tmd = source.Tmd;
            var titleKey = source.Ticket.DecryptTitleKey(_keys);

            var contentDir = _paths.ContentDir(tmd.TitleId);
            Directory.CreateDirectory(contentDir);

            string bootApp = null;
            string firstApp = null;

            foreach (var content in source.PresentContents)
            {
                ct.ThrowIfCancellationRequested();

                var dir = content.IsOptional ? Path.Combine(contentDir, "00000000") : contentDir;
                Directory.CreateDirectory(dir);

                var appPath = Path.Combine(dir, $"{content.Id:x8}.app");
                var tmpPath = appPath + ".tmp";

                using (var dest = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.DecryptContentTo(content, titleKey, dest, ct, progress);

                NcchDecryptor.DecryptFile(tmpPath, _keys, ct, progress);

                if (File.Exists(appPath))
                    File.Delete(appPath);
                File.Move(tmpPath, appPath);

                if (firstApp == null)
                    firstApp = appPath;
                if (content.Index == 0)
                    bootApp = appPath;
            }

            File.WriteAllBytes(Path.Combine(contentDir, "00000000.tmd"), source.RawTmd);
            TryWriteTicket(tmd.TitleId, source);

            return new Result { TitleId = tmd.TitleId, BootAppPath = bootApp ?? firstApp };
        }

        private void TryWriteTicket(ulong titleId, IDecryptableTitle source)
        {
            try
            {
                var ticketPath = _paths.TicketPath(titleId, source.Ticket.TicketId);
                Directory.CreateDirectory(Path.GetDirectoryName(ticketPath));
                File.WriteAllBytes(ticketPath, source.RawTicket);
            }
            catch (Exception ex)
            {
                // The SD-installed content is already fully decrypted, so the NCCH loader never consults the
                // ticket; a failure here does not block booting.
                _logger?.Warn(ex, $"[3DS] Could not write ticket for title {titleId:X16}.");
            }
        }

        // Removes a title's SD directory (base/update/DLC each have their own). Best-effort.
        public void UninstallTitle(ulong titleId)
        {
            var titleDir = _paths.TitleDir(titleId);
            if (Directory.Exists(titleDir))
                Directory.Delete(titleDir, true);
        }
    }
}
