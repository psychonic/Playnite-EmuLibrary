using EmuLibrary.RomTypes.ThreeDs.Crypto;
using Playnite.SDK;
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // Reads the title id and SMDH (authoritative name + 48x48 icon) from a *loose* 3DS image — an NCSD cartridge
    // dump (.cci/.3ds) or a bare NCCH executable (.cxi), plain or Z3DS-compressed (.zcci/.zcxi). Loose files are
    // not content-store packages (no tmd/ticket), so this reads the main NCCH's ExeFS "icon" directly, undoing
    // NCCH crypto with the user's keys when the dump is still encrypted. The icon lives in the ExeFS, which
    // precedes the (multi-GB) RomFS, so only a small front prefix is ever read — cheap even for a compressed cart.
    // Homebrew (.3dsx) and non-NCCH shapes yield null. Fail-soft throughout.
    internal static class ThreeDsLooseInfo
    {
        internal sealed class Result
        {
            public ulong TitleId { get; set; }
            public Smdh.Info Smdh { get; set; }
        }

        // Never read past this into a loose file while hunting for the icon; a real title's ExeFS icon sits within
        // the first few MB. Bounds a malformed header — especially a Z3DS prefix, which would otherwise keep
        // decompressing the whole (multi-GB) payload.
        private const long MaxScanOffset = 64L * 1024 * 1024;

        public static Result TryRead(string path, ThreeDsKeys keys, ILogger logger, CancellationToken ct)
        {
            try
            {
                return Z3ds.IsZ3ds(path)
                    ? TryReadZ3ds(path, keys, ct)
                    : TryReadPlain(path, keys, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[3DS] Failed to read loose info for \"{path}\".");
                return null;
            }
        }

        // Plain (uncompressed) loose file: random-access reads straight from the file.
        private static Result TryReadPlain(string path, ThreeDsKeys keys, CancellationToken ct)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return ReadFromRange((offset, length) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (offset < 0 || length < 0 || offset + length > MaxScanOffset)
                        return Array.Empty<byte>();
                    var buffer = new byte[length];
                    fs.Seek(offset, SeekOrigin.Begin);
                    return Sized(buffer, ReadUpTo(fs, buffer, length));
                }, keys);
            }
        }

        // Z3DS-compressed loose file: forward-only decompression grown just far enough to reach each range (the
        // icon is before the RomFS, so this never materializes the whole payload).
        private static Result TryReadZ3ds(string path, ThreeDsKeys keys, CancellationToken ct)
        {
            using (var prefix = Z3ds.OpenPrefix(path))
            {
                return ReadFromRange((offset, length) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (offset < 0 || length < 0 || offset + length > MaxScanOffset)
                        return Array.Empty<byte>();
                    if (prefix.EnsureAtLeast(offset + length, ct) < offset + length)
                        return Array.Empty<byte>(); // payload ended before this range
                    var buf = prefix.Buffer;
                    buf.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[length];
                    return Sized(buffer, ReadUpTo(buf, buffer, length));
                }, keys);
            }
        }

        // Locates the main NCCH (NCSD partition 0, or the file itself for a bare NCCH), pulls the title id from
        // the container header, and reads its SMDH.
        private static Result ReadFromRange(Func<long, int, byte[]> readAt, ThreeDsKeys keys)
        {
            var magic = readAt(0x100, 4);
            if (magic.Length < 4)
                return null;
            var tag = Encoding.ASCII.GetString(magic, 0, 4);

            long ncchBase;
            ulong titleId;
            if (tag == "NCSD")
            {
                var head = readAt(0, 0x200);
                if (head.Length < 0x200)
                    return null;
                titleId = Le.U64(head, 0x108); // NCSD media id == the cart's application title id
                int mediaUnit = 0x200 << head[0x188 + 6];
                uint part0OffsetMu = Le.U32(head, 0x120); // partition table entry 0: offset, then size
                uint part0SizeMu = Le.U32(head, 0x124);
                if (part0SizeMu == 0)
                    return null; // no main partition
                ncchBase = (long)part0OffsetMu * mediaUnit;
            }
            else if (tag == "NCCH")
            {
                var head = readAt(0, 0x200);
                if (head.Length < 0x200)
                    return null;
                ncchBase = 0;
                titleId = Le.U64(head, 0x108); // NCCH partition/title id
            }
            else
            {
                return null; // not an NCSD/NCCH loose image (e.g. .3dsx homebrew)
            }

            var smdh = Smdh.TryReadNcchIcon(readAt, ncchBase, keys);
            if (titleId == 0 && smdh == null)
                return null;
            return new Result { TitleId = titleId, Smdh = smdh };
        }

        private static byte[] Sized(byte[] buffer, int read)
        {
            if (read != buffer.Length)
                Array.Resize(ref buffer, read);
            return buffer;
        }

        private static int ReadUpTo(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read;
        }
    }
}
