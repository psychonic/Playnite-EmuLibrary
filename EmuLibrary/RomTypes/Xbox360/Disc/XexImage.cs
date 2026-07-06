using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace EmuLibrary.RomTypes.Xbox360.Disc
{
    // Recovers the embedded title image (an XDBF resource, a small PNG) out of a full default.xex. Unlike
    // XexHeader (which only reads the plaintext execution id), the image lives inside the PE basefile, which may
    // be AES-encrypted and/or compressed. Pipeline: parse the optional headers -> decrypt (retail or devkit key)
    // -> decompress -> map the XDBF resource VA into the image -> Xdbf.TryReadTitleImage.
    //
    // Supported here: uncompressed ("none") and "basic" (block list) formats, encrypted or not. "Normal"
    // (LZX) compression is recognized but not yet decoded (returns false) — that decoder lands separately.
    //
    // All multi-byte XEX fields are big-endian.
    internal static class XexImage
    {
        // Optional-header directory keys (see XexHeader for the directory walk). Low byte 0xFF => the value is a
        // file offset to a size-prefixed blob.
        private const uint FileFormatInfoKey = 0x000003FF;
        private const uint ResourceInfoKey = 0x000002FF;

        private const ushort EncryptionNone = 0;
        private const ushort CompressionNone = 0;
        private const ushort CompressionBasic = 1;
        private const ushort CompressionNormal = 2; // LZX — deferred

        // The public XEX2 retail key; devkit titles use an all-zero key. Neither is a secret.
        private static readonly byte[] RetailKey =
        {
            0x20, 0xB1, 0x85, 0xA5, 0x9D, 0x28, 0xFD, 0xC3,
            0x40, 0x58, 0x3F, 0xBB, 0x08, 0x96, 0xBF, 0x91,
        };
        private static readonly byte[] DevkitKey = new byte[16];

        internal struct BasicBlock
        {
            public uint DataSize;
            public uint ZeroSize;
        }

        private sealed class Resource
        {
            public string Name;
            public uint Address; // virtual address
            public uint Size;
        }

        // Reads the (namespace 2, id 0x8000) title image PNG out of a full default.xex for `titleId`. Returns
        // false (no throw) on any structural problem, an unsupported (LZX) format, a key mismatch, or no XDBF
        // title image. `titleId` picks the title's XDBF resource by name (falling back to trying every resource).
        public static bool TryReadTitleImage(byte[] xex, uint titleId, out byte[] png)
        {
            png = null;
            try
            {
                if (!TryParse(xex, out int exeOffset, out ushort encryption, out ushort compression,
                        out var blocks, out int windowBits, out uint firstBlockSize, out int imageSize,
                        out uint loadAddress, out var encryptedKey, out var resources))
                    return false;

                // Order resources so the one named after the title id is tried first; fall back to any resource
                // (the XDBF magic check below validates the pick, so a mis-named resource still can't false-hit).
                string titleName = titleId.ToString("X8");
                resources.Sort((a, b) =>
                    NameMatches(b.Name, titleName).CompareTo(NameMatches(a.Name, titleName)));

                var keys = encryption == EncryptionNone
                    ? new byte[][] { null }
                    : new byte[][] { RetailKey, DevkitKey };

                foreach (var wrapKey in keys)
                {
                    byte[] sessionKey = wrapKey == null ? null : DecryptImageKey(encryptedKey, wrapKey);
                    var image = compression == CompressionNormal
                        ? BuildImageNormal(xex, exeOffset, windowBits, firstBlockSize, imageSize, sessionKey)
                        : BuildImage(xex, exeOffset, compression, blocks, imageSize, sessionKey);
                    if (image == null)
                        continue;

                    foreach (var res in resources)
                    {
                        long off = (long)res.Address - loadAddress;
                        if (off < 0 || off >= image.Length)
                            continue;
                        long len = Math.Min(res.Size, image.Length - off);
                        if (len <= 0)
                            continue;

                        var slice = new byte[len];
                        Buffer.BlockCopy(image, (int)off, slice, 0, (int)len);
                        if (Xdbf.TryReadTitleImage(slice, out png))
                            return true;
                    }
                }
            }
            catch
            {
                png = null;
            }
            return false;
        }

        // Reassembles the PE basefile from the file bytes: decrypt (if a session key is given) and expand the
        // "basic" block list into the fixed-size image (data runs verbatim, zero_size runs left zero). Returns
        // null on any bounds/structure problem. Exposed internally for unit tests of the block expansion.
        internal static byte[] BuildImage(byte[] xex, int exeOffset, ushort compression,
            IReadOnlyList<BasicBlock> blocks, int imageSize, byte[] sessionKey)
        {
            if (imageSize <= 0 || imageSize > 128 * 1024 * 1024 || exeOffset < 0 || exeOffset > xex.Length)
                return null;

            var image = new byte[imageSize];

            using (var aes = sessionKey == null ? null : CreateCbc(sessionKey))
            using (var dec = aes?.CreateDecryptor())
            {
                if (compression == CompressionNone)
                {
                    int avail = Math.Min(imageSize, xex.Length - exeOffset);
                    if (dec != null)
                    {
                        int aligned = avail & ~15;
                        if (aligned > 0)
                            dec.TransformBlock(xex, exeOffset, aligned, image, 0);
                        // Any sub-block tail is padding; leave it zero.
                    }
                    else
                    {
                        Buffer.BlockCopy(xex, exeOffset, image, 0, avail);
                    }
                    return image;
                }

                if (compression == CompressionBasic)
                {
                    int inPos = exeOffset;
                    int outPos = 0;
                    foreach (var block in blocks)
                    {
                        int dataSize = (int)block.DataSize;
                        int zeroSize = (int)block.ZeroSize;
                        if (dataSize < 0 || zeroSize < 0)
                            return null;
                        if ((long)inPos + dataSize > xex.Length)
                            return null;
                        if ((long)outPos + dataSize + zeroSize > imageSize)
                            return null;

                        if (dataSize > 0)
                        {
                            if (dec != null)
                            {
                                if ((dataSize & 15) != 0)
                                    return null; // CBC needs 16-aligned runs
                                dec.TransformBlock(xex, inPos, dataSize, image, outPos);
                            }
                            else
                            {
                                Buffer.BlockCopy(xex, inPos, image, outPos, dataSize);
                            }
                        }
                        inPos += dataSize;
                        outPos += dataSize + zeroSize; // zero_size region stays zero
                    }
                    return image;
                }
            }

            return null; // unsupported compression
        }

        // Reassembles the PE basefile for "normal" (LZX) compression: decrypt the whole exe region (one CBC
        // stream, if encrypted), strip the XEX transport framing (per-block {u32 next_size, 20-byte hash}
        // headers and u16-length chunk records) into one continuous LZX stream, then LZX-decompress to imageSize.
        // Returns null on any structural/decode failure.
        internal static byte[] BuildImageNormal(byte[] xex, int exeOffset, int windowBits, uint firstBlockSize,
            int imageSize, byte[] sessionKey)
        {
            if (imageSize <= 0 || imageSize > 128 * 1024 * 1024 || exeOffset < 0 || exeOffset > xex.Length)
                return null;
            if (windowBits < 15 || windowBits > 21 || firstBlockSize < 24)
                return null;

            // Decrypt (or copy) the compressed region from exeOffset to end of file.
            int regionLen = xex.Length - exeOffset;
            if (regionLen <= 0)
                return null;
            var region = new byte[regionLen];
            if (sessionKey != null)
            {
                int aligned = regionLen & ~15;
                using (var aes = CreateCbc(sessionKey))
                using (var dec = aes.CreateDecryptor())
                    dec.TransformBlock(xex, exeOffset, aligned, region, 0);
                // any sub-16 tail can't be part of the (16-aligned) compressed stream; leave it zero
            }
            else
            {
                Buffer.BlockCopy(xex, exeOffset, region, 0, regionLen);
            }

            var lzxStream = DeframeNormal(region, firstBlockSize);
            if (lzxStream == null)
                return null;

            return Lzx.Decompress(lzxStream, windowBits, imageSize);
        }

        // Strips the XEX "normal" transport framing from the (decrypted) exe region into the raw LZX bitstream.
        // Blocks form a linked list: each is `firstBlockSize`/`nextBlockSize` bytes and begins with
        // { u32 next_block_size, u8 hash[20] }, followed by a body of u16-length-prefixed chunks (a zero length,
        // or the block boundary, ends the chunks; any trailing padding is skipped via the block size). A
        // next_block_size of 0 ends the list. Returns null on a malformed frame.
        private static byte[] DeframeNormal(byte[] region, uint firstBlockSize)
        {
            var outBuf = new List<byte>(region.Length);
            int p = 0;
            uint blockSize = firstBlockSize;

            while (blockSize != 0)
            {
                if (p + 24 > region.Length)
                    break;
                long blockEnd = Math.Min((long)p + blockSize, region.Length);

                uint nextBlockSize = ReadU32BE(region, p);
                p += 24; // next_block_size (4) + hash (20)

                while (p + 2 <= blockEnd)
                {
                    int chunkSize = (region[p] << 8) | region[p + 1];
                    p += 2;
                    if (chunkSize == 0)
                        break;
                    if ((long)p + chunkSize > blockEnd)
                        return outBuf.Count > 0 ? outBuf.ToArray() : null;
                    for (int i = 0; i < chunkSize; i++)
                        outBuf.Add(region[p + i]);
                    p += chunkSize;
                }

                p = (int)blockEnd; // skip any padding to the start of the next block
                blockSize = nextBlockSize;
            }

            return outBuf.Count > 0 ? outBuf.ToArray() : null;
        }

        // AES-128-ECB decrypt of the 16-byte encrypted image key to recover the CBC session key.
        private static byte[] DecryptImageKey(byte[] encryptedKey, byte[] wrapKey)
        {
            using (var aes = new AesManaged { Mode = CipherMode.ECB, Padding = PaddingMode.None, KeySize = 128, Key = wrapKey })
            using (var dec = aes.CreateDecryptor())
            {
                var outBuf = new byte[16];
                dec.TransformBlock(encryptedKey, 0, 16, outBuf, 0);
                return outBuf;
            }
        }

        // CBC over the whole image with a zero IV (chained across TransformBlock calls).
        private static AesManaged CreateCbc(byte[] key) =>
            new AesManaged { Mode = CipherMode.CBC, Padding = PaddingMode.None, KeySize = 128, Key = key, IV = new byte[16] };

        // Walks the XEX2 header for the fields we need. Returns false if the magic/tables are bad or the file
        // format / resource info headers are missing (both are always present in a bootable title).
        private static bool TryParse(byte[] xex, out int exeOffset, out ushort encryption, out ushort compression,
            out List<BasicBlock> blocks, out int windowBits, out uint firstBlockSize, out int imageSize,
            out uint loadAddress, out byte[] encryptedKey, out List<Resource> resources)
        {
            exeOffset = 0; encryption = 0; compression = 0; blocks = null; windowBits = 0; firstBlockSize = 0;
            imageSize = 0; loadAddress = 0; encryptedKey = null; resources = null;

            if (xex == null || xex.Length < 0x18)
                return false;
            if (!(xex[0] == 'X' && xex[1] == 'E' && xex[2] == 'X' && xex[3] == '2'))
                return false;

            exeOffset = (int)ReadU32BE(xex, 0x08);
            int securityOffset = (int)ReadU32BE(xex, 0x10);
            uint count = ReadU32BE(xex, 0x14);
            long tableEnd = 0x18L + (long)count * 8;
            if (count > 0x10000 || tableEnd > xex.Length)
                return false;

            uint fileFormatOffset = 0;
            uint resourceOffset = 0;
            for (int i = 0; i < count; i++)
            {
                int e = 0x18 + i * 8;
                uint key = ReadU32BE(xex, e);
                uint value = ReadU32BE(xex, e + 4);
                if (key == FileFormatInfoKey)
                    fileFormatOffset = value;
                else if (key == ResourceInfoKey)
                    resourceOffset = value;
            }
            if (fileFormatOffset == 0 || resourceOffset == 0)
                return false;

            // Security info: image size, load address, and the encrypted image key.
            if (securityOffset < 0 || securityOffset + 0x160 > xex.Length)
                return false;
            imageSize = (int)ReadU32BE(xex, securityOffset + 0x04);
            loadAddress = ReadU32BE(xex, securityOffset + 0x108 + 0x08);
            encryptedKey = new byte[16];
            Buffer.BlockCopy(xex, securityOffset + 0x108 + 0x48, encryptedKey, 0, 16);

            // File format info: encryption + compression, plus the basic block list when basic-compressed.
            if (fileFormatOffset + 8 > xex.Length)
                return false;
            uint ffInfoSize = ReadU32BE(xex, (int)fileFormatOffset);
            encryption = ReadU16BE(xex, (int)fileFormatOffset + 4);
            compression = ReadU16BE(xex, (int)fileFormatOffset + 6);

            blocks = new List<BasicBlock>();
            if (compression == CompressionBasic)
            {
                long blockData = (long)ffInfoSize - 8;
                if (ffInfoSize < 8 || fileFormatOffset + ffInfoSize > xex.Length || blockData % 8 != 0)
                    return false;
                int blockCount = (int)(blockData / 8);
                for (int i = 0; i < blockCount; i++)
                {
                    int b = (int)fileFormatOffset + 8 + i * 8;
                    blocks.Add(new BasicBlock
                    {
                        DataSize = ReadU32BE(xex, b),
                        ZeroSize = ReadU32BE(xex, b + 4),
                    });
                }
            }
            else if (compression == CompressionNormal)
            {
                // Normal (LZX) compression info: u32 window_size, then the first block descriptor
                // { u32 block_size, u8 hash[20] }. window_size is a power of two -> window bits.
                if (fileFormatOffset + 12 > xex.Length)
                    return false;
                uint windowSize = ReadU32BE(xex, (int)fileFormatOffset + 8);
                if (windowSize == 0 || (windowSize & (windowSize - 1)) != 0)
                    return false;
                windowBits = 0;
                while ((1u << windowBits) != windowSize)
                {
                    windowBits++;
                    if (windowBits > 31) return false;
                }
                if (windowBits < 15 || windowBits > 21)
                    return false;
                firstBlockSize = ReadU32BE(xex, (int)fileFormatOffset + 12);
            }

            // Resource info: a size-prefixed table of { name[8], VA, size } entries.
            if (resourceOffset + 4 > xex.Length)
                return false;
            uint resInfoSize = ReadU32BE(xex, (int)resourceOffset);
            long resTableBytes = (long)resInfoSize - 4;
            if (resInfoSize < 4 || resourceOffset + resInfoSize > xex.Length || resTableBytes % 16 != 0)
                return false;
            int resCount = (int)(resTableBytes / 16);
            resources = new List<Resource>(resCount);
            for (int i = 0; i < resCount; i++)
            {
                int r = (int)resourceOffset + 4 + i * 16;
                resources.Add(new Resource
                {
                    Name = ReadName(xex, r, 8),
                    Address = ReadU32BE(xex, r + 8),
                    Size = ReadU32BE(xex, r + 12),
                });
            }

            return true;
        }

        private static bool NameMatches(string resourceName, string titleName) =>
            string.Equals(resourceName, titleName, StringComparison.OrdinalIgnoreCase);

        private static string ReadName(byte[] b, int offset, int len)
        {
            int n = 0;
            while (n < len && b[offset + n] != 0)
                n++;
            var chars = new char[n];
            for (int i = 0; i < n; i++)
                chars[i] = (char)b[offset + i];
            return new string(chars);
        }

        private static ushort ReadU16BE(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        private static uint ReadU32BE(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    }
}
