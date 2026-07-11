using System;
using System.IO;
using System.IO.Compression;

namespace EmuLibrary.Util.Cso
{
    // Minimal reader for CISO (.cso) and ZISO (.zso) block-compressed PSP images, exposing a seekable view
    // over the logical (uncompressed) bytes so an ISO9660 walk can locate /PSP_GAME/PARAM.SFO without
    // decompressing the whole image. Only the blocks covering the bytes actually read are decompressed.
    //
    // Format (maxcso / CISO): a fixed-size header followed by (numBlocks + 1) little-endian uint32 index
    // entries. Each entry's low 31 bits are the block's file offset >> align; bit 31 marks an uncompressed
    // block. A compressed block is raw DEFLATE (CISO) or a raw LZ4 block (ZISO). CSOv2's per-block method
    // mixing is not implemented (v1 covers the common case).
    internal sealed class CsoReader : IDisposable
    {
        private const uint MagicCiso = 0x4F534943; // "CISO" little-endian
        private const uint MagicZiso = 0x4F53495A; // "ZISO" little-endian

        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly bool _isZiso; // blocks are LZ4 rather than DEFLATE
        private readonly ulong _totalBytes;
        private readonly uint _blockSize;
        private readonly int _align;
        private readonly uint[] _index; // numBlocks + 1 entries

        private int _cachedBlock = -1;
        private byte[] _cachedBlockData;

        private CsoReader(Stream stream, bool ownsStream, bool isZiso, ulong totalBytes, uint blockSize, int align, uint[] index)
        {
            _stream = stream;
            _ownsStream = ownsStream;
            _isZiso = isZiso;
            _totalBytes = totalBytes;
            _blockSize = blockSize;
            _align = align;
            _index = index;
        }

        public static bool TryOpen(string path, out CsoReader reader)
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

        internal static CsoReader Open(Stream stream, bool ownsStream)
        {
            var header = ReadExact(stream, 0, 24);
            if (header == null)
                return null;

            uint magic = ReadU32LE(header, 0);
            bool isZiso;
            if (magic == MagicCiso)
                isZiso = false;
            else if (magic == MagicZiso)
                isZiso = true;
            else
                return null;

            ulong totalBytes = ReadU64LE(header, 8);
            uint blockSize = ReadU32LE(header, 16);
            int align = header[21];

            if (blockSize == 0 || totalBytes == 0 || blockSize > 1 << 20)
                return null;

            ulong numBlocks64 = (totalBytes + blockSize - 1) / blockSize;
            if (numBlocks64 == 0 || numBlocks64 > 64 * 1024 * 1024)
                return null;
            int numBlocks = (int)numBlocks64;

            var indexBytes = ReadExact(stream, 24, (numBlocks + 1) * 4);
            if (indexBytes == null)
                return null;

            var index = new uint[numBlocks + 1];
            for (int i = 0; i <= numBlocks; i++)
                index[i] = ReadU32LE(indexBytes, i * 4);

            return new CsoReader(stream, ownsStream, isZiso, totalBytes, blockSize, align, index);
        }

        public ulong Length => _totalBytes;

        public Stream GetStream() => new CsoLogicalStream(this);

        private byte[] ReadBlock(int index)
        {
            if (_cachedBlock == index && _cachedBlockData != null)
                return _cachedBlockData;

            uint entry = _index[index];
            uint nextEntry = _index[index + 1];
            bool plain = (entry & 0x80000000u) != 0;

            long offset = (long)(entry & 0x7FFFFFFFu) << _align;
            long nextOffset = (long)(nextEntry & 0x7FFFFFFFu) << _align;
            int compLen = (int)(nextOffset - offset);
            if (compLen <= 0)
                throw new InvalidDataException("CSO: non-positive block length.");

            var raw = ReadExact(_stream, offset, compLen)
                ?? throw new InvalidDataException("CSO: truncated block data.");

            byte[] data;
            if (plain || compLen >= _blockSize)
            {
                // Stored uncompressed (either flagged, or a block that didn't compress smaller).
                data = new byte[_blockSize];
                Buffer.BlockCopy(raw, 0, data, 0, Math.Min(raw.Length, (int)_blockSize));
            }
            else if (_isZiso)
            {
                data = Lz4.DecompressBlock(raw, (int)_blockSize);
            }
            else
            {
                data = new byte[_blockSize];
                using (var ms = new MemoryStream(raw, false))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    ReadFully(ds, data);
            }

            _cachedBlock = index;
            _cachedBlockData = data;
            return data;
        }

        public void Dispose()
        {
            _cachedBlockData = null;
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

        private static byte[] ReadExact(Stream s, long offset, int length)
        {
            if (length < 0 || length > 64 * 1024 * 1024)
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

        private static uint ReadU32LE(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static ulong ReadU64LE(byte[] b, int o) =>
            ReadU32LE(b, o) | ((ulong)ReadU32LE(b, o + 4) << 32);

        private sealed class CsoLogicalStream : Stream
        {
            private readonly CsoReader _cso;
            private long _position;

            public CsoLogicalStream(CsoReader cso)
            {
                _cso = cso;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => (long)_cso._totalBytes;

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
                    int blockIndex = (int)(_position / _cso._blockSize);
                    int within = (int)(_position % _cso._blockSize);
                    var block = _cso.ReadBlock(blockIndex);

                    int n = Math.Min(count, (int)_cso._blockSize - within);
                    Buffer.BlockCopy(block, within, buffer, offset, n);

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
    }

    // Decoder for a single raw LZ4 block (no frame header), sufficient for ZISO block payloads.
    internal static class Lz4
    {
        public static byte[] DecompressBlock(byte[] src, int outputSize)
        {
            var dst = new byte[outputSize];
            int sPos = 0, dPos = 0;
            int sLen = src.Length;

            while (sPos < sLen)
            {
                int token = src[sPos++];

                // Literals.
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    int b;
                    do { b = src[sPos++]; litLen += b; } while (b == 255);
                }

                if (litLen > 0)
                {
                    if (sPos + litLen > sLen || dPos + litLen > outputSize)
                    {
                        int copy = Math.Min(litLen, Math.Min(sLen - sPos, outputSize - dPos));
                        Buffer.BlockCopy(src, sPos, dst, dPos, copy);
                        dPos += copy;
                        return dst; // reached the end of the block
                    }
                    Buffer.BlockCopy(src, sPos, dst, dPos, litLen);
                    sPos += litLen;
                    dPos += litLen;
                }

                if (sPos >= sLen)
                    break;

                // Match.
                int offset = src[sPos] | (src[sPos + 1] << 8);
                sPos += 2;
                if (offset == 0)
                    throw new InvalidDataException("LZ4: zero match offset.");

                int matchLen = token & 0x0F;
                if (matchLen == 15)
                {
                    int b;
                    do { b = src[sPos++]; matchLen += b; } while (b == 255);
                }
                matchLen += 4; // minmatch

                int matchPos = dPos - offset;
                if (matchPos < 0)
                    throw new InvalidDataException("LZ4: match offset before output start.");

                for (int i = 0; i < matchLen && dPos < outputSize; i++)
                    dst[dPos++] = dst[matchPos++];
            }

            return dst;
        }
    }
}
