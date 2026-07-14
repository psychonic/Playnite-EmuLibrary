using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using EmuLibrary.Util;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // A 3DS title delivered as a CDN/NUS folder: a "tmd" (or "tmd.<ver>") file plus one file per content named
    // by its 8-hex-digit content id (no extension), with the ticket supplied externally as a .tik. Each content
    // file is the same titlekey-AES-128-CBC-encrypted NCCH a CIA carries (IV = the content index as a 16-byte
    // big-endian value); the difference from a CIA is purely physical layout, so this presents the same
    // IDecryptableTitle surface the SD installer consumes.
    internal sealed class CdnTitle : IDecryptableTitle
    {
        private readonly string _folder;

        // content index -> the on-disk content file path.
        private readonly Dictionary<ushort, string> _contentFiles = new Dictionary<ushort, string>();

        public ThreeDsTitleMetadata Tmd { get; private set; }
        public ThreeDsTicket Ticket { get; private set; }
        public byte[] RawTmd { get; private set; }
        public byte[] RawTicket { get; private set; }

        private CdnTitle(string folder) { _folder = folder; }

        public static CdnTitle Open(string folder, string ticketPath)
        {
            var tmdPath = FindTmd(folder)
                ?? throw new FileNotFoundException($"No tmd file was found in CDN folder \"{folder}\".");

            var cdn = new CdnTitle(folder);
            cdn.RawTmd = File.ReadAllBytes(tmdPath);
            cdn.Tmd = ThreeDsTitleMetadata.Parse(cdn.RawTmd);

            if (string.IsNullOrEmpty(ticketPath) || !File.Exists(ticketPath))
                throw new FileNotFoundException($"No ticket (.tik) was found for CDN folder \"{folder}\".");
            cdn.RawTicket = File.ReadAllBytes(ticketPath);
            cdn.Ticket = ThreeDsTicket.Parse(cdn.RawTicket);

            foreach (var content in cdn.Tmd.Contents)
            {
                if (TryFindContentFile(folder, content, out var path))
                    cdn._contentFiles[content.Index] = path;
            }

            return cdn;
        }

        public IEnumerable<ThreeDsTmdContent> PresentContents =>
            Tmd.Contents.Where(c => _contentFiles.ContainsKey(c.Index));

        public void DecryptContentTo(ThreeDsTmdContent content, byte[] titleKey, Stream dest, CancellationToken ct,
            IProgress<long> progress = null)
        {
            if (!_contentFiles.TryGetValue(content.Index, out var path))
                throw new InvalidOperationException($"Content index {content.Index} is not present in \"{_folder}\".");

            var iv = new byte[16];
            iv[0] = (byte)(content.Index >> 8);
            iv[1] = (byte)content.Index;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                {
                    long remaining = (long)content.Size;
                    const int ChunkSize = 1 << 20; // 1 MiB, a multiple of the 16-byte block
                    var enc = new byte[ChunkSize];
                    var plain = new byte[ChunkSize];

                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(ChunkSize, remaining);
                        // CBC requires whole blocks; NCCH content is block-aligned.
                        int aligned = want - (want % 16);
                        if (aligned == 0)
                            break;
                        ReadExactlyInto(fs, enc, aligned);
                        dec.TransformBlock(enc, 0, aligned, plain, 0);
                        dest.Write(plain, 0, aligned);
                        remaining -= aligned;
                        progress?.Report(aligned);
                    }
                }
            }
        }

        // Random-access titlekey-CBC read of one content (the content file IS the content, so base offset 0).
        public byte[] ReadDecryptedRange(ThreeDsTmdContent content, byte[] titleKey, long offsetInContent, int length)
        {
            if (!_contentFiles.TryGetValue(content.Index, out var path))
                throw new InvalidOperationException($"Content index {content.Index} is not present in \"{_folder}\".");
            return ContentCbc.ReadRange(path, 0, content.Index, content.Size, titleKey, offsetInContent, length);
        }

        // True if every content the tmd lists is present on disk at its expected size. CDN downloads keep one
        // file per content named by the content id; a name with no matching real file (e.g. an interrupted
        // ".part" download left an oddly-named stub) leaves the title incomplete and uninstallable.
        public static bool IsComplete(string folder, ThreeDsTitleMetadata tmd)
        {
            foreach (var content in tmd.Contents)
            {
                if (!TryFindContentFile(folder, content, out _))
                    return false;
            }
            return true;
        }

        // The content file is the content id as 8 lowercase hex digits with no extension, sized exactly as the
        // tmd records. (A partially-downloaded content carries a different name, so it simply won't match here.)
        private static bool TryFindContentFile(string folder, ThreeDsTmdContent content, out string path)
        {
            path = Path.Combine(folder, content.Id.ToString("x8", CultureInfo.InvariantCulture));
            var info = new FileInfo(path);
            if (info.Exists && (ulong)info.Length == content.Size)
                return true;
            path = null;
            return false;
        }

        // The newest tmd in a CDN folder: a bare "tmd" or "tmd.<ver>"; the highest version wins.
        public static string FindTmd(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return null;

            string best = null;
            long bestVer = -1;
            foreach (var file in Directory.EnumerateFiles(folder, "tmd*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("tmd", StringComparison.OrdinalIgnoreCase))
                {
                    if (bestVer < 0) { best = file; bestVer = 0; }
                    continue;
                }
                if (!name.StartsWith("tmd.", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (long.TryParse(name.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ver)
                    && ver > bestVer)
                {
                    best = file;
                    bestVer = ver;
                }
            }
            return best;
        }

        private static void ReadExactlyInto(Stream s, byte[] buffer, int count) => StreamUtils.ReadExactly(s, buffer, count, "CDN content file");

        public void Dispose() { }
    }
}
