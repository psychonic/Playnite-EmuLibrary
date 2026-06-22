using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // First-section entry of an FST: where a content (.app) lives within its partition (disc) — unused for
    // loose NUS, where each content is a separate file. Mirrors JNUSLib ContentFSTInfo.
    internal sealed class ContentFstInfo
    {
        public uint OffsetSector;
        public uint SizeSector;

        // Byte offset of the content within the partition data area.
        public long Offset => OffsetSector == 0 ? 0 : (long)OffsetSector * 0x8000 - 0x8000;
    }

    // One real file in an FST.
    internal sealed class FstFile
    {
        public string Path;          // forward-slash relative path
        public uint Size;
        public ulong OffsetInContent; // byte offset within its content
        public ushort Flags;
        public ushort ContentIndex;   // index into the TMD content list (and ContentFstInfo)

        public byte ContentIdLow => (byte)ContentIndex;
    }

    // Parses a decrypted Wii U FST (the 'FST\0' filesystem table that is content[0] of a title / SI partition).
    // Combines cdecrypt's level/entry-stack traversal with JNUSLib's offset convention: a file's byte offset
    // within its content is rawOffset * offsetFactor (FST header field 0x04), except when flag 0x4 is set the
    // raw value is already a byte offset (cdecrypt's <<5 special case generalized).
    internal sealed class Fst
    {
        private const uint FstMagic = 0x46535400; // 'FST\0'

        public int OffsetFactor { get; private set; }
        public ContentFstInfo[] Contents { get; private set; }
        public List<FstFile> Files { get; private set; }

        public static Fst Parse(byte[] fst)
        {
            if (fst == null || fst.Length < 0x20 || Be.U32(fst, 0) != FstMagic)
                throw new InvalidDataException("FST magic mismatch.");

            int offsetFactor = (int)Be.U32(fst, 0x04);
            uint contentCount = Be.U32(fst, 0x08);

            long feBase = 0x20 + (long)contentCount * 0x20;
            uint entries = Be.U32(fst, (int)(feBase + 8)); // root entry's count == total entry count
            long nameBase = feBase + (long)entries * 0x10;

            var contents = new ContentFstInfo[contentCount];
            for (int i = 0; i < contentCount; i++)
            {
                int o = 0x20 + i * 0x20;
                contents[i] = new ContentFstInfo { OffsetSector = Be.U32(fst, o + 0), SizeSector = Be.U32(fst, o + 4) };
            }

            var files = new List<FstFile>();
            var dirStack = new int[16];
            var dirEnd = new uint[16];
            int level = 0;

            for (uint i = 1; i < entries; i++)
            {
                while (level > 0 && dirEnd[level - 1] == i)
                    level--;

                int fe = (int)(feBase + (long)i * 0x10);
                uint typeName = Be.U32(fst, fe);
                byte type = (byte)(typeName >> 24);
                uint nameRel = typeName & 0x00FFFFFF;

                if ((type & 1) != 0)
                {
                    dirStack[level] = (int)i;
                    dirEnd[level] = Be.U32(fst, fe + 8);
                    level++;
                    if (level >= 16)
                        throw new InvalidDataException("FST nesting too deep.");
                    continue;
                }

                if ((type & 0x80) != 0)
                    continue; // not a real on-disc file

                var sb = new StringBuilder();
                for (int j = 0; j < level; j++)
                {
                    uint ancRel = Be.U32(fst, (int)(feBase + (long)dirStack[j] * 0x10)) & 0x00FFFFFF;
                    sb.Append(ReadName(fst, nameBase + ancRel));
                    sb.Append('/');
                }
                sb.Append(ReadName(fst, nameBase + nameRel));

                uint rawOffset = Be.U32(fst, fe + 4);
                uint size = Be.U32(fst, fe + 8);
                ushort flags = Be.U16(fst, fe + 12);
                ushort contentIndex = Be.U16(fst, fe + 14);

                ulong offset = (flags & 4) != 0 ? rawOffset : (ulong)rawOffset * (uint)offsetFactor;

                files.Add(new FstFile
                {
                    Path = sb.ToString(),
                    Size = size,
                    OffsetInContent = offset,
                    Flags = flags,
                    ContentIndex = contentIndex,
                });
            }

            return new Fst { OffsetFactor = offsetFactor, Contents = contents, Files = files };
        }

        public FstFile Find(string path)
        {
            foreach (var f in Files)
                if (f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        private static string ReadName(byte[] fst, long nameAbsOffset)
        {
            int o = checked((int)nameAbsOffset);
            int end = o;
            while (end < fst.Length && fst[end] != 0)
                end++;
            return Encoding.UTF8.GetString(fst, o, end - o);
        }
    }
}
