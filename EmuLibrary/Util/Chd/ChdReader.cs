using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace EmuLibrary.Util.Chd
{
    // Minimal reader for MAME/chdman CHD **v5** disc images, sufficient to seek + read the handful of
    // logical (uncompressed) sectors near the volume start that hold an ISO9660 filesystem — so a PSP UMD
    // stored as a .chd can have its /PSP_GAME/PARAM.SFO located without decompressing the whole image
    // (important on the SMB scan path). Only the hunks covering the bytes actually read are decompressed.
    //
    // Ported from libchdr (Romain Tisserand, BSD-3-Clause) and the MAME CHD code (BSD-3-Clause) for the v5
    // header, the compressed-map decode (canonical Huffman + RLE over an MSB-first bitstream), and the hunk
    // codec parameters. Hunk codecs implemented: zstd, zlib (raw deflate), lzma (via the public-domain LZMA
    // SDK), and uncompressed/self references — these cover real-world PSP DVD-type CHDs (chdman defaults to
    // zstd). CD-only codecs (cdzl/cdlz/cdfl/cdzs), huff and flac are not implemented (N/A for DVD-type
    // images). Parent CHDs are not supported.
    internal sealed class ChdReader : IDisposable
    {
        private const int V5HeaderLength = 124;
        private const int SectorReadCap = 32 * 1024 * 1024; // guard against a corrupt logical size

        // Compression-type codes in the decoded hunk map (libchdr chd.h).
        private const byte COMPRESSION_TYPE_0 = 0;
        private const byte COMPRESSION_TYPE_1 = 1;
        private const byte COMPRESSION_TYPE_2 = 2;
        private const byte COMPRESSION_TYPE_3 = 3;
        private const byte COMPRESSION_NONE = 4;
        private const byte COMPRESSION_SELF = 5;
        private const byte COMPRESSION_PARENT = 6;
        private const byte COMPRESSION_RLE_SMALL = 7;
        private const byte COMPRESSION_RLE_LARGE = 8;
        private const byte COMPRESSION_SELF_0 = 9;
        private const byte COMPRESSION_SELF_1 = 10;
        private const byte COMPRESSION_PARENT_SELF = 11;
        private const byte COMPRESSION_PARENT_0 = 12;
        private const byte COMPRESSION_PARENT_1 = 13;

        private readonly Stream _stream;
        private readonly bool _ownsStream;

        private readonly uint[] _compressors = new uint[4]; // FourCCs, [0]==0 => uncompressed CHD
        private readonly ulong _logicalBytes;
        private readonly uint _hunkBytes;
        private readonly uint _unitBytes;
        private readonly uint _hunkCount;

        // Decoded per-hunk map. For base/none hunks: file offset + compressed length. For self refs:
        // _hunkOffset holds the referenced hunk index.
        private readonly byte[] _hunkComp;
        private readonly ulong[] _hunkOffset;
        private readonly uint[] _hunkLength;

        // One-hunk decode cache (reads are near-sequential over a few metadata sectors).
        private int _cachedHunk = -1;
        private byte[] _cachedHunkData;

        private ChdReader(Stream stream, bool ownsStream, uint[] compressors, ulong logicalBytes,
            uint hunkBytes, uint unitBytes, uint hunkCount, byte[] hunkComp, ulong[] hunkOffset, uint[] hunkLength)
        {
            _stream = stream;
            _ownsStream = ownsStream;
            Array.Copy(compressors, _compressors, 4);
            _logicalBytes = logicalBytes;
            _hunkBytes = hunkBytes;
            _unitBytes = unitBytes;
            _hunkCount = hunkCount;
            _hunkComp = hunkComp;
            _hunkOffset = hunkOffset;
            _hunkLength = hunkLength;
        }

        public static bool TryOpen(string path, out ChdReader reader)
        {
            reader = null;
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    reader = Open(fs, ownsStream: true);
                    return reader != null;
                }
                catch
                {
                    fs.Dispose();
                    throw;
                }
            }
            catch
            {
                reader = null;
                return false;
            }
        }

        // Parses the v5 header + compressed map. Returns null (not throws) for a non-CHD or a non-v5 CHD so
        // callers can fall back. Throws only on genuinely corrupt data.
        internal static ChdReader Open(Stream stream, bool ownsStream)
        {
            var header = ReadExact(stream, 0, V5HeaderLength);
            if (header == null)
                return null;

            // tag "MComprHD"
            if (Encoding.ASCII.GetString(header, 0, 8) != "MComprHD")
                return null;

            uint headerLen = ReadU32BE(header, 8);
            uint version = ReadU32BE(header, 12);
            if (version != 5 || headerLen != V5HeaderLength)
                return null; // only v5 is supported; older/newer fall back

            var compressors = new uint[4];
            for (int i = 0; i < 4; i++)
                compressors[i] = ReadU32BE(header, 16 + i * 4);

            ulong logicalBytes = ReadU64BE(header, 32);
            ulong mapOffset = ReadU64BE(header, 40);
            uint hunkBytes = ReadU32BE(header, 56);
            uint unitBytes = ReadU32BE(header, 60);

            if (hunkBytes == 0 || unitBytes == 0 || logicalBytes == 0)
                return null;

            if (compressors[0] == 0)
                return null; // uncompressed CHD: different (uncompressed) map format, not needed for PSP

            uint hunkCount = (uint)((logicalBytes + hunkBytes - 1) / hunkBytes);
            if (hunkCount == 0 || hunkCount > 64 * 1024 * 1024)
                return null;

            DecodeCompressedMap(stream, mapOffset, hunkCount, hunkBytes,
                out var hunkComp, out var hunkOffset, out var hunkLength);

            return new ChdReader(stream, ownsStream, compressors, logicalBytes,
                hunkBytes, unitBytes, hunkCount, hunkComp, hunkOffset, hunkLength);
        }

        public ulong Length => _logicalBytes;

        // A seekable, read-only stream over the logical (uncompressed) bytes. Decodes hunks on demand.
        public Stream GetStream() => new ChdLogicalStream(this);

        // Decodes the v5 compressed hunk map (libchdr decompress_v5_map). Produces per-hunk compression
        // type, file offset (or self-ref hunk index), and compressed length.
        private static void DecodeCompressedMap(Stream stream, ulong mapOffset, uint hunkCount, uint hunkBytes,
            out byte[] hunkComp, out ulong[] hunkOffset, out uint[] hunkLength)
        {
            var mapHeader = ReadExact(stream, (long)mapOffset, 16);
            if (mapHeader == null)
                throw new InvalidDataException("CHD: truncated map header.");

            uint mapBytes = ReadU32BE(mapHeader, 0);
            ulong firstOffs = ReadU48BE(mapHeader, 4);
            byte lengthBits = mapHeader[12];
            byte selfBits = mapHeader[13];
            byte parentBits = mapHeader[14];

            var compressed = ReadExact(stream, (long)mapOffset + 16, (int)mapBytes);
            if (compressed == null)
                throw new InvalidDataException("CHD: truncated map data.");

            var bits = new BitReader(compressed);

            // Decode the per-hunk compression-type stream (canonical Huffman over 16 codes, RLE-run encoded).
            var decoder = new HuffmanDecoder(16, 8);
            decoder.ImportTreeRle(bits);

            hunkComp = new byte[hunkCount];
            hunkOffset = new ulong[hunkCount];
            hunkLength = new uint[hunkCount];

            byte lastComp = 0;
            int repCount = 0;
            for (uint hunk = 0; hunk < hunkCount; hunk++)
            {
                if (repCount > 0)
                {
                    hunkComp[hunk] = lastComp;
                    repCount--;
                }
                else
                {
                    byte val = (byte)decoder.DecodeOne(bits);
                    if (val == COMPRESSION_RLE_SMALL)
                    {
                        hunkComp[hunk] = lastComp;
                        repCount = 2 + (int)decoder.DecodeOne(bits);
                    }
                    else if (val == COMPRESSION_RLE_LARGE)
                    {
                        hunkComp[hunk] = lastComp;
                        repCount = 2 + 16 + ((int)decoder.DecodeOne(bits) << 4);
                        repCount += (int)decoder.DecodeOne(bits);
                    }
                    else
                    {
                        hunkComp[hunk] = lastComp = val;
                    }
                }
            }

            // Second pass: resolve offsets + lengths, accumulating base/none offsets from firstOffs.
            ulong curOffset = firstOffs;
            uint lastSelf = 0;
            ulong lastParent = 0;
            for (uint hunk = 0; hunk < hunkCount; hunk++)
            {
                ulong offset = curOffset;
                uint length = 0;

                switch (hunkComp[hunk])
                {
                    case COMPRESSION_TYPE_0:
                    case COMPRESSION_TYPE_1:
                    case COMPRESSION_TYPE_2:
                    case COMPRESSION_TYPE_3:
                        length = bits.Read(lengthBits);
                        curOffset += length;
                        bits.Read(16); // crc16
                        break;

                    case COMPRESSION_NONE:
                        length = hunkBytes;
                        curOffset += length;
                        bits.Read(16); // crc16
                        break;

                    case COMPRESSION_SELF:
                        offset = bits.Read(selfBits);
                        lastSelf = (uint)offset;
                        break;

                    case COMPRESSION_PARENT:
                        offset = bits.Read(parentBits);
                        lastParent = offset;
                        break;

                    case COMPRESSION_SELF_1:
                        lastSelf++;
                        goto case COMPRESSION_SELF_0;
                    case COMPRESSION_SELF_0:
                        hunkComp[hunk] = COMPRESSION_SELF;
                        offset = lastSelf;
                        break;

                    case COMPRESSION_PARENT_SELF:
                        hunkComp[hunk] = COMPRESSION_PARENT;
                        offset = lastParent = (ulong)hunk * hunkBytes / (_unitBytesStatic(hunkBytes));
                        break;

                    case COMPRESSION_PARENT_1:
                        lastParent += hunkBytes / _unitBytesStatic(hunkBytes);
                        goto case COMPRESSION_PARENT_0;
                    case COMPRESSION_PARENT_0:
                        hunkComp[hunk] = COMPRESSION_PARENT;
                        offset = lastParent;
                        break;

                    default:
                        throw new InvalidDataException($"CHD: unknown hunk compression code {hunkComp[hunk]}.");
                }

                hunkOffset[hunk] = offset;
                hunkLength[hunk] = length;
            }
        }

        // The parent-ref pseudo-types need unitBytes, but the map decode above is static and has only
        // hunkBytes to hand. We never read from a parent CHD (unsupported), so these branches never resolve to
        // real reads; use hunkBytes as a harmless divisor to avoid divide-by-zero. Kept explicit so the port
        // reads like libchdr.
        private static uint _unitBytesStatic(uint hunkBytes) => hunkBytes == 0 ? 1 : hunkBytes;

        // Decodes hunk `index` into a hunkBytes-sized buffer (cached). Follows self references.
        private byte[] ReadHunk(int index)
        {
            return ReadHunk(index, 0);
        }

        private byte[] ReadHunk(int index, int selfDepth)
        {
            if (index < 0 || index >= _hunkCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (_cachedHunk == index && _cachedHunkData != null)
                return _cachedHunkData;

            byte[] data;
            byte comp = _hunkComp[index];
            switch (comp)
            {
                case COMPRESSION_TYPE_0:
                case COMPRESSION_TYPE_1:
                case COMPRESSION_TYPE_2:
                case COMPRESSION_TYPE_3:
                    {
                        uint fourcc = _compressors[comp];
                        var raw = ReadExact(_stream, (long)_hunkOffset[index], (int)_hunkLength[index])
                            ?? throw new InvalidDataException("CHD: truncated hunk data.");
                        data = DecodeCodec(fourcc, raw, (int)_hunkBytes);
                        break;
                    }

                case COMPRESSION_NONE:
                    data = ReadExact(_stream, (long)_hunkOffset[index], (int)_hunkBytes)
                        ?? throw new InvalidDataException("CHD: truncated uncompressed hunk.");
                    break;

                case COMPRESSION_SELF:
                    if (selfDepth > 64)
                        throw new InvalidDataException("CHD: self-reference chain too deep.");
                    return ReadHunk((int)_hunkOffset[index], selfDepth + 1);

                case COMPRESSION_PARENT:
                    throw new NotSupportedException("CHD: parent (delta) images are not supported.");

                default:
                    throw new InvalidDataException($"CHD: unresolved hunk compression code {comp}.");
            }

            if (data.Length != _hunkBytes)
                Array.Resize(ref data, (int)_hunkBytes);

            _cachedHunk = index;
            _cachedHunkData = data;
            return data;
        }

        private static byte[] DecodeCodec(uint fourcc, byte[] input, int outputSize)
        {
            switch (FourCc(fourcc))
            {
                case "zstd":
                    {
                        // Decompress into a fixed hunkBytes buffer; chdman's zstd frames don't carry a stored
                        // content size, so decode into the known output size rather than querying the frame.
                        var outBuf = new byte[outputSize];
                        using (var d = new Decompressor())
                            d.Unwrap(input, outBuf);
                        return outBuf;
                    }

                case "zlib":
                    {
                        var outBuf = new byte[outputSize];
                        using (var ms = new MemoryStream(input, false))
                        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                            ReadFully(ds, outBuf);
                        return outBuf;
                    }

                case "lzma":
                    {
                        // CHD stores a raw LZMA1 stream with no props/size header; chdman derives the decoder
                        // properties from the hunk size instead of writing them, so reconstruct them here.
                        var outBuf = new byte[outputSize];
                        var dec = new SevenZip.Compression.LZMA.Decoder();
                        dec.SetDecoderProperties(BuildChdLzmaProps(outputSize));
                        using (var inMs = new MemoryStream(input, false))
                        using (var outMs = new MemoryStream(outBuf, true))
                            dec.Code(inMs, outMs, input.Length, outputSize, null);
                        return outBuf;
                    }

                default:
                    throw new NotSupportedException($"CHD: unsupported hunk codec '{FourCc(fourcc)}'.");
            }
        }

        // Reconstructs the 5-byte LZMA property block chdman would have used for lzma hunks. The CHD lzma codec
        // (MAME/libchdr lzma.c) never stores props — it initializes the encoder with LzmaEncProps level 9 and
        // reduceSize = hunkbytes, leaving lc/lp/pb at their defaults (3/0/2 => props byte 0x5D), and derives the
        // dictionary size from reduceSize via LzmaEncProps_Normalize. The dict size only sizes the decode window
        // (any value >= the max match distance yields identical output), but we mirror the exact normalization
        // so the reconstructed props match chdman's.
        private static byte[] BuildChdLzmaProps(int hunkBytes)
        {
            uint dictSize = 1u << 26; // level 9 default (64 MiB)
            if (hunkBytes > 0 && dictSize > (uint)hunkBytes)
            {
                uint reduce = (uint)hunkBytes;
                for (int i = 11; i <= 30; i++)
                {
                    if (reduce <= (2u << i)) { dictSize = 2u << i; break; }
                    if (reduce <= (3u << i)) { dictSize = 3u << i; break; }
                }
            }

            const int lc = 3, lp = 0, pb = 2;
            return new[]
            {
                (byte)((pb * 5 + lp) * 9 + lc),
                (byte)dictSize,
                (byte)(dictSize >> 8),
                (byte)(dictSize >> 16),
                (byte)(dictSize >> 24),
            };
        }

        private static string FourCc(uint v)
        {
            var b = new byte[4];
            b[0] = (byte)((v >> 24) & 0xff);
            b[1] = (byte)((v >> 16) & 0xff);
            b[2] = (byte)((v >> 8) & 0xff);
            b[3] = (byte)(v & 0xff);
            return Encoding.ASCII.GetString(b);
        }

        public void Dispose()
        {
            _cachedHunkData = null;
            if (_ownsStream)
                _stream.Dispose();
        }

        private static void ReadFully(Stream s, byte[] buf)
        {
            int read = 0;
            while (read < buf.Length)
            {
                int n = s.Read(buf, read, buf.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
        }

        // Reads exactly `length` bytes at absolute `offset`, or null if the stream is too short.
        private static byte[] ReadExact(Stream s, long offset, int length)
        {
            if (length < 0 || length > SectorReadCap)
                return null;
            var buf = new byte[length];
            s.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = s.Read(buf, read, length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read == length ? buf : null;
        }

        private static uint ReadU32BE(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        private static ulong ReadU48BE(byte[] b, int o) =>
            ((ulong)b[o] << 40) | ((ulong)b[o + 1] << 32) | ((ulong)b[o + 2] << 24) |
            ((ulong)b[o + 3] << 16) | ((ulong)b[o + 4] << 8) | b[o + 5];

        private static ulong ReadU64BE(byte[] b, int o) =>
            ((ulong)ReadU32BE(b, o) << 32) | ReadU32BE(b, o + 4);

        // A read-only seekable view over the CHD's logical bytes, decoding hunks lazily via the parent reader.
        private sealed class ChdLogicalStream : Stream
        {
            private readonly ChdReader _chd;
            private long _position;

            public ChdLogicalStream(ChdReader chd)
            {
                _chd = chd;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => (long)_chd._logicalBytes;

            public override long Position
            {
                get => _position;
                set => _position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= Length || count <= 0)
                    return 0;

                long remaining = Length - _position;
                if (count > remaining)
                    count = (int)remaining;

                int total = 0;
                while (count > 0)
                {
                    int hunkIndex = (int)(_position / _chd._hunkBytes);
                    int within = (int)(_position % _chd._hunkBytes);
                    var hunk = _chd.ReadHunk(hunkIndex);

                    int n = Math.Min(count, (int)_chd._hunkBytes - within);
                    Buffer.BlockCopy(hunk, within, buffer, offset, n);

                    offset += n;
                    count -= n;
                    _position += n;
                    total += n;
                }
                return total;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: _position = offset; break;
                    case SeekOrigin.Current: _position += offset; break;
                    case SeekOrigin.End: _position = Length + offset; break;
                }
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // MSB-first bitstream reader (MAME/libchdr bitstream.c). Bits are consumed most-significant first.
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _offset;
            private uint _buffer;
            private int _bits;

            public BitReader(byte[] data)
            {
                _data = data;
            }

            public uint Peek(int numBits)
            {
                if (numBits == 0)
                    return 0;
                while (_bits < numBits)
                {
                    byte next = _offset < _data.Length ? _data[_offset] : (byte)0;
                    _offset++;
                    _buffer |= (uint)next << (24 - _bits);
                    _bits += 8;
                }
                return _buffer >> (32 - numBits);
            }

            public void Remove(int numBits)
            {
                _buffer <<= numBits;
                _bits -= numBits;
            }

            public uint Read(int numBits)
            {
                uint result = Peek(numBits);
                Remove(numBits);
                return result;
            }
        }

        // Canonical Huffman decoder (MAME/libchdr huffman.c), enough for the v5 map's RLE-encoded tree.
        private sealed class HuffmanDecoder
        {
            private readonly int _numCodes;
            private readonly int _maxBits;
            private readonly byte[] _codeLength;
            private readonly uint[] _code;
            private readonly ushort[] _lookup; // (code << 5) | bits

            public HuffmanDecoder(int numCodes, int maxBits)
            {
                _numCodes = numCodes;
                _maxBits = maxBits;
                _codeLength = new byte[numCodes];
                _code = new uint[numCodes];
                _lookup = new ushort[1 << maxBits];
            }

            // Reads the RLE-encoded set of per-code bit lengths, then builds canonical codes + lookup.
            public void ImportTreeRle(BitReader bits)
            {
                int numBits = _maxBits >= 16 ? 5 : (_maxBits >= 8 ? 4 : 3);

                int curNode = 0;
                while (curNode < _numCodes)
                {
                    int nodeBits = (int)bits.Read(numBits);
                    if (nodeBits != 1)
                    {
                        _codeLength[curNode++] = (byte)nodeBits;
                    }
                    else
                    {
                        nodeBits = (int)bits.Read(numBits);
                        if (nodeBits == 1)
                        {
                            _codeLength[curNode++] = (byte)nodeBits;
                        }
                        else
                        {
                            int repCount = (int)bits.Read(numBits) + 3;
                            while (repCount-- > 0 && curNode < _numCodes)
                                _codeLength[curNode++] = (byte)nodeBits;
                        }
                    }
                }

                if (curNode != _numCodes)
                    throw new InvalidDataException("CHD: malformed Huffman tree.");

                AssignCanonicalCodes();
                BuildLookup();
            }

            private void AssignCanonicalCodes()
            {
                var bitHisto = new uint[33];
                for (int c = 0; c < _numCodes; c++)
                {
                    int len = _codeLength[c];
                    if (len > _maxBits)
                        throw new InvalidDataException("CHD: Huffman code length exceeds maxbits.");
                    if (len <= 32)
                        bitHisto[len]++;
                }

                uint curStart = 0;
                for (int len = 32; len > 0; len--)
                {
                    uint nextStart = (curStart + bitHisto[len]) >> 1;
                    if (len != 1 && nextStart * 2 != (curStart + bitHisto[len]))
                        throw new InvalidDataException("CHD: inconsistent Huffman code lengths.");
                    bitHisto[len] = curStart;
                    curStart = nextStart;
                }

                for (int c = 0; c < _numCodes; c++)
                {
                    int len = _codeLength[c];
                    if (len > 0)
                        _code[c] = bitHisto[len]++;
                }
            }

            private void BuildLookup()
            {
                for (int c = 0; c < _numCodes; c++)
                {
                    int len = _codeLength[c];
                    if (len == 0)
                        continue;
                    int shift = _maxBits - len;
                    uint start = _code[c] << shift;
                    uint end = ((_code[c] + 1) << shift) - 1;
                    ushort value = (ushort)((c << 5) | len);
                    for (uint i = start; i <= end; i++)
                        _lookup[i] = value;
                }
            }

            public uint DecodeOne(BitReader bits)
            {
                uint peek = bits.Peek(_maxBits);
                ushort lookup = _lookup[peek];
                bits.Remove(lookup & 0x1f);
                return (uint)(lookup >> 5);
            }
        }
    }
}
