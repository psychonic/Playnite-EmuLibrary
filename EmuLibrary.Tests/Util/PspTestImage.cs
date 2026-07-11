using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmuLibrary.Tests.Util
{
    // Shared synthetic-image builders for the PSP tests: a minimal PARAM.SFO and a minimal ISO9660 volume with
    // /PSP_GAME/PARAM.SFO. Used by both PspImageReaderTests (wrapping the ISO in each container) and
    // PspScannerTests (a plain .iso on disk to exercise association/BuildTitle).
    internal static class PspTestImage
    {
        // A UMD PARAM.SFO (DISC_ID) or PBP/PSN one (TITLE_ID). Extra string entries can be appended (e.g.
        // DISC_VERSION / APP_VER).
        public static byte[] BuildSfo(string discId, bool titleIdOnly = false, params (string Key, string Value)[] extra)
        {
            var entries = new List<(string, string)>
            {
                ("CATEGORY", "UG"),
                ("TITLE", "Test PSP Game"),
            };
            if (titleIdOnly)
                entries.Add(("TITLE_ID", discId));
            else
                entries.Add(("DISC_ID", discId));
            if (extra != null)
                foreach (var e in extra)
                    entries.Add((e.Key, e.Value));

            var keyBytes = new List<byte>();
            var keyOffsets = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                keyOffsets[i] = keyBytes.Count;
                keyBytes.AddRange(Encoding.UTF8.GetBytes(entries[i].Item1));
                keyBytes.Add(0);
            }

            var dataBytes = new List<byte>();
            var dataOffsets = new int[entries.Count];
            var dataLens = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                dataOffsets[i] = dataBytes.Count;
                var v = Encoding.UTF8.GetBytes(entries[i].Item2);
                dataBytes.AddRange(v);
                dataBytes.Add(0);
                dataLens[i] = v.Length + 1;
            }

            int indexSize = entries.Count * 0x10;
            int keyTableStart = 0x14 + indexSize;
            int dataTableStart = keyTableStart + keyBytes.Count;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new byte[] { 0x00, (byte)'P', (byte)'S', (byte)'F' });
                w.Write((uint)0x00000101);
                w.Write((uint)keyTableStart);
                w.Write((uint)dataTableStart);
                w.Write((uint)entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    w.Write((ushort)keyOffsets[i]);
                    w.Write((ushort)0x0204);
                    w.Write((uint)dataLens[i]);
                    w.Write((uint)dataLens[i]);
                    w.Write((uint)dataOffsets[i]);
                }
                w.Write(keyBytes.ToArray());
                w.Write(dataBytes.ToArray());
                return ms.ToArray();
            }
        }

        // Minimal ISO9660 with /PSP_GAME/PARAM.SFO: PVD at sector 16, root dir 17, PSP_GAME dir 18, SFO 19.
        // When icon0 is supplied, a /PSP_GAME/ICON0.PNG file is added at sector 20 (so the PSP_GAME dir gets a
        // fourth child record).
        public static byte[] BuildPspIso(byte[] sfoBytes, byte[] icon0 = null)
        {
            const int sector = 2048;
            const long rootLba = 17, gameLba = 18, sfoLba = 19, iconLba = 20;
            int totalSectors = icon0 != null ? 21 : 20;
            var iso = new byte[totalSectors * sector];

            int pvd = 16 * sector;
            iso[pvd + 0] = 1;
            iso[pvd + 1] = (byte)'C'; iso[pvd + 2] = (byte)'D'; iso[pvd + 3] = (byte)'0';
            iso[pvd + 4] = (byte)'0'; iso[pvd + 5] = (byte)'1';
            iso[pvd + 6] = 1;
            var rootRec = BuildDirRecord("\0", rootLba, sector, true);
            Buffer.BlockCopy(rootRec, 0, iso, pvd + 156, rootRec.Length);

            WriteRecords(iso, (int)rootLba * sector,
                BuildDirRecord("\0", rootLba, sector, true),
                BuildDirRecord("\x01", rootLba, sector, true),
                BuildDirRecord("PSP_GAME", gameLba, sector, true));

            if (icon0 == null)
            {
                WriteRecords(iso, (int)gameLba * sector,
                    BuildDirRecord("\0", gameLba, sector, true),
                    BuildDirRecord("\x01", rootLba, sector, true),
                    BuildDirRecord("PARAM.SFO;1", sfoLba, sfoBytes.Length, false));
            }
            else
            {
                WriteRecords(iso, (int)gameLba * sector,
                    BuildDirRecord("\0", gameLba, sector, true),
                    BuildDirRecord("\x01", rootLba, sector, true),
                    BuildDirRecord("PARAM.SFO;1", sfoLba, sfoBytes.Length, false),
                    BuildDirRecord("ICON0.PNG;1", iconLba, icon0.Length, false));
                Buffer.BlockCopy(icon0, 0, iso, (int)iconLba * sector, Math.Min(icon0.Length, sector));
            }

            Buffer.BlockCopy(sfoBytes, 0, iso, (int)sfoLba * sector, Math.Min(sfoBytes.Length, sector));
            return iso;
        }

        // A minimal byte blob that passes the PNG-signature guard (8-byte magic + filler). Not a decodable PNG,
        // but the readers only validate the signature and return the raw bytes verbatim, so it round-trips.
        public static byte[] FakePng(int totalLength = 64)
        {
            if (totalLength < 8) totalLength = 8;
            var png = new byte[totalLength];
            var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Buffer.BlockCopy(sig, 0, png, 0, sig.Length);
            for (int i = sig.Length; i < png.Length; i++)
                png[i] = (byte)(i * 7 + 1);
            return png;
        }

        private static void WriteRecords(byte[] iso, int offset, params byte[][] records)
        {
            int o = offset;
            foreach (var r in records)
            {
                Buffer.BlockCopy(r, 0, iso, o, r.Length);
                o += r.Length;
            }
        }

        private static byte[] BuildDirRecord(string id, long lba, long len, bool isDir)
        {
            byte[] idBytes = id == "\0" ? new byte[] { 0x00 }
                : id == "\x01" ? new byte[] { 0x01 }
                : Encoding.ASCII.GetBytes(id);

            int recLen = 33 + idBytes.Length;
            if ((recLen & 1) != 0) recLen++;

            var rec = new byte[recLen];
            rec[0] = (byte)recLen;
            WriteU32LE(rec, 2, (uint)lba);
            WriteU32LE(rec, 10, (uint)len);
            rec[25] = (byte)(isDir ? 0x02 : 0x00);
            rec[32] = (byte)idBytes.Length;
            Buffer.BlockCopy(idBytes, 0, rec, 33, idBytes.Length);
            return rec;
        }

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
    }
}
