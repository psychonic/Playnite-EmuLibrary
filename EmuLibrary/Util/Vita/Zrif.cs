using System;
using System.IO;
using System.Text;

namespace EmuLibrary.Util.Vita
{
    // Decodes a Vita zRIF (the compact license representation used by NoNpDrm / pkg2zip) into a raw RIF, and
    // reads a title's license (work.bin, which may be a raw binary RIF or a zRIF string) into the RIF bytes the
    // emulator wants at ux0/license/<TITLEID>/<CONTENTID>.rif.
    //
    // A zRIF is a base64 string whose bytes are a zlib stream that inflates — with a fixed preset dictionary —
    // to a 512-byte RIF (SceNpDrmLicense). The RIF carries the content-id (offset 0x10, 0x30 bytes) and the
    // (encrypted) klicensee (offset 0x50, 0x10 bytes).
    //
    // Reference (permissively licensed, algorithm only): mmozeiko's pkg2zip (pkg2zip_zrif.c) and Mark Adler's
    // puff.c (zlib contrib, ported below). The preset dictionary + dictionary id are the pkg2zip constants.
    //
    // [VERIFY] Whether the PSN NoIntro work.bin files are raw binary RIFs or zRIF strings, the exact RIF the
    // Vita3K ux0/license path expects, and end-to-end correctness are open items for the deferred manual test
    // (no real work.bin samples were available at implementation time). The inflate itself is exercised by unit
    // tests against .NET-produced deflate streams (no-dictionary path); the preset-dictionary path is [VERIFY].
    internal static class Zrif
    {
        private const int RifSize = 512;              // Vita APP/DLC/PATCH RIF
        private const int RifContentIdOffset = 0x10;  // 0x30 bytes
        private const int RifKlicenseeOffset = 0x50;  // 0x10 bytes
        private const uint ZrifDictionaryId = 0x627D1D5D;
        private const int ZlibDeflateMethod = 8;

        // Reads a title's license source (work.bin) into RIF bytes. Accepts either a raw binary RIF (passed
        // through) or a zRIF base64 string (decoded). Never throws for a malformed input — returns the raw bytes
        // as a last resort so the install still writes *something* the user can inspect.
        public static byte[] ReadRifFromWorkBin(string workBinPath)
        {
            var bytes = File.ReadAllBytes(workBinPath);
            if (LooksLikeZrifText(bytes, out var text))
            {
                try
                {
                    return DecodeToRif(text);
                }
                catch
                {
                    // Fall through: not a decodable zRIF; treat as raw RIF.
                }
            }
            return bytes;
        }

        // Decodes a zRIF base64 string into the raw RIF blob.
        public static byte[] DecodeToRif(string zrif)
        {
            if (string.IsNullOrWhiteSpace(zrif))
                throw new ArgumentException("Empty zRIF string.", nameof(zrif));

            var raw = Base64Decode(zrif.Trim());
            var rif = ZlibInflate(raw);
            return rif;
        }

        public static string GetContentId(byte[] rif)
        {
            if (rif == null || rif.Length < RifContentIdOffset + 0x30)
                return null;
            var s = Encoding.ASCII.GetString(rif, RifContentIdOffset, 0x30).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        public static byte[] GetKlicensee(byte[] rif)
        {
            if (rif == null || rif.Length < RifKlicenseeOffset + 0x10)
                return null;
            var k = new byte[0x10];
            Buffer.BlockCopy(rif, RifKlicenseeOffset, k, 0, 0x10);
            return k;
        }

        // A zRIF is a printable base64 string; a raw RIF is binary (and typically 512+ bytes). Heuristic: all
        // bytes are base64/whitespace characters.
        private static bool LooksLikeZrifText(byte[] bytes, out string text)
        {
            text = null;
            if (bytes == null || bytes.Length == 0 || bytes.Length > 4096)
                return false;
            foreach (var b in bytes)
            {
                bool ok = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                    || b == '+' || b == '/' || b == '=' || b == '\r' || b == '\n' || b == ' ' || b == '\t';
                if (!ok)
                    return false;
            }
            text = Encoding.ASCII.GetString(bytes).Trim();
            return text.Length >= 4;
        }

        // Standard base64 decode, tolerant of missing padding (zRIF strings are frequently stored unpadded).
        // Strips whitespace and re-pads to a multiple of 4 before decoding. (pkg2zip's hand-rolled decoder emits
        // a spurious trailing byte for the "3 leftover chars" case and relies on exact-size framing to ignore
        // it; standard decoding is unambiguous.)
        internal static byte[] Base64Decode(string input)
        {
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (c != '\r' && c != '\n' && c != ' ' && c != '\t')
                    sb.Append(c);
            }
            var s = sb.ToString();
            int pad = (4 - (s.Length % 4)) % 4;
            if (pad > 0)
                s += new string('=', pad);
            return Convert.FromBase64String(s);
        }

        // RFC1950 zlib wrapper: validate the 2-byte header, optionally consume the preset-dictionary id and
        // prepend the fixed dictionary to the inflate window, inflate, then verify the trailing adler-32.
        private static byte[] ZlibInflate(byte[] input)
        {
            if (input.Length < 2 + 4)
                throw new InvalidDataException("zRIF too short.");

            if (((input[0] << 8) + input[1]) % 31 != 0)
                throw new InvalidDataException("zRIF header checksum is invalid.");
            if ((input[0] & 0xF) != ZlibDeflateMethod)
                throw new InvalidDataException("zRIF is not deflate-compressed.");

            int inPos;
            int dictLen;
            var outBuf = new byte[ZrifDict.Length + 2048];

            if ((input[1] & (1 << 5)) != 0)
            {
                // FDICT set: the fixed zRIF dictionary primes the window.
                uint dictId = ((uint)input[2] << 24) | ((uint)input[3] << 16) | ((uint)input[4] << 8) | input[5];
                if (dictId != ZrifDictionaryId)
                    throw new InvalidDataException("zRIF uses an unknown dictionary.");
                Buffer.BlockCopy(ZrifDict, 0, outBuf, 0, ZrifDict.Length);
                dictLen = ZrifDict.Length;
                inPos = 6;
            }
            else
            {
                dictLen = 0;
                inPos = 2;
            }

            int adlerPos = input.Length - 4;
            int deflateLen = adlerPos - inPos;
            if (deflateLen < 0)
                throw new InvalidDataException("zRIF too short for deflate payload.");

            int produced = Puff(outBuf, dictLen, input, inPos, deflateLen);

            var result = new byte[produced];
            Buffer.BlockCopy(outBuf, dictLen, result, 0, produced);

            uint wantAdler = ((uint)input[adlerPos] << 24) | ((uint)input[adlerPos + 1] << 16)
                | ((uint)input[adlerPos + 2] << 8) | input[adlerPos + 3];
            if (Adler32(result) != wantAdler)
                throw new InvalidDataException("zRIF checksum mismatch (corrupt).");

            return result;
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        // ---- RFC1951 inflate, ported from Mark Adler's puff.c (zlib contrib), with pkg2zip's dictionary
        // prefix. `output` holds the preset dictionary in [0, dictLen); decompressed data is written starting at
        // dictLen and may back-reference into the dictionary. Returns the number of decompressed bytes. ----

        private const int MaxBits = 15;
        private const int MaxLCodes = 286;
        private const int MaxDCodes = 30;
        private const int FixLCodes = 288;

        private static readonly short[] LengthBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
            35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
        };
        private static readonly short[] LengthExtra =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
        };
        private static readonly short[] DistBase =
        {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
            8193, 12289, 16385, 24577
        };
        private static readonly short[] DistExtra =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
        };
        private static readonly short[] CodeLengthOrder =
            { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        private sealed class Huffman
        {
            public short[] Count;
            public short[] Symbol;
        }

        private sealed class InflateState
        {
            public byte[] Out;
            public int OutCnt;
            public byte[] In;
            public int InBase;
            public int InLen;
            public int InCnt;
            public int BitBuf;
            public int BitCnt;
        }

        private static int Puff(byte[] output, int dictLen, byte[] input, int inputOffset, int inputLen)
        {
            var s = new InflateState
            {
                Out = output,
                OutCnt = dictLen,
                In = input,
                InBase = inputOffset,
                InLen = inputLen,
                InCnt = 0,
                BitBuf = 0,
                BitCnt = 0,
            };

            int last;
            do
            {
                last = Bits(s, 1);
                int type = Bits(s, 2);
                switch (type)
                {
                    case 0: Stored(s); break;
                    case 1: Codes(s, FixedLenCode, FixedDistCode); break;
                    case 2: Dynamic(s); break;
                    default: throw new InvalidDataException("Invalid deflate block type.");
                }
            } while (last == 0);

            return s.OutCnt - dictLen;
        }

        private static int Bits(InflateState s, int need)
        {
            long val = s.BitBuf;
            while (s.BitCnt < need)
            {
                if (s.InCnt == s.InLen)
                    throw new InvalidDataException("Deflate ran out of input.");
                val |= (long)s.In[s.InBase + s.InCnt++] << s.BitCnt;
                s.BitCnt += 8;
            }
            s.BitBuf = (int)(val >> need);
            s.BitCnt -= need;
            return (int)(val & ((1L << need) - 1));
        }

        private static void Stored(InflateState s)
        {
            s.BitBuf = 0;
            s.BitCnt = 0;

            if (s.InCnt + 4 > s.InLen)
                throw new InvalidDataException("Deflate stored block truncated.");
            int len = s.In[s.InBase + s.InCnt++];
            len |= s.In[s.InBase + s.InCnt++] << 8;
            int nlen = s.In[s.InBase + s.InCnt++];
            nlen |= s.In[s.InBase + s.InCnt++] << 8;
            if (nlen != ((~len) & 0xFFFF))
                throw new InvalidDataException("Deflate stored block length complement mismatch.");

            if (s.InCnt + len > s.InLen)
                throw new InvalidDataException("Deflate stored block truncated.");
            while (len-- > 0)
                s.Out[s.OutCnt++] = s.In[s.InBase + s.InCnt++];
        }

        private static int Decode(InflateState s, Huffman h)
        {
            int code = 0, first = 0, index = 0;
            int bitbuf = s.BitBuf;
            int left = s.BitCnt;
            int len = 1;
            int next = 1; // index into h.Count
            while (true)
            {
                while (left-- > 0)
                {
                    code |= bitbuf & 1;
                    bitbuf >>= 1;
                    int count = h.Count[next++];
                    if (code - count < first)
                    {
                        s.BitBuf = bitbuf;
                        s.BitCnt = (s.BitCnt - len) & 7;
                        return h.Symbol[index + (code - first)];
                    }
                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                    len++;
                }
                left = (MaxBits + 1) - len;
                if (left == 0)
                    break;
                if (s.InCnt == s.InLen)
                    throw new InvalidDataException("Deflate ran out of input.");
                bitbuf = s.In[s.InBase + s.InCnt++];
                if (left > 8)
                    left = 8;
            }
            throw new InvalidDataException("Deflate ran out of codes.");
        }

        private static int Construct(Huffman h, short[] length, int n)
        {
            int len, symbol;
            for (len = 0; len <= MaxBits; len++)
                h.Count[len] = 0;
            for (symbol = 0; symbol < n; symbol++)
                h.Count[length[symbol]]++;
            if (h.Count[0] == n)
                return 0;

            int left = 1;
            for (len = 1; len <= MaxBits; len++)
            {
                left <<= 1;
                left -= h.Count[len];
                if (left < 0)
                    return left;
            }

            var offs = new short[MaxBits + 1];
            offs[1] = 0;
            for (len = 1; len < MaxBits; len++)
                offs[len + 1] = (short)(offs[len] + h.Count[len]);

            for (symbol = 0; symbol < n; symbol++)
                if (length[symbol] != 0)
                    h.Symbol[offs[length[symbol]]++] = (short)symbol;

            return left;
        }

        private static void Codes(InflateState s, Huffman lencode, Huffman distcode)
        {
            int symbol;
            do
            {
                symbol = Decode(s, lencode);
                if (symbol < 0)
                    throw new InvalidDataException("Invalid deflate symbol.");
                if (symbol < 256)
                {
                    s.Out[s.OutCnt++] = (byte)symbol;
                }
                else if (symbol > 256)
                {
                    symbol -= 257;
                    if (symbol >= 29)
                        throw new InvalidDataException("Invalid deflate length code.");
                    int len = LengthBase[symbol] + Bits(s, LengthExtra[symbol]);

                    symbol = Decode(s, distcode);
                    if (symbol < 0)
                        throw new InvalidDataException("Invalid deflate distance symbol.");
                    int dist = DistBase[symbol] + Bits(s, DistExtra[symbol]);
                    if (dist > s.OutCnt)
                        throw new InvalidDataException("Deflate distance too far back.");

                    while (len-- > 0)
                    {
                        s.Out[s.OutCnt] = s.Out[s.OutCnt - dist];
                        s.OutCnt++;
                    }
                }
            } while (symbol != 256);
        }

        private static void Dynamic(InflateState s)
        {
            int nlen = Bits(s, 5) + 257;
            int ndist = Bits(s, 5) + 1;
            int ncode = Bits(s, 4) + 4;
            if (nlen > MaxLCodes || ndist > MaxDCodes)
                throw new InvalidDataException("Bad deflate code counts.");

            var lengths = new short[MaxLCodes + MaxDCodes];
            int index;
            for (index = 0; index < ncode; index++)
                lengths[CodeLengthOrder[index]] = (short)Bits(s, 3);
            for (; index < 19; index++)
                lengths[CodeLengthOrder[index]] = 0;

            var lencode = new Huffman { Count = new short[MaxBits + 1], Symbol = new short[MaxLCodes] };
            var distcode = new Huffman { Count = new short[MaxBits + 1], Symbol = new short[MaxDCodes] };

            if (Construct(lencode, lengths, 19) != 0)
                throw new InvalidDataException("Incomplete deflate code-length code.");

            index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(s, lencode);
                if (symbol < 0)
                    throw new InvalidDataException("Invalid deflate symbol.");
                if (symbol < 16)
                {
                    lengths[index++] = (short)symbol;
                }
                else
                {
                    int len = 0;
                    if (symbol == 16)
                    {
                        if (index == 0)
                            throw new InvalidDataException("Deflate repeat with no last length.");
                        len = lengths[index - 1];
                        symbol = 3 + Bits(s, 2);
                    }
                    else if (symbol == 17)
                    {
                        symbol = 3 + Bits(s, 3);
                    }
                    else
                    {
                        symbol = 11 + Bits(s, 7);
                    }
                    if (index + symbol > nlen + ndist)
                        throw new InvalidDataException("Too many deflate lengths.");
                    while (symbol-- > 0)
                        lengths[index++] = (short)len;
                }
            }

            if (lengths[256] == 0)
                throw new InvalidDataException("Deflate has no end-of-block code.");

            int err = Construct(lencode, lengths, nlen);
            if (err != 0 && (err < 0 || nlen != lencode.Count[0] + lencode.Count[1]))
                throw new InvalidDataException("Incomplete deflate literal/length code.");

            var distLengths = new short[MaxDCodes];
            Array.Copy(lengths, nlen, distLengths, 0, ndist);
            err = Construct(distcode, distLengths, ndist);
            if (err != 0 && (err < 0 || ndist != distcode.Count[0] + distcode.Count[1]))
                throw new InvalidDataException("Incomplete deflate distance code.");

            Codes(s, lencode, distcode);
        }

        private static readonly Huffman FixedLenCode = BuildFixedLenCode();
        private static readonly Huffman FixedDistCode = BuildFixedDistCode();

        private static Huffman BuildFixedLenCode()
        {
            var lengths = new short[FixLCodes];
            int symbol;
            for (symbol = 0; symbol < 144; symbol++) lengths[symbol] = 8;
            for (; symbol < 256; symbol++) lengths[symbol] = 9;
            for (; symbol < 280; symbol++) lengths[symbol] = 7;
            for (; symbol < FixLCodes; symbol++) lengths[symbol] = 8;
            var h = new Huffman { Count = new short[MaxBits + 1], Symbol = new short[FixLCodes] };
            Construct(h, lengths, FixLCodes);
            return h;
        }

        private static Huffman BuildFixedDistCode()
        {
            var lengths = new short[MaxDCodes];
            for (int symbol = 0; symbol < MaxDCodes; symbol++) lengths[symbol] = 5;
            var h = new Huffman { Count = new short[MaxBits + 1], Symbol = new short[MaxDCodes] };
            Construct(h, lengths, MaxDCodes);
            return h;
        }

        // The fixed 1024-byte zRIF preset dictionary (pkg2zip zrif_dict). Mostly zero, with the trailing
        // template bytes (content-id prefixes, ADDCONT, etc.). Copied verbatim from pkg2zip_zrif.c.
        private static readonly byte[] ZrifDict = BuildZrifDict();

        private static byte[] BuildZrifDict()
        {
            var d = new byte[1024];
            // Bytes [0, 979) are zero. The non-zero tail begins at index 979 (per pkg2zip_zrif.c).
            byte[] tail =
            {
                48, 48, 48, 48, 57, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 48, 48,
                48, 48, 54, 48, 48, 48, 48, 55, 48, 48, 48, 48, 56, 0, 48, 48, 48, 48, 51, 48, 48, 48, 48,
                52, 48, 48, 48, 48, 53, 48, 95, 48, 48, 45, 65, 68, 68, 67, 79, 78, 84, 48, 48, 48, 48, 50,
                45, 80, 67, 83, 71, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 49, 45, 80, 67, 83, 69, 48, 48,
                48, 45, 80, 67, 83, 70, 48, 48, 48, 45, 80, 67, 83, 67, 48, 48, 48, 45, 80, 67, 83, 68, 48,
                48, 48, 45, 80, 67, 83, 65, 48, 48, 48, 45, 80, 67, 83, 66, 48, 48, 48, 0, 1, 0, 1, 0, 1, 0,
                2, 239, 205, 171, 137, 103, 69, 35, 1,
            };
            Buffer.BlockCopy(tail, 0, d, d.Length - tail.Length, tail.Length);
            return d;
        }
    }
}
