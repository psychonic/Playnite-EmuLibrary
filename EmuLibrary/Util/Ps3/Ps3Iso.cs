using System;
using System.IO;
using System.Text;

namespace EmuLibrary.Util.Ps3
{
    // Minimal ISO9660 reader for locating PARAM.SFO inside a PS3 disc image (ECMA-119 / psdevwiki
    // "Bluray disc"). On a PS3 game disc the ISO9660 filesystem metadata and the PS3_GAME metadata
    // files (PARAM.SFO, ICON0.PNG, ...) live in the disc's UNENCRYPTED region — only USRDIR/EBOOT.BIN
    // and game data sit in the encrypted region(s) — so PARAM.SFO can be read directly from an
    // encrypted .iso with no disc key. Decrypted images and 1:1 encrypted images share the same
    // ISO9660 layout, so the same walk handles both.
    //
    // We seek + read only the handful of sectors we need (volume descriptor, two directory extents,
    // the SFO extent) rather than streaming the whole multi-GB image — important on the SMB scan path.
    // If PARAM.SFO can't be located, or its bytes aren't a valid SFO (e.g. it unexpectedly fell in an
    // encrypted region), callers fall back to the folder/filename heuristics.
    internal static class Ps3Iso
    {
        private const int SectorSize = 2048;
        private const int VolumeDescriptorRecordOffset = 156; // root dir record within a PVD (ECMA-119)
        private const byte VdTypePrimary = 1;
        private const byte VdTypeTerminator = 255;
        private const int DirFlagSubdirectory = 0x02;

        public static bool TryReadParamSfo(string path, out ParamSfo sfo)
        {
            sfo = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return TryReadParamSfo(fs, out sfo);
            }
            catch
            {
                sfo = null;
                return false;
            }
        }

        public static bool TryReadParamSfo(Stream stream, out ParamSfo sfo)
        {
            sfo = null;
            try
            {
                if (!TryReadRootDirectory(stream, out long rootLba, out long rootLen))
                    return false;

                if (!TryFindChild(stream, rootLba, rootLen, "PS3_GAME", wantDirectory: true, out long gameLba, out long gameLen))
                    return false;

                if (!TryFindChild(stream, gameLba, gameLen, "PARAM.SFO", wantDirectory: false, out long sfoLba, out long sfoLen))
                    return false;

                // Guard against a corrupt/implausible length before allocating.
                if (sfoLen <= 0 || sfoLen > (1 << 20))
                    return false;

                var sfoBytes = ReadBytes(stream, sfoLba * SectorSize, (int)sfoLen);
                return ParamSfo.TryParse(sfoBytes, out sfo);
            }
            catch
            {
                sfo = null;
                return false;
            }
        }

        // Finds the Primary Volume Descriptor (normally at sector 16) and returns its root directory extent.
        private static bool TryReadRootDirectory(Stream stream, out long rootLba, out long rootLen)
        {
            rootLba = 0;
            rootLen = 0;

            // The volume descriptor set begins at sector 16 and is terminated by a type-255 descriptor.
            for (long sector = 16; sector < 16 + 32; sector++)
            {
                var vd = ReadBytes(stream, sector * SectorSize, SectorSize);
                if (vd.Length < 7)
                    return false;

                // Every descriptor carries the "CD001" standard identifier; its absence means not an ISO9660.
                if (vd[1] != 'C' || vd[2] != 'D' || vd[3] != '0' || vd[4] != '0' || vd[5] != '1')
                    return false;

                byte type = vd[0];
                if (type == VdTypeTerminator)
                    return false;

                if (type == VdTypePrimary)
                {
                    rootLba = ReadU32LE(vd, VolumeDescriptorRecordOffset + 2);
                    rootLen = ReadU32LE(vd, VolumeDescriptorRecordOffset + 10);
                    return rootLen > 0;
                }
            }

            return false;
        }

        // Scans a directory extent for a child record matching name (case-insensitive, version suffix
        // stripped) of the requested kind.
        private static bool TryFindChild(Stream stream, long dirLba, long dirLen, string name, bool wantDirectory, out long childLba, out long childLen)
        {
            childLba = 0;
            childLen = 0;

            // Cap the read so a corrupt length can't trigger a huge allocation; real PS3 root/PS3_GAME
            // directories are a sector or two.
            int sectorCount = (int)Math.Min((dirLen + SectorSize - 1) / SectorSize, 1024);
            var data = ReadBytes(stream, dirLba * SectorSize, sectorCount * SectorSize);

            int pos = 0;
            while (pos < data.Length)
            {
                int recLen = data[pos];
                if (recLen == 0)
                {
                    // A zero length pads out the rest of the sector; advance to the next sector boundary.
                    pos = ((pos / SectorSize) + 1) * SectorSize;
                    continue;
                }
                if (recLen < 33 || pos + recLen > data.Length)
                    break;

                int idLen = data[pos + 32];
                if (idLen <= 0 || pos + 33 + idLen > data.Length)
                {
                    pos += recLen;
                    continue;
                }

                bool isDir = (data[pos + 25] & DirFlagSubdirectory) != 0;

                // Skip the "." (0x00) and ".." (0x01) self/parent entries.
                bool isSelfOrParent = idLen == 1 && (data[pos + 33] == 0x00 || data[pos + 33] == 0x01);
                if (!isSelfOrParent && isDir == wantDirectory)
                {
                    string id = Encoding.ASCII.GetString(data, pos + 33, idLen);
                    int semi = id.IndexOf(';'); // strip the ";1" version suffix on file identifiers
                    if (semi >= 0)
                        id = id.Substring(0, semi);

                    if (string.Equals(id, name, StringComparison.OrdinalIgnoreCase))
                    {
                        childLba = ReadU32LE(data, pos + 2);
                        childLen = ReadU32LE(data, pos + 10);
                        return true;
                    }
                }

                pos += recLen;
            }

            return false;
        }

        private static byte[] ReadBytes(Stream stream, long offset, int length)
        {
            var buf = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(buf, read, length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read < length)
                Array.Resize(ref buf, read);
            return buf;
        }

        private static uint ReadU32LE(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
