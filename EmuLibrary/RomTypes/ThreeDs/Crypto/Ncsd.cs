using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // Minimal NCSD reader for cartridge images (.cci / .3ds). An NCSD wraps up to 8 NCCH partitions (0 = main
    // CXI, 1 = manual, 2 = download-play child, 6/7 = update data, ...). Only the partition table is needed
    // here, to locate each partition's NCCH so it can be decrypted in place. The header is little-endian and
    // always plaintext (even in an encrypted dump).
    internal static class Ncsd
    {
        private const int MagicOffset = 0x100;
        private const int MediaIdOffset = 0x108;
        private const int PartitionTableOffset = 0x120;
        private const int PartitionFlagsOffset = 0x188;
        private const int PartitionCount = 8;

        public sealed class Partition
        {
            public int Index;
            public long Offset; // byte offset of the partition's NCCH within the file
            public long Size;   // bytes
        }

        public static bool IsNcsd(FileStream fs)
        {
            if (fs.Length < MagicOffset + 4)
                return false;
            var magic = new byte[4];
            fs.Seek(MagicOffset, SeekOrigin.Begin);
            fs.Read(magic, 0, 4);
            return Encoding.ASCII.GetString(magic) == "NCSD";
        }

        // Reads the partition table, returning the present (size > 0) partitions with byte offsets/sizes.
        public static IReadOnlyList<Partition> ReadPartitions(FileStream fs)
        {
            var result = new List<Partition>();
            if (fs.Length < PartitionFlagsOffset + 8)
                return result;

            var header = new byte[0x200];
            fs.Seek(0, SeekOrigin.Begin);
            int read = 0;
            while (read < header.Length)
            {
                int n = fs.Read(header, read, header.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }

            int mediaUnit = 0x200 << header[PartitionFlagsOffset + 6];

            for (int i = 0; i < PartitionCount; i++)
            {
                int o = PartitionTableOffset + i * 8;
                uint offMu = Le.U32(header, o);
                uint sizeMu = Le.U32(header, o + 4);
                if (sizeMu == 0)
                    continue;
                result.Add(new Partition
                {
                    Index = i,
                    Offset = (long)offMu * mediaUnit,
                    Size = (long)sizeMu * mediaUnit,
                });
            }
            return result;
        }
    }
}
