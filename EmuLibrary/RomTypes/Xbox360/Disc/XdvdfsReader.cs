using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmuLibrary.RomTypes.Xbox360.Disc
{
    // Minimal read-only XGD (XISO / XDVDFS) filesystem reader: locate the data partition, walk the root
    // directory tree, and expose a single named file's extent. Listing + single-file read only — no
    // extraction (the .iso is copied verbatim on install). XDVDFS is little-endian.
    internal sealed class XdvdfsReader : IDisposable
    {
        private const int SectorSize = 0x800;                 // 2048-byte logical sectors
        private const int VolumeDescriptorSector = 32;        // sector 32 within the data partition
        private static readonly byte[] Signature = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA"); // 20 bytes

        // Candidate partition base offsets (absolute, bytes). 0 = raw XISO; the rest are the known XGD1/2/3
        // video-partition offsets. The reader tries each and keeps the one whose sector 32 carries the volume
        // signature.
        internal static readonly long[] PartitionBaseOffsets =
        {
            0x0,        // raw XISO (extracted data partition)
            0xFD90000,  // XGD1 / XGD2
            0xFDA0000,  // XGD2 (alternate)
            0x2080000,  // XGD3
            0x2090000,  // XGD3 (alternate)
            0x4100000,  // XGD3 (alternate)
        };

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private long _partitionOffset;
        private uint _rootDirSector;
        private uint _rootDirSize;

        private XdvdfsReader(Stream stream, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
        }

        // Opens an .iso path. Returns null if the volume can't be located at any known partition offset.
        public static XdvdfsReader TryOpen(string path)
        {
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var reader = TryOpen(fs, leaveOpen: false);
                if (reader == null)
                    fs.Dispose();
                return reader;
            }
            catch
            {
                fs?.Dispose();
                return null;
            }
        }

        // Opens over an existing seekable stream (used by tests). Returns null if no volume is found.
        public static XdvdfsReader TryOpen(Stream stream, bool leaveOpen)
        {
            if (stream == null || !stream.CanSeek)
                return null;

            var reader = new XdvdfsReader(stream, leaveOpen);
            if (reader.TryLocateVolume())
                return reader;

            if (!leaveOpen)
                stream.Dispose();
            return null;
        }

        private bool TryLocateVolume()
        {
            var buf = new byte[SectorSize];
            foreach (var baseOffset in PartitionBaseOffsets)
            {
                long descOffset = baseOffset + (long)VolumeDescriptorSector * SectorSize;
                if (!TryReadExact(descOffset, buf, 0, buf.Length))
                    continue;

                if (!StartsWith(buf, Signature))
                    continue;

                // Volume descriptor: 0x14 root dir sector (LE), 0x18 root dir size (LE).
                _partitionOffset = baseOffset;
                _rootDirSector = ReadU32LE(buf, 0x14);
                _rootDirSize = ReadU32LE(buf, 0x18);
                return true;
            }
            return false;
        }

        // Locates a top-level file by name (case-insensitive). Returns its absolute byte offset + size.
        public bool TryFindRootFile(string name, out long absoluteOffset, out long size)
        {
            absoluteOffset = 0;
            size = 0;

            if (_rootDirSize == 0 || _rootDirSize > 32 * 1024 * 1024)
                return false;

            var dir = new byte[_rootDirSize];
            long dirOffset = _partitionOffset + (long)_rootDirSector * SectorSize;
            if (!TryReadExact(dirOffset, dir, 0, dir.Length))
                return false;

            // The directory is an AVL tree of fixed-prefix entries packed into the region. Left/right child
            // pointers are in 4-byte units; 0 or 0xFFFF means no child. Traverse the whole tree (visiting every
            // node) so we don't depend on the on-disk name-compare ordering.
            var stack = new Stack<int>();
            var visited = new HashSet<int>();
            stack.Push(0);

            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (node < 0 || node + 0x0E > dir.Length || !visited.Add(node))
                    continue;

                ushort left = ReadU16LE(dir, node + 0x00);
                ushort right = ReadU16LE(dir, node + 0x02);
                uint startSector = ReadU32LE(dir, node + 0x04);
                uint fileSize = ReadU32LE(dir, node + 0x08);
                byte attributes = dir[node + 0x0C];
                byte nameLen = dir[node + 0x0D];

                if (node + 0x0E + nameLen <= dir.Length && nameLen > 0)
                {
                    var entryName = Encoding.ASCII.GetString(dir, node + 0x0E, nameLen);
                    const byte FILE_ATTRIBUTE_DIRECTORY = 0x10;
                    bool isDirectory = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    if (!isDirectory && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        absoluteOffset = _partitionOffset + (long)startSector * SectorSize;
                        size = fileSize;
                        return true;
                    }
                }

                if (left != 0 && left != 0xFFFF)
                    stack.Push(left * 4);
                if (right != 0 && right != 0xFFFF)
                    stack.Push(right * 4);
            }

            return false;
        }

        // Reads up to maxBytes from the default.xex (enough to parse the XEX header / execution id). Returns
        // null if default.xex isn't present.
        public byte[] TryReadDefaultXex(int maxBytes)
        {
            if (!TryFindRootFile("default.xex", out var offset, out var size))
                return null;

            int count = (int)Math.Min(size, maxBytes);
            if (count <= 0)
                return null;

            var buf = new byte[count];
            if (!TryReadExact(offset, buf, 0, count))
                return null;
            return buf;
        }

        private bool TryReadExact(long offset, byte[] buffer, int start, int count)
        {
            if (offset < 0 || offset + count > _stream.Length)
                return false;
            _stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, start + read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        private static bool StartsWith(byte[] buffer, byte[] prefix)
        {
            if (buffer.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
                if (buffer[i] != prefix[i])
                    return false;
            return true;
        }

        private static ushort ReadU16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

        private static uint ReadU32LE(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        public void Dispose()
        {
            if (!_leaveOpen)
                _stream?.Dispose();
        }
    }
}
