using EmuLibrary.RomTypes.Xbox360.Disc;
using System.Collections.Generic;
using System.Security.Cryptography;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    public class XexImageTests
    {
        private static readonly byte[] RetailKey =
        {
            0x20, 0xB1, 0x85, 0xA5, 0x9D, 0x28, 0xFD, 0xC3,
            0x40, 0x58, 0x3F, 0xBB, 0x08, 0x96, 0xBF, 0x91,
        };

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD };

        [Fact]
        public void BuildImage_None_CopiesVerbatim()
        {
            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var file = new byte[100];
            System.Buffer.BlockCopy(payload, 0, file, 32, payload.Length);

            var image = XexImage.BuildImage(file, 32, 0 /* none */, new List<XexImage.BasicBlock>(),
                payload.Length, null);

            Assert.NotNull(image);
            Assert.Equal(payload, image);
        }

        [Fact]
        public void BuildImage_Basic_ExpandsBlocksWithZeroPadding()
        {
            // Two data runs separated/followed by zero_size fills.
            var run0 = new byte[16]; for (int i = 0; i < 16; i++) run0[i] = (byte)(i + 1);
            var run1 = new byte[16]; for (int i = 0; i < 16; i++) run1[i] = (byte)(0xA0 + i);

            var file = new byte[64];
            System.Buffer.BlockCopy(run0, 0, file, 0, 16);
            System.Buffer.BlockCopy(run1, 0, file, 16, 16);

            var blocks = new List<XexImage.BasicBlock>
            {
                new XexImage.BasicBlock { DataSize = 16, ZeroSize = 8 },
                new XexImage.BasicBlock { DataSize = 16, ZeroSize = 4 },
            };
            int imageSize = 16 + 8 + 16 + 4;

            var image = XexImage.BuildImage(file, 0, 1 /* basic */, blocks, imageSize, null);

            Assert.NotNull(image);
            Assert.Equal(imageSize, image.Length);
            for (int i = 0; i < 16; i++) Assert.Equal(run0[i], image[i]);
            for (int i = 16; i < 24; i++) Assert.Equal(0, image[i]);       // zero fill
            for (int i = 0; i < 16; i++) Assert.Equal(run1[i], image[24 + i]);
            for (int i = 40; i < 44; i++) Assert.Equal(0, image[i]);       // zero fill
        }

        [Fact]
        public void BuildImage_Basic_Encrypted_RoundTrips()
        {
            var sessionKey = new byte[16];
            for (int i = 0; i < 16; i++) sessionKey[i] = (byte)(i * 7 + 1);

            var payload = new byte[32];
            for (int i = 0; i < 32; i++) payload[i] = (byte)(i * 3);

            var encrypted = CbcEncrypt(sessionKey, payload);

            var blocks = new List<XexImage.BasicBlock> { new XexImage.BasicBlock { DataSize = 32, ZeroSize = 0 } };
            var image = XexImage.BuildImage(encrypted, 0, 1 /* basic */, blocks, 32, sessionKey);

            Assert.NotNull(image);
            Assert.Equal(payload, image);
        }

        [Fact]
        public void TryReadTitleImage_None_Unencrypted()
        {
            uint titleId = 0x5841085E;
            var xdbf = BuildXdbf(titleId, Png);
            var xex = BuildXex(titleId, xdbf, encrypt: false, compression: 1, out _);

            Assert.True(XexImage.TryReadTitleImage(xex, titleId, out var png));
            Assert.Equal(Png, png);
        }

        [Fact]
        public void TryReadTitleImage_Basic_Encrypted_RetailKey()
        {
            uint titleId = 0x415607E6;
            var xdbf = BuildXdbf(titleId, Png);
            var xex = BuildXex(titleId, xdbf, encrypt: true, compression: 1, out _);

            Assert.True(XexImage.TryReadTitleImage(xex, titleId, out var png));
            Assert.Equal(Png, png);
        }

        [Fact]
        public void TryReadTitleImage_RejectsLzx()
        {
            uint titleId = 0x11112222;
            var xdbf = BuildXdbf(titleId, Png);
            var xex = BuildXex(titleId, xdbf, encrypt: false, compression: 2 /* normal/LZX */, out _);

            Assert.False(XexImage.TryReadTitleImage(xex, titleId, out _));
        }

        // ---- synthetic XEX + XDBF builders ----

        private const int FileFormatOffset = 0x40;
        private const int ResourceOffset = 0x80;
        private const int SecurityOffset = 0x100;
        private const int ExeOffset = 0x400;
        private const uint LoadAddress = 0x82000000;

        // Builds a default.xex whose basefile is one basic block holding `image`, optionally AES-encrypted with a
        // session key wrapped by the retail key. When compression==2 the format is marked LZX (undecodable here).
        private static byte[] BuildXex(uint titleId, byte[] image, bool encrypt, ushort compression, out byte[] sessionKey)
        {
            // 16-align the payload so a single basic block is CBC-friendly.
            int padded = (image.Length + 15) & ~15;
            var payload = new byte[padded];
            System.Buffer.BlockCopy(image, 0, payload, 0, image.Length);

            sessionKey = new byte[16];
            for (int i = 0; i < 16; i++) sessionKey[i] = (byte)(i * 5 + 3);

            byte[] fileData = encrypt ? CbcEncrypt(sessionKey, payload) : payload;

            var xex = new byte[ExeOffset + padded];
            xex[0] = (byte)'X'; xex[1] = (byte)'E'; xex[2] = (byte)'X'; xex[3] = (byte)'2';
            WriteU32BE(xex, 0x08, ExeOffset);
            WriteU32BE(xex, 0x10, SecurityOffset);
            WriteU32BE(xex, 0x14, 2); // optional header count
            WriteU32BE(xex, 0x18, 0x000003FF); WriteU32BE(xex, 0x1C, FileFormatOffset);
            WriteU32BE(xex, 0x20, 0x000002FF); WriteU32BE(xex, 0x24, ResourceOffset);

            // File format info: one basic block { padded, 0 }.
            WriteU32BE(xex, FileFormatOffset + 0x00, 8 + 8); // info_size
            WriteU16BE(xex, FileFormatOffset + 0x04, (ushort)(encrypt ? 1 : 0));
            WriteU16BE(xex, FileFormatOffset + 0x06, compression);
            WriteU32BE(xex, FileFormatOffset + 0x08, (uint)padded); // block data_size
            WriteU32BE(xex, FileFormatOffset + 0x0C, 0);            // block zero_size

            // Resource info: one entry named after the title id at VA = LoadAddress (image offset 0).
            WriteU32BE(xex, ResourceOffset + 0x00, 4 + 16); // res_info_size
            var name = System.Text.Encoding.ASCII.GetBytes(titleId.ToString("X8"));
            System.Buffer.BlockCopy(name, 0, xex, ResourceOffset + 4, 8);
            WriteU32BE(xex, ResourceOffset + 4 + 8, LoadAddress);
            WriteU32BE(xex, ResourceOffset + 4 + 12, (uint)padded);

            // Security info: image_size, load_address, encrypted image key.
            WriteU32BE(xex, SecurityOffset + 0x04, (uint)padded); // image_size
            WriteU32BE(xex, SecurityOffset + 0x108 + 0x08, LoadAddress);
            var encryptedKey = EcbEncrypt(RetailKey, sessionKey);
            System.Buffer.BlockCopy(encryptedKey, 0, xex, SecurityOffset + 0x108 + 0x48, 16);

            System.Buffer.BlockCopy(fileData, 0, xex, ExeOffset, fileData.Length);
            return xex;
        }

        // Minimal XDBF with one (ns=2, id=0x8000) title-image entry.
        private static byte[] BuildXdbf(uint titleId, byte[] png)
        {
            var head = new List<byte>();
            head.AddRange(new byte[] { (byte)'X', (byte)'D', (byte)'B', (byte)'F' });
            AddU32BE(head, 0x10000); // version
            AddU32BE(head, 1);       // entry table length
            AddU32BE(head, 1);       // entry count
            AddU32BE(head, 0);       // free table length
            AddU32BE(head, 0);       // free count
            // entry: ns 2, id 0x8000, offset 0, length png.Length
            AddU16BE(head, 2);
            AddU64BE(head, 0x8000);
            AddU32BE(head, 0);
            AddU32BE(head, (uint)png.Length);
            head.AddRange(png);
            return head.ToArray();
        }

        private static byte[] CbcEncrypt(byte[] key, byte[] data)
        {
            using (var aes = new AesManaged { Mode = CipherMode.CBC, Padding = PaddingMode.None, KeySize = 128, Key = key, IV = new byte[16] })
            using (var enc = aes.CreateEncryptor())
            {
                var outBuf = new byte[data.Length];
                enc.TransformBlock(data, 0, data.Length, outBuf, 0);
                return outBuf;
            }
        }

        private static byte[] EcbEncrypt(byte[] key, byte[] block)
        {
            using (var aes = new AesManaged { Mode = CipherMode.ECB, Padding = PaddingMode.None, KeySize = 128, Key = key })
            using (var enc = aes.CreateEncryptor())
            {
                var outBuf = new byte[block.Length];
                enc.TransformBlock(block, 0, block.Length, outBuf, 0);
                return outBuf;
            }
        }

        private static void WriteU16BE(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
        private static void AddU16BE(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        private static void AddU32BE(List<byte> b, uint v)
        {
            b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
        }
        private static void AddU64BE(List<byte> b, ulong v) { AddU32BE(b, (uint)(v >> 32)); AddU32BE(b, (uint)v); }
    }
}
