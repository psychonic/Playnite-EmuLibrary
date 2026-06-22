using System;
using System.IO;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // Random-access view over a Wii U disc image, abstracting the on-disk container (raw .wud or
    // sector-deduplicated .wux). Exposes the logical (uncompressed) disc bytes plus the disc-key AES-CBC layer
    // used for the partition TOC / SI partition / FSTs. Game content is read raw (no disc-key layer) and
    // decrypted by NusReader with the title key. Ported from Maschell/JNUSLib (WUDDiscReader + WUDImage).
    internal abstract class DiscReader : IDisposable
    {
        // Logical (uncompressed) disc size.
        public abstract long Length { get; }

        // Fill `count` logical bytes from `offset`, zero-padding past the end of the disc.
        public abstract void ReadRaw(long offset, byte[] buffer, int bufferOffset, int count);

        public byte[] ReadRaw(long offset, int count)
        {
            var b = new byte[count];
            ReadRaw(offset, b, 0, count);
            return b;
        }

        // Disc-key AES-CBC decrypt of `size` bytes (the metadata layer: TOC, SI partition, FSTs). With a fixed
        // IV the same IV (zeros when null) decrypts each 0x10000 block independently; otherwise each block's IV
        // is { 0*8, BE(fileOffset >> 16) }. Each 0x10000 chunk is decrypted as one CBC run. Mirrors
        // WUDDiscReader.readDecryptedToOutputStream.
        public byte[] ReadDecrypted(long clusterOffset, long fileOffset, int size, byte[] key, byte[] iv, bool fixedIV)
        {
            const int Block = 0x10000;
            using (var aes = new AesCbc(key))
            using (var ms = new MemoryStream(size))
            {
                var enc = new byte[Block];
                var dec = new byte[Block];
                long usedFileOffset = fileOffset;
                long usedSize = size;
                long total = 0;

                do
                {
                    long blockNumber = usedFileOffset / Block;
                    long blockOffset = usedFileOffset % Block;
                    long readOffset = clusterOffset + blockNumber * Block;

                    byte[] usedIv;
                    if (fixedIV)
                    {
                        usedIv = iv ?? new byte[16];
                    }
                    else
                    {
                        usedIv = new byte[16];
                        ulong v = (ulong)(usedFileOffset >> 16);
                        for (int i = 0; i < 8; i++)
                            usedIv[15 - i] = (byte)(v >> (8 * i));
                    }

                    ReadRaw(readOffset, enc, 0, Block);
                    aes.Decrypt(usedIv, enc, 0, Block, dec, 0);

                    long maxCopy = Block - blockOffset;
                    long copy = Math.Min(usedSize, maxCopy);
                    ms.Write(dec, (int)blockOffset, (int)copy);

                    total += copy;
                    usedSize -= copy;
                    usedFileOffset += copy;
                } while (total < size);

                return ms.ToArray();
            }
        }

        // Opens a .wud (raw) or .wux (compressed) image by sniffing the WUX magic.
        public static DiscReader Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var header = new byte[WuxReader.HeaderSize];
                int read = 0;
                while (read < header.Length)
                {
                    int n = fs.Read(header, read, header.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (read >= 8 && Le.U32(header, 0) == WuxReader.Magic0 && Le.U32(header, 4) == WuxReader.Magic1)
                    return new WuxReader(fs, header);

                return new WudFileReader(fs);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public abstract void Dispose();
    }

    // Raw, uncompressed .wud image (the logical bytes are the file bytes).
    internal sealed class WudFileReader : DiscReader
    {
        private readonly FileStream _fs;
        private readonly long _length;

        public WudFileReader(FileStream fs)
        {
            _fs = fs;
            _length = fs.Length;
        }

        public override long Length => _length;

        public override void ReadRaw(long offset, byte[] buffer, int bufferOffset, int count)
        {
            if (offset >= _length)
            {
                Array.Clear(buffer, bufferOffset, count);
                return;
            }
            _fs.Seek(offset, SeekOrigin.Begin);
            int want = (int)Math.Min(count, _length - offset);
            int read = 0;
            while (read < want)
            {
                int n = _fs.Read(buffer, bufferOffset + read, want - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < count)
                Array.Clear(buffer, bufferOffset + read, count - read);
        }

        public override void Dispose() => _fs.Dispose();
    }

    // Sector-deduplicated .wux image: a logical-sector -> physical-sector index table over a deduped sector
    // array. Ported from JNUSLib WUDImageCompressedInfo / WUDImage. Header is little-endian.
    internal sealed class WuxReader : DiscReader
    {
        public const int HeaderSize = 0x20;
        public const uint Magic0 = 0x30585557; // 'WUX0'
        public const uint Magic1 = 0x1099d02e;

        private readonly FileStream _fs;
        private readonly int _sectorSize;
        private readonly long _uncompressedSize;
        private readonly long _offsetSectorArray;
        private readonly uint[] _indexTable;

        public WuxReader(FileStream fs, byte[] header)
        {
            _fs = fs;
            _sectorSize = (int)Le.U32(header, 0x08);
            _uncompressedSize = (long)Le.U64(header, 0x10);
            if (_sectorSize <= 0)
                throw new InvalidDataException("Invalid WUX sector size.");

            long entryCount = (_uncompressedSize + _sectorSize - 1) / _sectorSize;
            long offsetIndexTable = HeaderSize;

            long offsetSectorArray = offsetIndexTable + entryCount * 0x04L;
            offsetSectorArray += _sectorSize - 1;
            offsetSectorArray -= offsetSectorArray % _sectorSize; // align up to sectorSize
            _offsetSectorArray = offsetSectorArray;

            _indexTable = new uint[entryCount];
            var tableBytes = new byte[entryCount * 4];
            _fs.Seek(offsetIndexTable, SeekOrigin.Begin);
            int read = 0;
            while (read < tableBytes.Length)
            {
                int n = _fs.Read(tableBytes, read, tableBytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            for (long i = 0; i < entryCount; i++)
                _indexTable[i] = Le.U32(tableBytes, (int)(i * 4));
        }

        public override long Length => _uncompressedSize;

        public override void ReadRaw(long offset, byte[] buffer, int bufferOffset, int count)
        {
            while (count > 0)
            {
                if (offset >= _uncompressedSize)
                {
                    Array.Clear(buffer, bufferOffset, count);
                    return;
                }

                long sector = offset / _sectorSize;
                int within = (int)(offset % _sectorSize);
                uint phys = _indexTable[sector];
                long physOffset = _offsetSectorArray + (long)phys * _sectorSize + within;

                int chunk = (int)Math.Min(count, _sectorSize - within);
                chunk = (int)Math.Min(chunk, _uncompressedSize - offset);

                _fs.Seek(physOffset, SeekOrigin.Begin);
                int read = 0;
                while (read < chunk)
                {
                    int n = _fs.Read(buffer, bufferOffset + read, chunk - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < chunk)
                    Array.Clear(buffer, bufferOffset + read, chunk - read);

                offset += chunk;
                bufferOffset += chunk;
                count -= chunk;
            }
        }

        public override void Dispose() => _fs.Dispose();
    }

    // Little-endian readers (the WUX container header is little-endian, unlike the big-endian disc payload).
    internal static class Le
    {
        public static uint U32(byte[] b, int o) =>
            (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        public static ulong U64(byte[] b, int o) => U32(b, o) | ((ulong)U32(b, o + 4) << 32);
    }
}
