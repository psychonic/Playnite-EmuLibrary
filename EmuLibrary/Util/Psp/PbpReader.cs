using System;
using System.IO;

namespace EmuLibrary.Util.Psp
{
    // Reads the embedded PARAM.SFO out of a PSP PBP (PSN EBOOT / homebrew) container. A PBP has a 40-byte
    // header: magic "\0PBP", a version word, then eight little-endian uint32 offsets to its segments
    // (PARAM.SFO, ICON0.PNG, ICON1.PMF, PIC0.PNG, PIC1.PNG, SND0.AT3, DATA.PSP, DATA.PSAR). PARAM.SFO spans
    // [offset[0], offset[1]); no ISO9660 filesystem is involved.
    internal static class PbpReader
    {
        public static bool TryReadParamSfoBytes(string path, out byte[] sfoBytes)
        {
            sfoBytes = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return TryReadParamSfoBytes(fs, out sfoBytes);
            }
            catch
            {
                sfoBytes = null;
                return false;
            }
        }

        public static bool TryReadParamSfoBytes(Stream stream, out byte[] sfoBytes)
        {
            sfoBytes = null;
            try
            {
                var header = new byte[40];
                stream.Seek(0, SeekOrigin.Begin);
                if (!ReadFully(stream, header, header.Length))
                    return false;

                // Magic "\0PBP".
                if (header[0] != 0x00 || header[1] != (byte)'P' || header[2] != (byte)'B' || header[3] != (byte)'P')
                    return false;

                uint sfoOffset = ReadU32LE(header, 0x08);
                uint icon0Offset = ReadU32LE(header, 0x0C);

                if (icon0Offset <= sfoOffset)
                    return false;

                int sfoLen = (int)(icon0Offset - sfoOffset);
                if (sfoLen <= 0 || sfoLen > (1 << 20))
                    return false;

                var buf = new byte[sfoLen];
                stream.Seek(sfoOffset, SeekOrigin.Begin);
                if (!ReadFully(stream, buf, sfoLen))
                    return false;

                sfoBytes = buf;
                return true;
            }
            catch
            {
                sfoBytes = null;
                return false;
            }
        }

        // Reads the embedded ICON0.PNG (the PBP's second segment) as raw PNG bytes. It spans
        // [offset[1], offset[2]); returns false if that segment is empty (offsets equal) or the bytes aren't a
        // PNG. Used to surface a game icon for PSN/homebrew EBOOT PBPs, which have no ISO9660 filesystem.
        public static bool TryReadIcon0Bytes(string path, out byte[] png)
        {
            png = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return TryReadIcon0Bytes(fs, out png);
            }
            catch
            {
                png = null;
                return false;
            }
        }

        public static bool TryReadIcon0Bytes(Stream stream, out byte[] png)
        {
            png = null;
            try
            {
                var header = new byte[40];
                stream.Seek(0, SeekOrigin.Begin);
                if (!ReadFully(stream, header, header.Length))
                    return false;

                // Magic "\0PBP".
                if (header[0] != 0x00 || header[1] != (byte)'P' || header[2] != (byte)'B' || header[3] != (byte)'P')
                    return false;

                uint icon0Offset = ReadU32LE(header, 0x0C);
                uint icon1Offset = ReadU32LE(header, 0x10);

                if (icon1Offset <= icon0Offset)
                    return false; // no ICON0.PNG segment

                long len = icon1Offset - icon0Offset;
                if (len <= 0 || len > (4 << 20))
                    return false;

                var buf = new byte[len];
                stream.Seek(icon0Offset, SeekOrigin.Begin);
                if (!ReadFully(stream, buf, (int)len))
                    return false;

                if (!Png.HasPngSignature(buf))
                    return false;

                png = buf;
                return true;
            }
            catch
            {
                png = null;
                return false;
            }
        }

        private static bool ReadFully(Stream s, byte[] buf, int length)
        {
            int read = 0;
            while (read < length)
            {
                int n = s.Read(buf, read, length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read == length;
        }

        private static uint ReadU32LE(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
