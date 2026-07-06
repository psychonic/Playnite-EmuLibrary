using System;
using System.IO;
using System.Threading;
using ZstdSharp;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // A Z3DS-compressed 3DS file (.zcia/.zcci/.zcxi/.z3dsx — Azahar's container, NOT the repo's Cemu ZArchive).
    // Layout (header is little-endian, 0x20 bytes):
    //   0x00 magic[4]            = 'Z','3','D','S'
    //   0x04 underlying_magic[4] = the original file's magic (informational)
    //   0x08 version (u8)        = 1
    //   0x09 reserved (u8)
    //   0x0A header_size (u16)   = 0x20
    //   0x0C metadata_size (u32) = size of the (16-byte-aligned) metadata block following the header
    //   0x10 compressed_size (u64)
    //   0x18 uncompressed_size (u64)
    // The seekable-zstd payload begins at header_size + metadata_size. Seekable zstd is just standard zstd
    // frames concatenated with a trailing skippable seek-table frame; ZstdSharp's DecompressionStream decodes
    // the concatenated frames and skips the skippable frame, so a sequential read recovers the original bytes
    // (the seek table is unused). We only decompress when *we* must parse the content (a .zcia, fed to Cia);
    // loose .z* variants Azahar reads natively and are copied verbatim.
    internal static class Z3ds
    {
        public const int HeaderSize = 0x20;

        // 'Z','3','D','S' read little-endian as a u32 (0x53443350).
        private static readonly uint MagicZ3ds =
            'Z' | ((uint)'3' << 8) | ((uint)'D' << 16) | ((uint)'S' << 24);

        internal struct Header
        {
            public uint UnderlyingMagic;
            public ushort StructHeaderSize;
            public uint MetadataSize;
            public ulong CompressedSize;
            public ulong UncompressedSize;
            public long PayloadOffset;
        }

        // True if `path` begins with the Z3DS magic (a cheap 4-byte sniff; safe on short/missing files).
        public static bool IsZ3ds(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var magic = new byte[4];
                    if (ReadInto(fs, magic, 4) != 4)
                        return false;
                    return Le.U32(magic, 0) == MagicZ3ds;
                }
            }
            catch
            {
                return false;
            }
        }

        // Decompresses a .zcia (or any Z3DS file) into a freshly created temp file, returning its path. The
        // caller owns the temp file and must delete it. The recovered bytes are the original cia/ncch/3dsx.
        public static string DecompressToTempFile(string path, CancellationToken ct)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N") + ".cia");
            try
            {
                using (var dest = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    DecompressTo(path, dest, ct);
                return tmp;
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        // Stream-decompresses the Z3DS payload of `path` into `dest`.
        public static void DecompressTo(string path, Stream dest, CancellationToken ct)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var header = ReadHeader(fs);
                fs.Seek(header.PayloadOffset, SeekOrigin.Begin);

                using (var zstd = new DecompressionStream(fs))
                {
                    var buffer = new byte[1 << 20]; // 1 MiB
                    int n;
                    while ((n = zstd.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dest.Write(buffer, 0, n);
                    }
                }
            }
        }

        // Opens a Z3DS file for prefix (forward-only) decompression — used by the scan path to read just the
        // CIA header/tmd of a .zcia without materializing the whole payload. The install path keeps
        // DecompressToTempFile (a faithful full decode).
        public static PrefixReader OpenPrefix(string path)
        {
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = ReadHeader(fs);
                fs.Seek(header.PayloadOffset, SeekOrigin.Begin);
                return new PrefixReader(fs, new DecompressionStream(fs));
            }
            catch
            {
                fs?.Dispose();
                throw;
            }
        }

        // Decompresses a Z3DS payload on demand into a growing, seekable in-memory buffer, so a caller can read
        // a small header, learn how many more bytes it needs, and pull exactly those — all from one forward-only
        // zstd stream. The scan path needs only the CIA tmd, not the (potentially multi-GB) content, so it never
        // grows past the tmd. Forward-only: never re-open or seek the underlying stream mid-parse.
        internal sealed class PrefixReader : IDisposable
        {
            private readonly FileStream _file;
            private readonly DecompressionStream _zstd;
            private readonly MemoryStream _buffer = new MemoryStream();
            private bool _eof;

            internal PrefixReader(FileStream file, DecompressionStream zstd)
            {
                _file = file;
                _zstd = zstd;
            }

            // Decompressed bytes buffered so far.
            public long Available => _buffer.Length;

            // Pulls from the zstd stream until at least `count` bytes are buffered (or EOF). Returns the new
            // buffered length (which may be < count at EOF).
            public long EnsureAtLeast(long count, CancellationToken ct)
            {
                if (_buffer.Length >= count || _eof)
                    return _buffer.Length;

                _buffer.Seek(0, SeekOrigin.End);
                var chunk = new byte[1 << 16]; // 64 KiB
                while (_buffer.Length < count)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = _zstd.Read(chunk, 0, chunk.Length);
                    if (n <= 0)
                    {
                        _eof = true;
                        break;
                    }
                    _buffer.Write(chunk, 0, n);
                }
                return _buffer.Length;
            }

            // A seekable view of the bytes buffered so far, positioned at the start. Ownership stays with the
            // reader; the caller may read/seek but must not dispose it.
            public Stream Buffer
            {
                get
                {
                    _buffer.Seek(0, SeekOrigin.Begin);
                    return _buffer;
                }
            }

            public void Dispose()
            {
                _zstd.Dispose();
                _file.Dispose();
                _buffer.Dispose();
            }
        }

        internal static Header ReadHeader(Stream s)
        {
            var head = new byte[HeaderSize];
            if (ReadInto(s, head, HeaderSize) != HeaderSize || Le.U32(head, 0x00) != MagicZ3ds)
                throw new InvalidDataException("Not a Z3DS file (bad magic or truncated header).");

            var header = new Header
            {
                UnderlyingMagic = Le.U32(head, 0x04),
                StructHeaderSize = Le.U16(head, 0x0A),
                MetadataSize = Le.U32(head, 0x0C),
                CompressedSize = Le.U64(head, 0x10),
                UncompressedSize = Le.U64(head, 0x18),
            };
            // header_size should be 0x20; fall back to it if a writer left the field zero.
            long headerSize = header.StructHeaderSize != 0 ? header.StructHeaderSize : HeaderSize;
            header.PayloadOffset = headerSize + header.MetadataSize;
            return header;
        }

        private static int ReadInto(Stream s, byte[] buffer, int count)
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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
