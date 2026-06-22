using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // One game partition (GM...) of a Wii U disc: where its content begins and its (disc-key-decrypted)
    // tmd/ticket/cert (which come from the SI partition).
    internal sealed class GamePartition
    {
        public string Name;
        public long PartitionDataOffset; // absolute disc offset where the partition's content/FST begins
        public byte[] RawTmd;
        public byte[] RawTicket;
        public byte[] RawCert;
        public ulong TitleId;
        public ushort TitleVersion;
        public WiiUContentKind Kind;
    }

    // Parses a Wii U disc image (WUD or WUX), decrypting the metadata layer with the disc key (the .key file
    // beside the image) to recover the partition table, the SI partition's per-game tmd/ticket/cert, and each
    // GM game partition's content offset. Ported from Maschell/JNUSLib (WUDInfoParser). Game content itself is
    // read raw and decrypted with the title key by NusReader (see WudContentSource).
    internal sealed class WiiUDisc : IDisposable
    {
        private const int Sector = 0x8000;
        private const int DecryptedAreaOffset = 0x18000;
        private const int TocOffset = 0x800;
        private const int TocEntrySize = 0x80;

        private static readonly byte[] SigDecryptedArea = { 0xCC, 0xA6, 0xE6, 0x7B };
        private static readonly byte[] SigPartitionStart = { 0xCC, 0x93, 0xA4, 0xF5 };

        public DiscReader Reader { get; private set; }
        public byte[] DiscKey { get; private set; }
        public List<GamePartition> GamePartitions { get; } = new List<GamePartition>();

        public static WiiUDisc Open(string discPath, string keyPath)
        {
            var discKey = File.ReadAllBytes(keyPath);
            if (discKey.Length != 16)
                throw new InvalidDataException($"Wii U disc key \"{keyPath}\" must be 16 bytes (was {discKey.Length}).");

            var reader = DiscReader.Open(discPath);
            try
            {
                var disc = new WiiUDisc { Reader = reader, DiscKey = discKey };
                disc.Parse();
                return disc;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        private void Parse()
        {
            // Partition TOC (disc-key decrypted, fixed zero IV).
            var toc = Reader.ReadDecrypted(DecryptedAreaOffset, 0, Sector, DiscKey, null, true);
            if (!StartsWith(toc, SigDecryptedArea))
                throw new InvalidDataException("Disc key appears invalid (partition TOC signature mismatch).");

            uint partitionCount = Be.U32(toc, 0x1C);
            var partitionOffsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < partitionCount; i++)
            {
                int o = TocOffset + i * TocEntrySize;
                string name = ReadCString(toc, o, 0x19);
                long offsetInSector = Be.U32(toc, o + 0x20);
                partitionOffsets[name] = offsetInSector * Sector;
            }

            var si = partitionOffsets.FirstOrDefault(p => p.Key.StartsWith("SI", StringComparison.OrdinalIgnoreCase));
            if (si.Key == null)
                throw new InvalidDataException("Disc has no SI partition.");
            long siOffset = si.Value;

            var siHeader = Reader.ReadRaw(siOffset, 0x20);
            if (!StartsWith(siHeader, SigPartitionStart))
                throw new InvalidDataException("SI partition header signature mismatch.");
            long siHeaderSize = Be.U32(siHeader, 0x04);
            long siFstSize = Be.U32(siHeader, 0x14);

            var siFstBytes = Reader.ReadDecrypted(siOffset + siHeaderSize, 0, (int)siFstSize, DiscKey, null, true);
            var siFst = Fst.Parse(siFstBytes);

            // The SI partition holds <game>/title.{tik,tmd,cert} for each GM partition.
            foreach (var tik in siFst.Files.Where(f => f.Path.EndsWith("/title.tik", StringComparison.OrdinalIgnoreCase)
                                                       || f.Path.Equals("title.tik", StringComparison.OrdinalIgnoreCase)))
            {
                string dir = tik.Path.Length > "title.tik".Length
                    ? tik.Path.Substring(0, tik.Path.Length - "title.tik".Length)
                    : "";
                var tmd = siFst.Find(dir + "title.tmd");
                var cert = siFst.Find(dir + "title.cert");
                if (tmd == null)
                    continue;

                byte[] rawTik = ReadSiFile(siFst, tik, siOffset, siHeaderSize);
                byte[] rawTmd = ReadSiFile(siFst, tmd, siOffset, siHeaderSize);
                byte[] rawCert = cert != null ? ReadSiFile(siFst, cert, siOffset, siHeaderSize) : null;

                // The GM partition name is "GM" + the ticket's title id (8 bytes at 0x1DC).
                string gmName = "GM" + BytesToHex(rawTik, 0x1DC, 8);
                var gm = partitionOffsets.FirstOrDefault(p => p.Key.StartsWith(gmName, StringComparison.OrdinalIgnoreCase));
                if (gm.Key == null)
                    continue;

                var gmHeader = Reader.ReadRaw(gm.Value, 0x20);
                if (!StartsWith(gmHeader, SigPartitionStart))
                    throw new InvalidDataException($"GM partition \"{gm.Key}\" header signature mismatch.");
                long gmHeaderSize = Be.U32(gmHeader, 0x04);

                var meta = TitleMetadata.Parse(rawTmd);
                GamePartitions.Add(new GamePartition
                {
                    Name = gm.Key,
                    PartitionDataOffset = gm.Value + gmHeaderSize,
                    RawTmd = rawTmd,
                    RawTicket = rawTik,
                    RawCert = rawCert,
                    TitleId = meta.TitleId,
                    TitleVersion = meta.TitleVersion,
                    Kind = meta.Kind,
                });
            }
        }

        // Reads one SI-partition file (tmd/tik/cert), disc-key decrypted with a per-file IV.
        private byte[] ReadSiFile(Fst siFst, FstFile file, long siOffset, long siHeaderSize)
        {
            long clusterOffset = siHeaderSize + siOffset + siFst.Contents[file.ContentIndex].Offset;
            return Reader.ReadDecrypted(clusterOffset, (long)file.OffsetInContent, (int)file.Size, DiscKey, null, false);
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i])
                    return false;
            return true;
        }

        private static string ReadCString(byte[] buf, int offset, int maxLen)
        {
            int end = offset;
            int limit = Math.Min(buf.Length, offset + maxLen);
            while (end < limit && buf[end] != 0)
                end++;
            return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset);
        }

        private static string BytesToHex(byte[] b, int offset, int count)
        {
            var sb = new System.Text.StringBuilder(count * 2);
            for (int i = 0; i < count; i++)
                sb.Append(b[offset + i].ToString("x2"));
            return sb.ToString();
        }

        public void Dispose() => Reader?.Dispose();
    }

    // INusContentSource over a disc game partition: tmd/ticket come from the partition; content bytes are read
    // raw from the disc at the partition data offset + the content's ContentFstInfo offset.
    internal sealed class WudContentSource : INusContentSource
    {
        private readonly DiscReader _disc;
        private readonly long _partitionDataOffset;
        private readonly TitleMetadata _tmd;
        private ContentFstInfo[] _layout;

        public byte[] Tmd { get; }
        public byte[] Ticket { get; }

        public WudContentSource(DiscReader disc, GamePartition partition)
        {
            _disc = disc;
            _partitionDataOffset = partition.PartitionDataOffset;
            Tmd = partition.RawTmd;
            Ticket = partition.RawTicket;
            _tmd = TitleMetadata.Parse(Tmd);
        }

        // content[0] (the FST) is at partition offset 0.
        public byte[] ReadFstContent() => _disc.ReadRaw(_partitionDataOffset, (int)_tmd.Contents[0].Size);

        public void SetContentLayout(ContentFstInfo[] contentFstInfos) => _layout = contentFstInfos;

        public void ReadRawContent(int contentIndex, long offsetInContent, byte[] buffer, int count)
        {
            long contentOffset = (contentIndex == 0 || _layout == null) ? 0 : _layout[contentIndex].Offset;
            _disc.ReadRaw(_partitionDataOffset + contentOffset + offsetInContent, buffer, 0, count);
        }

        // The DiscReader is owned by the WiiUDisc, not by this source.
        public void Dispose() { }
    }
}
