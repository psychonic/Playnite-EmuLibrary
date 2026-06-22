using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace EmuLibrary.Util.ZArchive
{
    // Managed, write-only port of Exzap's ZArchive container (the format behind Cemu's .wua), faithful to
    // Exzap/ZArchive (src/zarchivewriter.cpp + include/zarchive/zarchivecommon.h). Produces a self-contained
    // archive: concatenated 64 KiB zstd blocks, then offset records, a deduplicated name table, a
    // breadth-first file tree, an (empty) metadata section, and a footer carrying section offsets + a
    // whole-archive SHA-256.
    //
    // The integrity hash is computed over exactly the bytes WE write, and the reference reader decompresses
    // each block with ZSTD_decompress, so our zstd frames only need to be valid (not byte-identical to
    // libzstd). All multi-byte integers are big-endian, matching ZArchive's _store(). Output is written
    // sequentially with no seeking (the format is designed for no-seek creation).
    internal sealed class ZArchiveWriter : IDisposable
    {
        private const int CompressedBlockSize = 64 * 1024;
        private const int EntriesPerOffsetRecord = 16;
        private const uint FooterMagic = 0x169f52d6;
        private const uint FooterVersion1 = 0x61bf3a01;
        private const int FooterSize = 16 * 6 + 32 + 8 + 4 + 4; // 144

        // ZArchive stores node names as Windows-1252 bytes (case-insensitive for ASCII letters). Wii U paths
        // are ASCII, so this is effectively a 1:1 byte mapping.
        private static readonly Encoding NameEncoding = Encoding.GetEncoding(1252);

        private sealed class PathNode
        {
            public readonly bool IsFile;
            public readonly uint NameIndex;
            public readonly List<PathNode> Subnodes = new List<PathNode>();
            public ulong FileOffset;
            public ulong FileSize;
            public uint NodeStartIndex;

            public PathNode(bool isFile, uint nameIndex)
            {
                IsFile = isFile;
                NameIndex = nameIndex;
            }
        }

        private struct OffsetRecord
        {
            public ulong BaseOffset;
            public ushort[] Size; // compressed size - 1, EntriesPerOffsetRecord entries
        }

        private readonly Stream _output;
        private readonly Compressor _compressor = new Compressor(6);
        private readonly byte[] _compressBuffer;
        private readonly byte[] _blockBuffer = new byte[CompressedBlockSize];
        private int _blockBufferLen;
        private readonly byte[] _scratch = new byte[8];

        private SHA256 _sha = SHA256.Create();
        private ulong _outputOffset;       // bytes written to _output
        private ulong _inputOffset;        // uncompressed bytes appended so far

        private readonly PathNode _rootNode = new PathNode(false, 0xFFFFFFFF);
        private readonly List<string> _nodeNames = new List<string>();
        private readonly Dictionary<string, uint> _nodeNameLookup = new Dictionary<string, uint>(StringComparer.Ordinal);
        private uint[] _nodeNameOffsets;

        private readonly List<OffsetRecord> _offsetRecords = new List<OffsetRecord>();
        private int _numWrittenBlocks;

        private PathNode _currentFileNode;
        private bool _finalized;

        // Footer section offsets/sizes, filled during Finalize.
        private (ulong off, ulong size) _secCompressed, _secOffsetRecords, _secNames, _secFileTree, _secMetaDir, _secMetaData;

        public ZArchiveWriter(Stream output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _compressBuffer = new byte[Compressor.GetCompressBound(CompressedBlockSize)];
        }

        #region Tree building (paths)

        // Create a directory (and, when recursive, all missing parents). Faithful to ZArchiveWriter::MakeDir.
        public bool MakeDir(string path, bool recursive)
        {
            var parser = TrimTrailingSlashes(path);
            if (!recursive)
            {
                SplitFilenameFromPath(ref parser, out var dirName);
                var dir = GetNodeByPath(parser);
                if (dir == null || FindSubnodeByName(dir, dirName) != null)
                    return false;
                dir.Subnodes.Add(new PathNode(false, CreateNameEntry(dirName)));
                return true;
            }

            var current = _rootNode;
            int pos = 0;
            while (TryGetNextPathNode(parser, ref pos, out var nodeName))
            {
                var next = FindSubnodeByName(current, nodeName);
                if (next != null && next.IsFile)
                    return false;
                if (next == null)
                {
                    next = new PathNode(false, CreateNameEntry(nodeName));
                    current.Subnodes.Add(next);
                }
                current = next;
            }
            return true;
        }

        // Begin a new file; subsequent AppendData calls write to it. Parent directory must already exist.
        // Faithful to ZArchiveWriter::StartNewFile.
        public bool StartNewFile(string path)
        {
            _currentFileNode = null;
            var parser = path;
            SplitFilenameFromPath(ref parser, out var filename);
            var dir = GetNodeByPath(parser);
            if (dir == null || FindSubnodeByName(dir, filename) != null)
                return false;
            var node = new PathNode(true, CreateNameEntry(filename)) { FileOffset = _inputOffset };
            dir.Subnodes.Add(node);
            _currentFileNode = node;
            return true;
        }

        private PathNode GetNodeByPath(string path)
        {
            var current = _rootNode;
            int pos = 0;
            while (TryGetNextPathNode(path, ref pos, out var nodeName))
            {
                var next = FindSubnodeByName(current, nodeName);
                if (next == null || next.IsFile)
                    return null;
                current = next;
            }
            return current;
        }

        private PathNode FindSubnodeByName(PathNode parent, string name)
        {
            foreach (var sub in parent.Subnodes)
            {
                if (CompareNodeNameEqual(_nodeNames[(int)sub.NameIndex], name))
                    return sub;
            }
            return null;
        }

        private uint CreateNameEntry(string name)
        {
            if (_nodeNameLookup.TryGetValue(name, out var idx))
                return idx;
            idx = (uint)_nodeNames.Count;
            _nodeNames.Add(name);
            _nodeNameLookup.Add(name, idx);
            return idx;
        }

        #endregion

        #region Data append + block compression

        public void AppendData(byte[] data, int offset, int count)
        {
            int dataSize = count;
            while (count > 0)
            {
                int toCopy = CompressedBlockSize - _blockBufferLen;
                if (toCopy > count)
                    toCopy = count;

                if (_blockBufferLen == 0 && toCopy == CompressedBlockSize)
                {
                    // Block-aligned chunk: compress straight from the input without buffering.
                    StoreBlock(data, offset);
                }
                else
                {
                    Buffer.BlockCopy(data, offset, _blockBuffer, _blockBufferLen, toCopy);
                    _blockBufferLen += toCopy;
                    if (_blockBufferLen == CompressedBlockSize)
                    {
                        StoreBlock(_blockBuffer, 0);
                        _blockBufferLen = 0;
                    }
                }
                offset += toCopy;
                count -= toCopy;
            }

            if (_currentFileNode != null)
                _currentFileNode.FileSize += (ulong)dataSize;
            _inputOffset += (ulong)dataSize;
        }

        // Compress and emit one full 64 KiB block, recording its compressed size in the offset records.
        private void StoreBlock(byte[] block, int blockOffset)
        {
            ulong compressedWriteOffset = _outputOffset;

            int outputSize;
            ReadOnlySpan<byte> src = new ReadOnlySpan<byte>(block, blockOffset, CompressedBlockSize);
            outputSize = _compressor.Wrap(src, _compressBuffer);

            if (outputSize >= CompressedBlockSize)
            {
                // Compression didn't help: store the raw block.
                outputSize = CompressedBlockSize;
                OutputData(block, blockOffset, CompressedBlockSize);
            }
            else
            {
                OutputData(_compressBuffer, 0, outputSize);
            }

            int slot = _numWrittenBlocks % EntriesPerOffsetRecord;
            if (slot == 0)
                _offsetRecords.Add(new OffsetRecord { BaseOffset = compressedWriteOffset, Size = new ushort[EntriesPerOffsetRecord] });
            var rec = _offsetRecords[_offsetRecords.Count - 1];
            rec.Size[slot] = (ushort)(outputSize - 1);
            _numWrittenBlocks++;
        }

        #endregion

        #region Finalize + section writers

        public void Complete()
        {
            if (_finalized)
                return;
            _finalized = true;

            _currentFileNode = null;
            // Flush the partial final block by zero-padding it to a full block.
            if (_blockBufferLen != 0)
            {
                int pad = CompressedBlockSize - _blockBufferLen;
                Array.Clear(_blockBuffer, _blockBufferLen, pad);
                StoreBlock(_blockBuffer, 0);
                _blockBufferLen = 0;
                _inputOffset += (ulong)pad;
            }

            _secCompressed = (0, _outputOffset);

            // Pad the compressed-data section to an 8-byte boundary (excluded from sectionCompressedData.size).
            var zero = new byte[1];
            while ((_outputOffset % 8) != 0)
                OutputData(zero, 0, 1);

            WriteOffsetRecords();
            WriteNameTable();
            WriteFileTree();
            WriteMetaData();
            WriteFooter();
        }

        private void WriteOffsetRecords()
        {
            ulong start = _outputOffset;
            foreach (var rec in _offsetRecords)
            {
                WriteU64BE(rec.BaseOffset);
                for (int i = 0; i < EntriesPerOffsetRecord; i++)
                    WriteU16BE(rec.Size[i]);
            }
            _secOffsetRecords = (start, _outputOffset - start);
        }

        private void WriteNameTable()
        {
            ulong start = _outputOffset;
            _nodeNameOffsets = new uint[_nodeNames.Count];
            uint cur = 0;
            for (int i = 0; i < _nodeNames.Count; i++)
            {
                _nodeNameOffsets[i] = cur;
                var bytes = NameEncoding.GetBytes(_nodeNames[i]);
                int len = bytes.Length;
                if (len > 0x7FFF)
                    len = 0x7FFF; // cut off after 2^15-1 bytes (matches reference)

                if (len >= 0x80)
                {
                    _scratch[0] = (byte)((len & 0x7F) | 0x80);
                    _scratch[1] = (byte)(len >> 7);
                    OutputData(_scratch, 0, 2);
                    cur += 2;
                }
                else
                {
                    _scratch[0] = (byte)(len & 0x7F);
                    OutputData(_scratch, 0, 1);
                    cur += 1;
                }
                OutputData(bytes, 0, len);
                cur += (uint)len;
            }
            _secNames = (start, _outputOffset - start);
        }

        private void WriteFileTree()
        {
            // First pass (BFS): assign each directory a contiguous index range; sort children lexicographically
            // (case-insensitive) so the reader can binary-search.
            var queue = new Queue<PathNode>();
            queue.Enqueue(_rootNode);
            uint currentIndex = 1; // root is index 0
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node.IsFile)
                {
                    node.NodeStartIndex = 0xFFFFFFFF;
                    continue;
                }
                node.Subnodes.Sort((a, b) =>
                    CompareNodeName(_nodeNames[(int)a.NameIndex], _nodeNames[(int)b.NameIndex]));
                node.NodeStartIndex = currentIndex;
                currentIndex += (uint)node.Subnodes.Count;
                foreach (var sub in node.Subnodes)
                    queue.Enqueue(sub);
            }

            // Second pass (same BFS order): serialize one 16-byte FileDirectoryEntry per node.
            ulong start = _outputOffset;
            queue.Enqueue(_rootNode);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                uint nameOffset = node == _rootNode ? 0x7FFFFFFFu : _nodeNameOffsets[(int)node.NameIndex];
                uint nameAndType = (nameOffset & 0x7FFFFFFF) | (node.IsFile ? 0x80000000u : 0u);

                uint w0, w1, w2;
                if (node.IsFile)
                {
                    // 48-bit offset/size split across three uint32s (see FileDirectoryEntry::SetFileOffset/Size).
                    w0 = (uint)node.FileOffset;                         // fileOffsetLow
                    w1 = (uint)node.FileSize;                           // fileSizeLow
                    uint hi = (uint)((node.FileOffset >> 32) & 0xFFFF); // low 16: offset bits 32..47
                    hi |= (uint)(((node.FileSize >> 16) & 0xFFFF0000)); // high 16: size bits 32..47
                    w2 = hi;
                }
                else
                {
                    w0 = node.NodeStartIndex;
                    w1 = (uint)node.Subnodes.Count;
                    w2 = 0;
                }

                WriteU32BE(nameAndType);
                WriteU32BE(w0);
                WriteU32BE(w1);
                WriteU32BE(w2);

                foreach (var sub in node.Subnodes)
                    queue.Enqueue(sub);
            }
            _secFileTree = (start, _outputOffset - start);
        }

        private void WriteMetaData()
        {
            _secMetaDir = (_outputOffset, 0);
            _secMetaData = (_outputOffset, 0);
        }

        private void WriteFooter()
        {
            ulong totalSize = _outputOffset + (ulong)FooterSize;

            // Serialize the footer with a zeroed hash, hash it (continuing the running SHA), then close.
            var footerZero = SerializeFooter(totalSize, new byte[32]);
            _sha.TransformBlock(footerZero, 0, footerZero.Length, null, 0);
            _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = _sha.Hash;

            // Write the final footer (with the real hash) WITHOUT hashing it.
            var footerFinal = SerializeFooter(totalSize, hash);
            _output.Write(footerFinal, 0, footerFinal.Length);
            _outputOffset += (ulong)footerFinal.Length;
        }

        // On-disk footer layout: 6 OffsetInfo (offset,size each big-endian u64), 32-byte hash, totalSize u64,
        // version u32, magic u32.
        private byte[] SerializeFooter(ulong totalSize, byte[] hash32)
        {
            var buf = new byte[FooterSize];
            int p = 0;
            void PutU64(ulong v) { WriteU64BEInto(buf, p, v); p += 8; }
            void PutU32(uint v) { WriteU32BEInto(buf, p, v); p += 4; }

            PutU64(_secCompressed.off); PutU64(_secCompressed.size);
            PutU64(_secOffsetRecords.off); PutU64(_secOffsetRecords.size);
            PutU64(_secNames.off); PutU64(_secNames.size);
            PutU64(_secFileTree.off); PutU64(_secFileTree.size);
            PutU64(_secMetaDir.off); PutU64(_secMetaDir.size);
            PutU64(_secMetaData.off); PutU64(_secMetaData.size);
            Buffer.BlockCopy(hash32, 0, buf, p, 32); p += 32;
            PutU64(totalSize);
            PutU32(FooterVersion1);
            PutU32(FooterMagic);
            return buf;
        }

        #endregion

        #region Low-level output + big-endian helpers

        private void OutputData(byte[] data, int offset, int length)
        {
            _output.Write(data, offset, length);
            if (_sha != null)
                _sha.TransformBlock(data, offset, length, null, 0);
            _outputOffset += (ulong)length;
        }

        private void WriteU16BE(ushort v)
        {
            _scratch[0] = (byte)(v >> 8);
            _scratch[1] = (byte)v;
            OutputData(_scratch, 0, 2);
        }

        private void WriteU32BE(uint v)
        {
            WriteU32BEInto(_scratch, 0, v);
            OutputData(_scratch, 0, 4);
        }

        private void WriteU64BE(ulong v)
        {
            WriteU64BEInto(_scratch, 0, v);
            OutputData(_scratch, 0, 8);
        }

        private static void WriteU32BEInto(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        private static void WriteU64BEInto(byte[] b, int o, ulong v)
        {
            b[o] = (byte)(v >> 56);
            b[o + 1] = (byte)(v >> 48);
            b[o + 2] = (byte)(v >> 40);
            b[o + 3] = (byte)(v >> 32);
            b[o + 4] = (byte)(v >> 24);
            b[o + 5] = (byte)(v >> 16);
            b[o + 6] = (byte)(v >> 8);
            b[o + 7] = (byte)v;
        }

        #endregion

        #region Path parsing (faithful to zarchivecommon.h)

        private static string TrimTrailingSlashes(string path)
        {
            int end = path.Length;
            while (end > 0 && (path[end - 1] == '/' || path[end - 1] == '\\'))
                end--;
            return path.Substring(0, end);
        }

        private static bool TryGetNextPathNode(string path, ref int pos, out string node)
        {
            while (pos < path.Length && (path[pos] == '/' || path[pos] == '\\'))
                pos++;
            if (pos >= path.Length)
            {
                node = null;
                return false;
            }
            int start = pos;
            while (pos < path.Length && path[pos] != '/' && path[pos] != '\\')
                pos++;
            node = path.Substring(start, pos - start);
            return true;
        }

        // Splits the last path component into `filename` and trims it (and its separator) off `path`.
        private static void SplitFilenameFromPath(ref string path, out string filename)
        {
            if (path.Length == 0)
            {
                filename = path;
                return;
            }
            int index = path.Length - 1;
            while (true)
            {
                if (path[index] == '/' || path[index] == '\\')
                {
                    index++;
                    break;
                }
                if (index == 0)
                    break;
                index--;
            }
            filename = path.Substring(index);
            path = path.Substring(0, index);
        }

        private static bool CompareNodeNameEqual(string a, string b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (LowerAscii(a[i]) != LowerAscii(b[i]))
                    return false;
            }
            return true;
        }

        // Returns a value matching std::sort with the reference's `CompareNodeName(a,b) > 0` comparator,
        // i.e. ascending case-insensitive order.
        private static int CompareNodeName(string a, string b)
        {
            int min = Math.Min(a.Length, b.Length);
            for (int i = 0; i < min; i++)
            {
                char c1 = LowerAscii(a[i]);
                char c2 = LowerAscii(b[i]);
                if (c1 != c2)
                    return c1 < c2 ? -1 : 1;
            }
            if (a.Length < b.Length) return -1;
            if (a.Length > b.Length) return 1;
            return 0;
        }

        private static char LowerAscii(char c) => (c >= 'A' && c <= 'Z') ? (char)(c + ('a' - 'A')) : c;

        #endregion

        public void Dispose()
        {
            _compressor?.Dispose();
            _sha?.Dispose();
            _sha = null;
        }
    }
}
