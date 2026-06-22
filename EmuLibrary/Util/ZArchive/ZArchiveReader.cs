using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZstdSharp;

namespace EmuLibrary.Util.ZArchive
{
    // Managed reader for Exzap's ZArchive container (Cemu's .wua), the inverse of ZArchiveWriter and faithful
    // to the same layout (Exzap/ZArchive src/zarchivereader.cpp + include/zarchive/zarchivecommon.h): a footer
    // carrying section offsets, a file tree of 16-byte nodes (root index 0, directories naming a contiguous
    // child range), a deduplicated name table, offset records mapping each 64 KiB uncompressed block to its
    // compressed location, and the concatenated zstd blocks themselves. All multi-byte integers are big-endian.
    //
    // This is a random-access reader: it loads the metadata sections into memory and decompresses content
    // blocks on demand (one-block cache), so reading meta.xml out of a multi-GB archive stays cheap. The
    // whole-archive SHA-256 in the footer is not verified (we only need the plaintext).
    internal sealed class ZArchiveReader : IDisposable
    {
        private const int CompressedBlockSize = 64 * 1024;
        private const int EntriesPerOffsetRecord = 16;
        private const int OffsetRecordSize = 8 + EntriesPerOffsetRecord * 2; // 40
        private const int FileTreeEntrySize = 16;
        private const uint FooterMagic = 0x169f52d6;
        private const uint FooterVersion1 = 0x61bf3a01;
        private const int FooterSize = 16 * 6 + 32 + 8 + 4 + 4; // 144

        private static readonly Encoding NameEncoding = Encoding.GetEncoding(1252);

        // A parsed file-tree node. For a directory, [ChildStart, ChildStart+ChildCount) index into _entries.
        private struct Entry
        {
            public bool IsFile;
            public string Name;
            public ulong FileOffset;   // file: uncompressed offset of its data
            public ulong FileSize;     // file: logical size
            public uint ChildStart;    // dir: first child index
            public uint ChildCount;    // dir: child count
        }

        private readonly Stream _stream;
        private readonly bool _leaveOpen;

        private readonly ulong _secOffsetRecordsOff, _secOffsetRecordsSize;
        private readonly byte[] _offsetRecords;
        private readonly Entry[] _entries;

        private readonly Decompressor _decompressor = new Decompressor();
        private readonly byte[] _compressedBuf = new byte[CompressedBlockSize];
        private readonly byte[] _blockBuf = new byte[CompressedBlockSize];
        private long _cachedBlockIndex = -1;

        private ZArchiveReader(Stream stream, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;

            if (!stream.CanSeek)
                throw new InvalidDataException("ZArchive requires a seekable stream.");
            if (stream.Length < FooterSize)
                throw new InvalidDataException("File is too small to be a ZArchive.");

            // Footer: 6 OffsetInfo (off,size u64 each), 32-byte hash, totalSize u64, version u32, magic u32.
            var footer = ReadAt(stream.Length - FooterSize, FooterSize);
            uint magic = ReadU32BE(footer, FooterSize - 4);
            uint version = ReadU32BE(footer, FooterSize - 8);
            if (magic != FooterMagic)
                throw new InvalidDataException("Not a ZArchive (bad footer magic).");
            if (version != FooterVersion1)
                throw new InvalidDataException($"Unsupported ZArchive version 0x{version:x8}.");

            _secOffsetRecordsOff = ReadU64BE(footer, 16);
            _secOffsetRecordsSize = ReadU64BE(footer, 24);
            ulong namesOff = ReadU64BE(footer, 32);
            ulong namesSize = ReadU64BE(footer, 40);
            ulong fileTreeOff = ReadU64BE(footer, 48);
            ulong fileTreeSize = ReadU64BE(footer, 56);

            _offsetRecords = ReadAt((long)_secOffsetRecordsOff, (int)_secOffsetRecordsSize);
            var names = ReadAt((long)namesOff, (int)namesSize);
            var fileTree = ReadAt((long)fileTreeOff, (int)fileTreeSize);

            _entries = ParseFileTree(fileTree, names);
        }

        public static ZArchiveReader Open(string path) =>
            new ZArchiveReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), leaveOpen: false);

        public static ZArchiveReader Open(Stream stream, bool leaveOpen = false) =>
            new ZArchiveReader(stream, leaveOpen);

        #region File tree parse + navigation

        private static Entry[] ParseFileTree(byte[] fileTree, byte[] names)
        {
            int count = fileTree.Length / FileTreeEntrySize;
            var entries = new Entry[count];
            for (int i = 0; i < count; i++)
            {
                int o = i * FileTreeEntrySize;
                uint nameAndType = ReadU32BE(fileTree, o);
                uint w0 = ReadU32BE(fileTree, o + 4);
                uint w1 = ReadU32BE(fileTree, o + 8);
                uint w2 = ReadU32BE(fileTree, o + 12);

                bool isFile = (nameAndType & 0x80000000u) != 0;
                uint nameOffset = nameAndType & 0x7FFFFFFF;

                var e = new Entry { IsFile = isFile };
                e.Name = (nameOffset == 0x7FFFFFFF) ? string.Empty : ReadName(names, nameOffset);
                if (isFile)
                {
                    e.FileOffset = w0 | ((ulong)(w2 & 0xFFFF) << 32);
                    e.FileSize = w1 | ((ulong)(w2 & 0xFFFF0000) << 16);
                }
                else
                {
                    e.ChildStart = w0;
                    e.ChildCount = w1;
                }
                entries[i] = e;
            }
            return entries;
        }

        private static string ReadName(byte[] names, uint offset)
        {
            int p = (int)offset;
            int len = names[p++];
            if ((len & 0x80) != 0)
                len = (len & 0x7F) | (names[p++] << 7);
            return NameEncoding.GetString(names, p, len);
        }

        // Locate an entry index by forward/back-slash path (case-insensitive), or -1. Root path "" -> 0.
        private int FindEntry(string path)
        {
            int current = 0; // root
            if (string.IsNullOrEmpty(path))
                return current;

            foreach (var part in path.Split('/', '\\'))
            {
                if (part.Length == 0)
                    continue;
                if (_entries[current].IsFile)
                    return -1;
                int next = FindChild(current, part);
                if (next < 0)
                    return -1;
                current = next;
            }
            return current;
        }

        private int FindChild(int dirIndex, string name)
        {
            ref readonly var dir = ref _entries[dirIndex];
            for (uint i = 0; i < dir.ChildCount; i++)
            {
                int idx = (int)(dir.ChildStart + i);
                if (NameEquals(_entries[idx].Name, name))
                    return idx;
            }
            return -1;
        }

        #endregion

        #region Public query API

        public bool FileExists(string path)
        {
            int i = FindEntry(path);
            return i >= 0 && _entries[i].IsFile;
        }

        public bool DirectoryExists(string path)
        {
            int i = FindEntry(path);
            return i >= 0 && !_entries[i].IsFile;
        }

        // Immediate child names of a directory (files and subdirectories), or empty if the path is missing or a
        // file. Order is the archive's stored order (ascending case-insensitive).
        public IReadOnlyList<string> ListDirectory(string path)
        {
            var result = new List<string>();
            int i = FindEntry(path);
            if (i < 0 || _entries[i].IsFile)
                return result;
            var dir = _entries[i];
            for (uint c = 0; c < dir.ChildCount; c++)
                result.Add(_entries[(int)(dir.ChildStart + c)].Name);
            return result;
        }

        // Immediate subdirectory names of a directory.
        public IReadOnlyList<string> ListSubdirectories(string path)
        {
            var result = new List<string>();
            int i = FindEntry(path);
            if (i < 0 || _entries[i].IsFile)
                return result;
            var dir = _entries[i];
            for (uint c = 0; c < dir.ChildCount; c++)
            {
                int idx = (int)(dir.ChildStart + c);
                if (!_entries[idx].IsFile)
                    result.Add(_entries[idx].Name);
            }
            return result;
        }

        public bool TryReadFile(string path, out byte[] data)
        {
            data = null;
            int i = FindEntry(path);
            if (i < 0 || !_entries[i].IsFile)
                return false;
            data = ReadData(_entries[i].FileOffset, _entries[i].FileSize);
            return true;
        }

        public byte[] ReadFile(string path)
        {
            if (!TryReadFile(path, out var data))
                throw new FileNotFoundException($"No file \"{path}\" in archive.");
            return data;
        }

        #endregion

        #region Block decompression

        // Read `size` uncompressed bytes starting at `offset` in the logical (decompressed) stream, spanning
        // 64 KiB blocks as needed.
        private byte[] ReadData(ulong offset, ulong size)
        {
            var output = new byte[size];
            int written = 0;
            ulong pos = offset;
            ulong remaining = size;

            while (remaining > 0)
            {
                long blockIndex = (long)(pos / CompressedBlockSize);
                int within = (int)(pos % CompressedBlockSize);
                int take = (int)Math.Min(remaining, (ulong)(CompressedBlockSize - within));

                var block = GetBlock(blockIndex);
                Buffer.BlockCopy(block, within, output, written, take);

                written += take;
                pos += (ulong)take;
                remaining -= (ulong)take;
            }
            return output;
        }

        // Decompress one 64 KiB block, with a single-block cache for sequential reads.
        private byte[] GetBlock(long blockIndex)
        {
            if (blockIndex == _cachedBlockIndex)
                return _blockBuf;

            int record = (int)(blockIndex / EntriesPerOffsetRecord);
            int slot = (int)(blockIndex % EntriesPerOffsetRecord);
            int recBase = record * OffsetRecordSize;

            ulong compressedOffset = ReadU64BE(_offsetRecords, recBase);
            for (int i = 0; i < slot; i++)
                compressedOffset += (ulong)(ReadU16BE(_offsetRecords, recBase + 8 + i * 2) + 1);
            int compressedSize = ReadU16BE(_offsetRecords, recBase + 8 + slot * 2) + 1;

            ReadExactAt((long)compressedOffset, _compressedBuf, compressedSize);

            if (compressedSize == CompressedBlockSize)
            {
                // Stored raw (compression didn't help) — copy verbatim.
                Buffer.BlockCopy(_compressedBuf, 0, _blockBuf, 0, CompressedBlockSize);
            }
            else
            {
                int n = _decompressor.Unwrap(_compressedBuf, 0, compressedSize, _blockBuf, 0, CompressedBlockSize);
                if (n != CompressedBlockSize)
                    throw new InvalidDataException($"ZArchive block {blockIndex} decompressed to {n} bytes (expected {CompressedBlockSize}).");
            }

            _cachedBlockIndex = blockIndex;
            return _blockBuf;
        }

        #endregion

        #region Stream helpers + big-endian readers

        private byte[] ReadAt(long offset, int count)
        {
            var buf = new byte[count];
            ReadExactAt(offset, buf, count);
            return buf;
        }

        private void ReadExactAt(long offset, byte[] buffer, int count)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException("Unexpected end of ZArchive.");
                read += n;
            }
        }

        private static ushort ReadU16BE(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        private static uint ReadU32BE(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        private static ulong ReadU64BE(byte[] b, int o) =>
            ((ulong)ReadU32BE(b, o) << 32) | ReadU32BE(b, o + 4);

        private static bool NameEquals(string a, string b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (LowerAscii(a[i]) != LowerAscii(b[i]))
                    return false;
            return true;
        }

        private static char LowerAscii(char c) => (c >= 'A' && c <= 'Z') ? (char)(c + ('a' - 'A')) : c;

        #endregion

        public void Dispose()
        {
            _decompressor?.Dispose();
            if (!_leaveOpen)
                _stream?.Dispose();
        }
    }
}
