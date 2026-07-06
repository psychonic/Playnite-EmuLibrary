using System;

namespace EmuLibrary.RomTypes.Xbox360.Disc
{
    // LZX ("normal" compression) decoder for Xbox 360 XEX basefiles.
    //
    // This is a faithful C# port of the LZX decompressor from libmspack (lzxd.c / readbits.h / readhuff.h),
    // (C) 2003-2023 Stuart Caie, distributed under the GNU LGPL v2.1. libmspack's LGPL terms permit use within
    // this GPLv3 project. The original C uses a streaming system-callback model; this port reads from a single
    // in-memory input buffer and writes to a single output buffer, which is all the XEX use case needs:
    //   - reset_interval = 0 (the XEX bitstream never periodically resets, like CAB LZX)
    //   - is_delta = 0 (regular LZX, not LZX DELTA)
    //   - intel_filesize = 0 (the E8 call transform is disabled — the 360 is PowerPC, not x86)
    // The E8 branch is therefore never taken, so each output frame is copied straight from the window.
    //
    // The caller (XexImage) de-frames the XEX transport (per-block {u32,20-byte hash} headers and u16 chunk
    // lengths) into one continuous compressed buffer before calling this; libmspack's per-32KB-frame input
    // realignment consumes the 16-bit padding at each XEX chunk boundary.
    internal sealed class Lzx
    {
        private const int MinMatch = 2;
        private const int MaxMatch = 257;
        private const int NumChars = 256;
        private const int BlockTypeVerbatim = 1;
        private const int BlockTypeAligned = 2;
        private const int BlockTypeUncompressed = 3;
        private const int NumPrimaryLengths = 7;
        private const int NumSecondaryLengths = 249;
        private const int FrameSize = 32768;

        private const int PretreeMaxSymbols = 20;
        private const int PretreeTableBits = 6;
        private const int MaintreeMaxSymbols = NumChars + 290 * 8; // 2576
        private const int MaintreeTableBits = 12;
        private const int LengthMaxSymbols = NumSecondaryLengths + 1; // 250
        private const int LengthTableBits = 12;
        private const int AlignedMaxSymbols = 8;
        private const int AlignedTableBits = 7;
        private const int LenTableSafety = 64;

        private static readonly uint[] PositionSlots = { 30, 32, 34, 36, 38, 42, 50, 66, 98, 162, 290 };
        private static readonly byte[] ExtraBits =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
            9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16
        };
        private static readonly uint[] PositionBase = BuildPositionBase();

        private sealed class LzxException : Exception { }

        // input bitstream (MSB-first, 16-bit little-endian words), 32-bit bit buffer
        private readonly byte[] _input;
        private int _iPtr;
        private uint _bitBuffer;
        private int _bitsLeft;

        // output
        private readonly byte[] _output;
        private long _offset; // bytes written to output

        // decode window
        private readonly byte[] _window;
        private readonly uint _windowSize;
        private uint _windowPosn;
        private uint _framePosn;
        private uint _frame;
        private uint _numOffsets;

        private uint _r0 = 1, _r1 = 1, _r2 = 1;
        private uint _blockLength;
        private uint _blockRemaining;
        private int _blockType = LZX_BLOCKTYPE_INVALID;
        private bool _headerRead;
        private bool _lengthEmpty;

        private const int LZX_BLOCKTYPE_INVALID = 0;

        private readonly byte[] _pretreeLen = new byte[PretreeMaxSymbols + LenTableSafety];
        private readonly byte[] _maintreeLen = new byte[MaintreeMaxSymbols + LenTableSafety];
        private readonly byte[] _lengthLen = new byte[LengthMaxSymbols + LenTableSafety];
        private readonly byte[] _alignedLen = new byte[AlignedMaxSymbols + LenTableSafety];

        private readonly ushort[] _pretreeTable = new ushort[(1 << PretreeTableBits) + PretreeMaxSymbols * 2];
        private readonly ushort[] _maintreeTable = new ushort[(1 << MaintreeTableBits) + MaintreeMaxSymbols * 2];
        private readonly ushort[] _lengthTable = new ushort[(1 << LengthTableBits) + LengthMaxSymbols * 2];
        private readonly ushort[] _alignedTable = new ushort[(1 << AlignedTableBits) + AlignedMaxSymbols * 2];

        private Lzx(byte[] input, int windowBits, int outputLength)
        {
            _input = input;
            _windowSize = 1u << windowBits;
            _window = new byte[_windowSize];
            _output = new byte[outputLength];
            _numOffsets = PositionSlots[windowBits - 15] << 3;
        }

        // Decompresses a de-framed LZX stream to exactly outputLength bytes. windowBits must be 15..21. Returns
        // the decompressed bytes, or null on any decode error (fail-soft).
        public static byte[] Decompress(byte[] input, int windowBits, int outputLength)
        {
            if (input == null || outputLength <= 0 || windowBits < 15 || windowBits > 21)
                return null;
            try
            {
                var lzx = new Lzx(input, windowBits, outputLength);
                lzx.Run(outputLength);
                return lzx._output;
            }
            catch (LzxException)
            {
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        private static uint[] BuildPositionBase()
        {
            // position_base[0]=0; position_base[i] = position_base[i-1] + (1 << extra_bits[i-1]).
            // extra_bits[i] = 17 for i >= 36. 290 entries cover the LZX DELTA maximum; regular LZX uses far fewer.
            var pb = new uint[290];
            uint acc = 0;
            for (int i = 0; i < 290; i++)
            {
                pb[i] = acc;
                int eb = i < 36 ? ExtraBits[i] : 17;
                acc += 1u << eb;
            }
            return pb;
        }

        // ---- bit reader ----

        private byte NextByte()
        {
            // Past the end of input, libmspack fakes zero bytes; do the same (still advancing the pointer).
            if (_iPtr < _input.Length) return _input[_iPtr++];
            _iPtr++;
            return 0;
        }

        private void EnsureBits(int nbits)
        {
            while (_bitsLeft < nbits)
            {
                byte b0 = NextByte();
                byte b1 = NextByte();
                uint word = (uint)((b1 << 8) | b0);
                _bitBuffer |= word << (16 - _bitsLeft);
                _bitsLeft += 16;
            }
        }

        private uint PeekBits(int nbits) => nbits == 0 ? 0u : _bitBuffer >> (32 - nbits);

        private void RemoveBits(int nbits)
        {
            _bitBuffer <<= nbits;
            _bitsLeft -= nbits;
        }

        private uint ReadBits(int nbits)
        {
            EnsureBits(nbits);
            uint v = PeekBits(nbits);
            RemoveBits(nbits);
            return v;
        }

        // ---- huffman ----

        private int ReadHuffSym(ushort[] table, byte[] lens, int maxSymbols, int tableBits)
        {
            EnsureBits(16);
            int sym = table[PeekBits(tableBits)];
            if (sym >= maxSymbols)
            {
                int huffIdx = 1 << (32 - tableBits);
                do
                {
                    huffIdx >>= 1;
                    if (huffIdx == 0)
                        throw new LzxException();
                    sym = table[(sym << 1) | (((_bitBuffer & (uint)huffIdx) != 0) ? 1 : 0)];
                } while (sym >= maxSymbols);
            }
            RemoveBits(lens[sym]);
            return sym;
        }

        // Builds a fast huffman decode table from canonical code lengths (make_decode_table, MSB order).
        private static bool MakeDecodeTable(uint nsyms, uint nbits, byte[] length, ushort[] table)
        {
            uint pos = 0;
            uint tableMask = 1u << (int)nbits;
            uint bitMask = tableMask >> 1;

            for (uint bitNum = 1; bitNum <= nbits; bitNum++)
            {
                for (uint sym = 0; sym < nsyms; sym++)
                {
                    if (length[sym] != bitNum) continue;
                    uint leaf = pos;
                    if ((pos += bitMask) > tableMask) return false; // table overrun
                    for (uint fill = bitMask; fill-- > 0;) table[leaf++] = (ushort)sym;
                }
                bitMask >>= 1;
            }

            if (pos == tableMask) return true;

            for (uint sym = pos; sym < tableMask; sym++) table[sym] = 0xFFFF;

            uint nextSymbol = ((tableMask >> 1) < nsyms) ? nsyms : (tableMask >> 1);
            pos <<= 16;
            tableMask <<= 16;
            bitMask = 1u << 15;

            for (uint bitNum = nbits + 1; bitNum <= 16; bitNum++)
            {
                for (uint sym = 0; sym < nsyms; sym++)
                {
                    if (length[sym] != bitNum) continue;
                    if (pos >= tableMask) return false; // table overflow
                    uint leaf = pos >> 16;
                    for (uint fill = 0; fill < (bitNum - nbits); fill++)
                    {
                        if (table[leaf] == 0xFFFF)
                        {
                            table[nextSymbol << 1] = 0xFFFF;
                            table[(nextSymbol << 1) + 1] = 0xFFFF;
                            table[leaf] = (ushort)nextSymbol++;
                        }
                        leaf = (uint)(table[leaf] << 1);
                        if (((pos >> (int)(15 - fill)) & 1) != 0) leaf++;
                    }
                    table[leaf] = (ushort)sym;
                    pos += bitMask;
                }
                bitMask >>= 1;
            }

            return pos == tableMask;
        }

        // Reads code lengths for symbols [first, last) into lens using the pretree-coded RLE scheme.
        private void ReadLengths(byte[] lens, uint first, uint last)
        {
            for (int x = 0; x < 20; x++)
                _pretreeLen[x] = (byte)ReadBits(4);
            if (!MakeDecodeTable(PretreeMaxSymbols, PretreeTableBits, _pretreeLen, _pretreeTable))
                throw new LzxException();

            for (uint x = first; x < last;)
            {
                int z = ReadHuffSym(_pretreeTable, _pretreeLen, PretreeMaxSymbols, PretreeTableBits);
                if (z == 17)
                {
                    uint y = ReadBits(4) + 4;
                    while (y-- > 0) lens[x++] = 0;
                }
                else if (z == 18)
                {
                    uint y = ReadBits(5) + 20;
                    while (y-- > 0) lens[x++] = 0;
                }
                else if (z == 19)
                {
                    uint y = ReadBits(1) + 4;
                    z = ReadHuffSym(_pretreeTable, _pretreeLen, PretreeMaxSymbols, PretreeTableBits);
                    z = lens[x] - z; if (z < 0) z += 17;
                    while (y-- > 0) lens[x++] = (byte)z;
                }
                else
                {
                    z = lens[x] - z; if (z < 0) z += 17;
                    lens[x++] = (byte)z;
                }
            }
        }

        private void BuildTableMaybeEmpty()
        {
            _lengthEmpty = false;
            if (!MakeDecodeTable(LengthMaxSymbols, LengthTableBits, _lengthLen, _lengthTable))
            {
                for (int i = 0; i < LengthMaxSymbols; i++)
                    if (_lengthLen[i] > 0)
                        throw new LzxException();
                _lengthEmpty = true; // empty tree is allowed (no matches use it)
            }
        }

        // ---- main decode ----

        private void Run(int outBytes)
        {
            byte[] window = _window;
            uint windowPosn = _windowPosn;
            uint r0 = _r0, r1 = _r1, r2 = _r2;
            byte[] buf = new byte[12];

            uint endFrame = (uint)((_offset + outBytes) / FrameSize) + 1;

            while (_frame < endFrame)
            {
                // reset_interval is 0 for XEX (never resets); is_delta is 0. Both handled by omission.

                if (!_headerRead)
                {
                    // The E8 (Intel call) translation header: 1 bit, then a 32-bit filesize when set. XEX streams
                    // set it to 0 (PowerPC), so we consume the bits but the E8 transform is never applied.
                    if (ReadBits(1) != 0) { ReadBits(16); ReadBits(16); }
                    _headerRead = true;
                }

                uint frameSize = FrameSize;
                if (_output.Length != 0 && (_output.Length - _offset) < frameSize)
                    frameSize = (uint)(_output.Length - _offset);

                int bytesTodo = (int)(_framePosn + frameSize - windowPosn);
                while (bytesTodo > 0)
                {
                    if (_blockRemaining == 0)
                    {
                        // realign if previous block was an odd-sized uncompressed block
                        if (_blockType == BlockTypeUncompressed && (_blockLength & 1) != 0)
                            _iPtr++;

                        _blockType = (int)ReadBits(3);
                        uint hi = ReadBits(16);
                        uint lo = ReadBits(8);
                        _blockRemaining = _blockLength = (hi << 8) | lo;

                        if (_blockType == BlockTypeAligned)
                        {
                            for (int i = 0; i < 8; i++)
                                _alignedLen[i] = (byte)ReadBits(3);
                            if (!MakeDecodeTable(AlignedMaxSymbols, AlignedTableBits, _alignedLen, _alignedTable))
                                throw new LzxException();
                        }

                        if (_blockType == BlockTypeAligned || _blockType == BlockTypeVerbatim)
                        {
                            ReadLengths(_maintreeLen, 0, 256);
                            ReadLengths(_maintreeLen, 256, 256 + _numOffsets);
                            if (!MakeDecodeTable(MaintreeMaxSymbols, MaintreeTableBits, _maintreeLen, _maintreeTable))
                                throw new LzxException();
                            ReadLengths(_lengthLen, 0, NumSecondaryLengths);
                            BuildTableMaybeEmpty();
                        }
                        else if (_blockType == BlockTypeUncompressed)
                        {
                            if (_bitsLeft == 0) EnsureBits(16);
                            _bitsLeft = 0; _bitBuffer = 0; // align to a byte boundary
                            for (int i = 0; i < 12; i++) buf[i] = NextByte();
                            r0 = Le32(buf, 0); r1 = Le32(buf, 4); r2 = Le32(buf, 8);
                        }
                        else
                        {
                            throw new LzxException();
                        }
                    }

                    int thisRun = (int)_blockRemaining;
                    if (thisRun > bytesTodo) thisRun = bytesTodo;
                    bytesTodo -= thisRun;
                    _blockRemaining -= (uint)thisRun;

                    if (_blockType == BlockTypeAligned || _blockType == BlockTypeVerbatim)
                    {
                        while (thisRun > 0)
                        {
                            int mainElement = ReadHuffSym(_maintreeTable, _maintreeLen, MaintreeMaxSymbols, MaintreeTableBits);
                            if (mainElement < NumChars)
                            {
                                window[windowPosn++] = (byte)mainElement;
                                thisRun--;
                                continue;
                            }

                            mainElement -= NumChars;
                            int matchLength = mainElement & NumPrimaryLengths;
                            if (matchLength == NumPrimaryLengths)
                            {
                                if (_lengthEmpty) throw new LzxException();
                                int lengthFooter = ReadHuffSym(_lengthTable, _lengthLen, LengthMaxSymbols, LengthTableBits);
                                matchLength += lengthFooter;
                            }
                            matchLength += MinMatch;

                            uint matchOffset = (uint)(mainElement >> 3);
                            switch (matchOffset)
                            {
                                case 0: matchOffset = r0; break;
                                case 1: matchOffset = r1; r1 = r0; r0 = matchOffset; break;
                                case 2: matchOffset = r2; r2 = r0; r0 = matchOffset; break;
                                default:
                                    int extra = (matchOffset >= 36) ? 17 : ExtraBits[matchOffset];
                                    matchOffset = PositionBase[matchOffset] - 2;
                                    if (extra >= 3 && _blockType == BlockTypeAligned)
                                    {
                                        if (extra > 3)
                                        {
                                            uint verbatimBits = ReadBits(extra - 3);
                                            matchOffset += verbatimBits << 3;
                                        }
                                        int alignedBits = ReadHuffSym(_alignedTable, _alignedLen, AlignedMaxSymbols, AlignedTableBits);
                                        matchOffset += (uint)alignedBits;
                                    }
                                    else if (extra != 0)
                                    {
                                        uint verbatimBits = ReadBits(extra);
                                        matchOffset += verbatimBits;
                                    }
                                    r2 = r1; r1 = r0; r0 = matchOffset;
                                    break;
                            }

                            if ((windowPosn + matchLength) > _windowSize)
                                throw new LzxException();

                            int rundest = (int)windowPosn;
                            int i = matchLength;
                            if (matchOffset > windowPosn)
                            {
                                if ((long)matchOffset > _offset && (matchOffset - windowPosn) > 0)
                                    throw new LzxException(); // match beyond available data (no ref data)
                                int j = (int)(matchOffset - windowPosn);
                                if (j > _windowSize) throw new LzxException();
                                int runsrc = (int)(_windowSize - j);
                                if (j < i)
                                {
                                    i -= j;
                                    while (j-- > 0) window[rundest++] = window[runsrc++];
                                    runsrc = 0;
                                }
                                while (i-- > 0) window[rundest++] = window[runsrc++];
                            }
                            else
                            {
                                int runsrc = rundest - (int)matchOffset;
                                while (i-- > 0) window[rundest++] = window[runsrc++];
                            }

                            thisRun -= matchLength;
                            windowPosn += (uint)matchLength;
                        }
                    }
                    else // uncompressed
                    {
                        int rundest = (int)windowPosn;
                        windowPosn += (uint)thisRun;
                        while (thisRun-- > 0)
                            window[rundest++] = NextByte();
                        thisRun = 0;
                    }

                    if (thisRun < 0)
                    {
                        if ((uint)(-thisRun) > _blockRemaining) throw new LzxException();
                        _blockRemaining -= (uint)(-thisRun);
                    }
                }

                if ((windowPosn - _framePosn) != frameSize)
                    throw new LzxException();

                // re-align input bitstream to a 16-bit boundary at each frame end
                if (_bitsLeft > 0) EnsureBits(16);
                if ((_bitsLeft & 15) != 0) RemoveBits(_bitsLeft & 15);

                // E8 transform is disabled (intel_filesize == 0), so the frame output is simply the window slice.
                int write = (outBytes < (int)frameSize) ? outBytes : (int)frameSize;
                Array.Copy(window, (int)_framePosn, _output, (int)_offset, write);
                _offset += write;
                outBytes -= write;

                _framePosn += frameSize;
                _frame++;

                if (windowPosn == _windowSize) windowPosn = 0;
                if (_framePosn == _windowSize) _framePosn = 0;
            }

            if (outBytes != 0)
                throw new LzxException();

            _windowPosn = windowPosn;
            _r0 = r0; _r1 = r1; _r2 = r2;
        }

        private static uint Le32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
