using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // A parsed CIA container. Header is little-endian; the cert chain, ticket and tmd follow at 64-byte-aligned
    // offsets, then the content blob (each content AES-128-CBC encrypted under the titlekey, IV = the content
    // index as a 16-byte big-endian value), then an optional meta block. A CIA carries its own ticket, so no
    // external .tik is needed.
    internal sealed class Cia : IDecryptableTitle
    {
        private const int HeaderSize = 0x2020;
        private const int ContentIndexOffset = 0x20;
        private const int ContentIndexSize = 0x2000;
        private const int Alignment = 0x40;

        private readonly string _path;

        // When the source was a Z3DS-compressed .zcia, _path points at a temp .cia we decompressed and own;
        // it is deleted on Dispose. Null for a plain .cia (nothing to clean up).
        private readonly string _tempPath;

        public ThreeDsTitleMetadata Tmd { get; private set; }
        public ThreeDsTicket Ticket { get; private set; }

        // Raw section bytes, kept so the SD installer can write the tmd / ticket verbatim to disk.
        public byte[] RawTmd { get; private set; }
        public byte[] RawTicket { get; private set; }

        // Absolute file offset of each present content, keyed by its tmd content index.
        private readonly Dictionary<ushort, long> _contentOffsets = new Dictionary<ushort, long>();

        private Cia(string path, string tempPath)
        {
            _path = path;
            _tempPath = tempPath;
        }

        // Opens a .cia, or a Z3DS-compressed .zcia (transparently decompressed to a temp .cia first). A .zcia
        // is detected by its magic, not its extension, so a mislabeled file still works.
        public static Cia Open(string path)
        {
            return Z3ds.IsZ3ds(path)
                ? OpenFromFile(Z3ds.DecompressToTempFile(path, CancellationToken.None), ownsFile: true)
                : OpenFromFile(path, ownsFile: false);
        }

        private static Cia OpenFromFile(string path, bool ownsFile)
        {
            var cia = new Cia(path, ownsFile ? path : null);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var header = ReadExactly(fs, HeaderSize);
                ComputeSections(header, out long ticketOffset, out uint ticketSize,
                    out long tmdOffset, out uint tmdSize, out long contentOffset);

                fs.Seek(ticketOffset, SeekOrigin.Begin);
                cia.RawTicket = ReadExactly(fs, (int)ticketSize);
                cia.Ticket = ThreeDsTicket.Parse(cia.RawTicket);

                fs.Seek(tmdOffset, SeekOrigin.Begin);
                cia.RawTmd = ReadExactly(fs, (int)tmdSize);
                cia.Tmd = ThreeDsTitleMetadata.Parse(cia.RawTmd);

                cia.MapContentOffsets(header, contentOffset);
            }
            return cia;
        }

        // The tmd/ticket parsed from just a CIA prefix — no content mapping, no decrypt. Used by the scan path
        // for .zcia so it can parse a small decompressed prefix instead of materializing the whole .cia.
        public sealed class Metadata
        {
            public ThreeDsTitleMetadata Tmd { get; set; }
            public ThreeDsTicket Ticket { get; set; }
            public byte[] RawTmd { get; set; }
            public byte[] RawTicket { get; set; }
        }

        // The single source of truth for CIA section offsets, shared by Cia.Open and the metadata path so the
        // two cannot drift. The cert chain, ticket and tmd follow the header at 64-byte-aligned offsets;
        // content begins at contentOffset.
        private static void ComputeSections(byte[] header, out long ticketOffset, out uint ticketSize,
            out long tmdOffset, out uint tmdSize, out long contentOffset)
        {
            uint certSize = Le.U32(header, 0x08);
            ticketSize = Le.U32(header, 0x0C);
            tmdSize = Le.U32(header, 0x10);
            // meta size (0x14) and content size (0x18, u64) are not needed to locate sections.

            long certOffset = Align(HeaderSize);
            ticketOffset = Align(certOffset + certSize);
            tmdOffset = Align(ticketOffset + ticketSize);
            contentOffset = Align(tmdOffset + tmdSize);
        }

        // The minimum prefix length (decompressed bytes) needed to parse the tmd/ticket: through the end of the
        // tmd section. Computed from the first HeaderSize bytes of the CIA.
        private static long MetadataLength(byte[] header)
        {
            ComputeSections(header, out _, out _, out long tmdOffset, out uint tmdSize, out _);
            return tmdOffset + tmdSize;
        }

        // The largest metadata prefix we will decompress for a scan before falling back to a full decode. A real
        // CIA's cert+ticket+tmd is well under this; the cap bounds a malformed/hostile header.
        private const long MaxMetadataPrefix = 16 * 1024 * 1024;

        // Parses the tmd/ticket from a seekable CIA prefix (header + cert + ticket + tmd). Does not map or
        // decrypt content. The stream must contain at least MetadataLength(header) bytes.
        public static Metadata ReadMetadata(Stream seekablePrefix)
        {
            seekablePrefix.Seek(0, SeekOrigin.Begin);
            var header = ReadExactly(seekablePrefix, HeaderSize);
            ComputeSections(header, out long ticketOffset, out uint ticketSize,
                out long tmdOffset, out uint tmdSize, out _);

            var md = new Metadata();
            seekablePrefix.Seek(ticketOffset, SeekOrigin.Begin);
            md.RawTicket = ReadExactly(seekablePrefix, (int)ticketSize);
            md.Ticket = ThreeDsTicket.Parse(md.RawTicket);

            seekablePrefix.Seek(tmdOffset, SeekOrigin.Begin);
            md.RawTmd = ReadExactly(seekablePrefix, (int)tmdSize);
            md.Tmd = ThreeDsTitleMetadata.Parse(md.RawTmd);
            return md;
        }

        // Two-stage prefix parse over a forward-only Z3DS reader: grow to the CIA header, learn how much more is
        // needed, grow to the end of the tmd, then parse. Throws if the header demands more than MaxMetadataPrefix
        // (the caller falls back to a full decode).
        public static Metadata ReadMetadata(Z3ds.PrefixReader prefix, CancellationToken ct)
        {
            if (prefix.EnsureAtLeast(HeaderSize, ct) < HeaderSize)
                throw new InvalidDataException("Z3DS payload is shorter than a CIA header.");
            var header = ReadExactly(prefix.Buffer, HeaderSize);

            long need = MetadataLength(header);
            if (need > MaxMetadataPrefix)
                throw new InvalidDataException($"CIA metadata prefix ({need} bytes) exceeds the {MaxMetadataPrefix}-byte cap.");
            if (prefix.EnsureAtLeast(need, ct) < need)
                throw new EndOfStreamException("Z3DS payload ended before the end of the CIA tmd.");

            return ReadMetadata(prefix.Buffer);
        }

        // Parses a .zcia's tmd/ticket from just the decompressed prefix, without materializing the whole .cia.
        public static Metadata ReadMetadataFromZ3ds(string path, CancellationToken ct)
        {
            using (var prefix = Z3ds.OpenPrefix(path))
                return ReadMetadata(prefix, ct);
        }

        // Never decompress past this into a .zcia while reading its SMDH. The main NCCH's ExeFS "icon" sits near
        // the front of the content blob (before the multi-GB RomFS), so a real title is satisfied within a few MB;
        // this bounds a malformed header.
        private const long MaxSmdhPrefix = 64 * 1024 * 1024;

        // Reads a .zcia's SMDH (name + 48x48 icon) by decompressing only the small front prefix that holds the
        // CIA header/ticket/tmd and the main NCCH's ExeFS icon — never the whole (multi-GB) payload, the way
        // Cia.Open (DecryptToTempFile) would. Mirrors the tmd prefix parse, so a first-time scan of a compressed
        // title stays cheap. The main NCCH is content index 0, which sits at the start of the content blob.
        // Returns null on any failure. Fail-soft.
        public static Smdh.Info TryReadSmdhFromZ3ds(string path, ThreeDsKeys keys, CancellationToken ct)
        {
            if (keys == null)
                return null; // the CIA titlekey layer can't be stripped without keys

            try
            {
                using (var prefix = Z3ds.OpenPrefix(path))
                {
                    if (prefix.EnsureAtLeast(HeaderSize, ct) < HeaderSize)
                        return null;
                    var header = ReadExactly(prefix.Buffer, HeaderSize);
                    ComputeSections(header, out long ticketOffset, out uint ticketSize,
                        out long tmdOffset, out uint tmdSize, out long contentOffset);

                    long metaEnd = tmdOffset + tmdSize;
                    if (metaEnd > MaxMetadataPrefix || prefix.EnsureAtLeast(metaEnd, ct) < metaEnd)
                        return null;

                    var pbuf = prefix.Buffer;
                    pbuf.Seek(ticketOffset, SeekOrigin.Begin);
                    var ticket = ThreeDsTicket.Parse(ReadExactly(pbuf, (int)ticketSize));
                    pbuf.Seek(tmdOffset, SeekOrigin.Begin);
                    var tmd = ThreeDsTitleMetadata.Parse(ReadExactly(pbuf, (int)tmdSize));

                    // The main NCCH is content index 0, first in the ascending-index content blob when present.
                    ThreeDsTmdContent boot = null;
                    foreach (var c in tmd.Contents)
                        if (c.Index == 0) { boot = c; break; }
                    if (boot == null || !IsContentPresent(header, 0))
                        return null;

                    var titleKey = ticket.DecryptTitleKey(keys);
                    ulong content0Size = boot.Size;

                    // content index 0 begins at the start of the content blob (contentOffset). Read the icon
                    // region through the titlekey-CBC layer, growing the prefix just far enough for each range.
                    return Smdh.TryReadNcchIcon((offset, length) =>
                    {
                        long need = contentOffset + (((offset + length) + 15) & ~15L);
                        if (need > MaxSmdhPrefix)
                            return Array.Empty<byte>();
                        prefix.EnsureAtLeast(need, ct);
                        return ContentCbc.ReadRange(prefix.Buffer, contentOffset, 0, content0Size, titleKey, offset, length);
                    }, 0, keys);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }
        }

        // Walk the tmd contents that the content-index bitmask marks present, in ascending index order, to
        // assign each its absolute offset in the concatenated content blob.
        private void MapContentOffsets(byte[] header, long contentOffset)
        {
            long offset = contentOffset;
            foreach (var c in Tmd.Contents.OrderBy(c => c.Index))
            {
                if (!IsContentPresent(header, c.Index))
                    continue;
                _contentOffsets[c.Index] = offset;
                offset += (long)c.Size;
            }
        }

        private static bool IsContentPresent(byte[] header, ushort index)
        {
            int byteIndex = ContentIndexOffset + index / 8;
            if (byteIndex >= ContentIndexOffset + ContentIndexSize)
                return false;
            int bit = 0x80 >> (index % 8);
            return (header[byteIndex] & bit) != 0;
        }

        public IEnumerable<ThreeDsTmdContent> PresentContents =>
            Tmd.Contents.Where(c => _contentOffsets.ContainsKey(c.Index));

        // Streams one content out of the CIA with its titlekey CBC layer stripped (still an NCCH whose internal
        // crypto NcchDecryptor must then remove). IV = content index as a 16-byte big-endian value.
        public void DecryptContentTo(ThreeDsTmdContent content, byte[] titleKey, Stream dest, CancellationToken ct,
            IProgress<long> progress = null)
        {
            if (!_contentOffsets.TryGetValue(content.Index, out var start))
                throw new InvalidOperationException($"Content index {content.Index} is not present in \"{_path}\".");

            var iv = new byte[16];
            iv[0] = (byte)(content.Index >> 8);
            iv[1] = (byte)content.Index;

            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    long remaining = (long)content.Size;
                    const int ChunkSize = 1 << 20; // 1 MiB, a multiple of the 16-byte block
                    var enc = new byte[ChunkSize];
                    var plain = new byte[ChunkSize];

                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(ChunkSize, remaining);
                        // CBC requires whole blocks; CIA content is block-aligned.
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

        // Random-access titlekey-CBC read of one content (used to read a title's SMDH cheaply).
        public byte[] ReadDecryptedRange(ThreeDsTmdContent content, byte[] titleKey, long offsetInContent, int length)
        {
            if (!_contentOffsets.TryGetValue(content.Index, out var start))
                throw new InvalidOperationException($"Content index {content.Index} is not present in \"{_path}\".");
            return ContentCbc.ReadRange(_path, start, content.Index, content.Size, titleKey, offsetInContent, length);
        }

        private static long Align(long value) => (value + Alignment - 1) / Alignment * Alignment;

        private static byte[] ReadExactly(Stream s, int count)
        {
            var buffer = new byte[count];
            ReadExactlyInto(s, buffer, count);
            return buffer;
        }

        private static void ReadExactlyInto(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException("Unexpected end of CIA stream.");
                read += n;
            }
        }

        public void Dispose()
        {
            if (_tempPath == null)
                return;
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); }
            catch { /* best-effort cleanup of the decompressed temp .cia */ }
        }
    }
}
