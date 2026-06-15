using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmuLibrary.Util.Ps3
{
    // Parser for the PS3/PSP PARAM.SFO key/value blob (psdevwiki "PARAM.SFO").
    // Layout:
    //   header  : magic(0x46535000 = "\0PSF"), version(u32), key_table_start(u32),
    //             data_table_start(u32), num_entries(u32)
    //   index   : num_entries * { key_offset(u16), data_fmt(u16), data_len(u32),
    //             data_max_len(u32), data_offset(u32) }
    //   keys    : null-terminated UTF-8 strings (relative to key_table_start)
    //   data    : values (relative to data_table_start)
    // data_fmt: 0x0004 = utf8 (not null-terminated), 0x0204 = utf8 (null-terminated),
    //           0x0404 = uint32. We expose strings (TITLE/TITLE_ID/APP_VER/VERSION/CATEGORY).
    internal sealed class ParamSfo
    {
        private const uint Magic = 0x46535000; // "\0PSF" little-endian

        private readonly Dictionary<string, string> _strings;

        private ParamSfo(Dictionary<string, string> strings)
        {
            _strings = strings;
        }

        public string Title => Get("TITLE");
        public string TitleId => Get("TITLE_ID");
        public string AppVer => Get("APP_VER");
        public string TargetAppVer => Get("TARGET_APP_VER");
        public string Version => Get("VERSION");
        public string Category => Get("CATEGORY");

        public string Get(string key)
        {
            return _strings.TryGetValue(key, out var v) ? v : null;
        }

        public static bool TryParse(byte[] data, out ParamSfo sfo)
        {
            sfo = null;
            if (data == null)
                return false;

            try
            {
                using (var ms = new MemoryStream(data, false))
                {
                    return TryParse(ms, out sfo);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParse(Stream stream, out ParamSfo sfo)
        {
            sfo = null;
            try
            {
                // Read the whole blob; SFOs are tiny (a few KB at most).
                byte[] buf;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    buf = ms.ToArray();
                }

                if (buf.Length < 0x14)
                    return false;

                uint magic = BitConverter.ToUInt32(buf, 0x00);
                if (magic != Magic)
                    return false;

                uint keyTableStart = BitConverter.ToUInt32(buf, 0x08);
                uint dataTableStart = BitConverter.ToUInt32(buf, 0x0C);
                uint numEntries = BitConverter.ToUInt32(buf, 0x10);

                // Sanity-cap to avoid acting on a corrupt header.
                if (numEntries > 1024)
                    return false;

                var result = new Dictionary<string, string>(StringComparer.Ordinal);

                const int indexBase = 0x14;
                const int indexEntrySize = 0x10;

                for (int i = 0; i < numEntries; i++)
                {
                    int entryOff = indexBase + i * indexEntrySize;
                    if (entryOff + indexEntrySize > buf.Length)
                        break;

                    ushort keyOffset = BitConverter.ToUInt16(buf, entryOff + 0x00);
                    ushort dataFmt = BitConverter.ToUInt16(buf, entryOff + 0x02);
                    uint dataLen = BitConverter.ToUInt32(buf, entryOff + 0x04);
                    // entryOff + 0x08 = data_max_len (unused)
                    uint dataOffset = BitConverter.ToUInt32(buf, entryOff + 0x0C);

                    string key = ReadNullTerminated(buf, (int)keyTableStart + keyOffset);
                    if (string.IsNullOrEmpty(key))
                        continue;

                    long valStart = dataTableStart + dataOffset;
                    if (valStart < 0 || valStart > buf.Length)
                        continue;

                    // We only need text values; numeric fmt (0x0404) is ignored here.
                    if (dataFmt == 0x0004 || dataFmt == 0x0204)
                    {
                        int len = (int)Math.Min(dataLen, (uint)(buf.Length - valStart));
                        string value = Encoding.UTF8.GetString(buf, (int)valStart, len).TrimEnd('\0');
                        result[key] = value;
                    }
                }

                sfo = new ParamSfo(result);
                return true;
            }
            catch
            {
                sfo = null;
                return false;
            }
        }

        private static string ReadNullTerminated(byte[] buf, int offset)
        {
            if (offset < 0 || offset >= buf.Length)
                return null;

            int end = offset;
            while (end < buf.Length && buf[end] != 0)
                end++;

            return Encoding.UTF8.GetString(buf, offset, end - offset);
        }
    }
}
