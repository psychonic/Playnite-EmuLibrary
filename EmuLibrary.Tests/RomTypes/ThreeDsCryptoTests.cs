using EmuLibrary.RomTypes.ThreeDs;
using EmuLibrary.RomTypes.ThreeDs.Crypto;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Xunit;
using ZstdSharp;

namespace EmuLibrary.Tests.RomTypes
{
    // Deterministic tests for the 3DS crypto building blocks. The full NCCH decrypt can only be validated
    // against a real encrypted title (keys + sample), so it is exercised manually; here we pin the pure,
    // self-contained pieces: the 128-bit key-scrambler arithmetic, the signature-aware tmd parse, the ticket
    // titlekey CBC decrypt, and the CIA section/offset math + content CBC strip.
    public class ThreeDsCryptoTests
    {
        private const uint SigTypeRsa2048Sha256 = 0x00010004;
        private const int Rsa2048SigBlock = 4 + 0x100 + 0x3C; // 0x140

        [Fact]
        public void Rol128_ByOneByte_RotatesBytes()
        {
            var v = new byte[16];
            for (int i = 0; i < 16; i++)
                v[i] = (byte)(i + 1); // 01 02 ... 10

            var r = ThreeDsKeyScrambler.Rol128(v, 8);

            var expected = new byte[16];
            for (int i = 0; i < 16; i++)
                expected[i] = (byte)(((i + 1) % 16) + 1); // 02 03 ... 10 01
            Assert.Equal(expected, r);
        }

        [Fact]
        public void Rol128_ByOneBit_ShiftsWithWraparound()
        {
            var v = new byte[16];
            v[0] = 0x80; // high bit set -> should wrap to the lowest bit

            var r = ThreeDsKeyScrambler.Rol128(v, 1);

            var expected = new byte[16];
            expected[15] = 0x01;
            Assert.Equal(expected, r);
        }

        [Fact]
        public void Add128_PropagatesCarryAcrossBytes()
        {
            var a = new byte[16];
            a[15] = 0xFF;
            a[14] = 0x01;
            var b = new byte[16];
            b[15] = 0x01;

            ThreeDsKeyScrambler.Add128InPlace(a, b); // 0x01FF + 0x01 = 0x0200

            Assert.Equal(0x00, a[15]);
            Assert.Equal(0x02, a[14]);
        }

        [Fact]
        public void AesCtr_IsSymmetricAndCounterAdvances()
        {
            var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            var data = Enumerable.Range(0, 64).Select(i => (byte)(i * 7)).ToArray();
            var original = (byte[])data.Clone();

            using (var ctr = new AesCtr(key))
            {
                var counter = new byte[16];
                ctr.Apply(counter, data, 0, data.Length);
                Assert.NotEqual(original, data);

                // A fresh counter (re-seeded) re-applies the same keystream, recovering the plaintext.
                var counter2 = new byte[16];
                ctr.Apply(counter2, data, 0, data.Length);
                Assert.Equal(original, data);
            }
        }

        [Fact]
        public void AesCtr_ChunkedApplication_MatchesSinglePassAcrossBatchBoundary()
        {
            // Larger than one keystream batch (4096 blocks = 64 KiB) so the batching path is crossed more than
            // once, and not a multiple of it, so the final partial batch is exercised too.
            var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
            var data = new byte[200000];
            new Random(99).NextBytes(data);

            // A single whole-buffer pass.
            var reference = (byte[])data.Clone();
            using (var ctr = new AesCtr(key))
                ctr.Apply(new byte[16], reference, 0, reference.Length);
            Assert.NotEqual(data, reference);

            // The same region applied in 16-aligned chunks (all but the last), continuing the one running counter,
            // must produce the identical keystream — this pins the counter-advance post-condition NcchDecryptor
            // relies on when it decrypts a multi-GB region 1 MiB at a time.
            var chunked = (byte[])data.Clone();
            using (var ctr = new AesCtr(key))
            {
                var counter = new byte[16];
                int pos = 0;
                foreach (var len in new[] { 16, 4096 * 16, 32, 100000, chunked.Length - (16 + 4096 * 16 + 32 + 100000) })
                {
                    ctr.Apply(counter, chunked, pos, len);
                    pos += len;
                }
                Assert.Equal(chunked.Length, pos);
            }
            Assert.Equal(reference, chunked);
        }

        [Fact]
        public void TmdParse_ReadsTitleKindContentsAndSizes()
        {
            // Update title id 0x0004000E00123400 -> base 0x0004000000123400.
            ulong titleId = 0x0004000E00123400UL;
            var tmd = new byte[Rsa2048SigBlock + 0x9C4 + 2 * 0x30];
            WriteBe32(tmd, 0, SigTypeRsa2048Sha256);
            WriteBe64(tmd, Rsa2048SigBlock + 0x4C, titleId);
            WriteBe16(tmd, Rsa2048SigBlock + 0x9C, 0x1234);   // title version
            WriteBe16(tmd, Rsa2048SigBlock + 0x9E, 2);        // content count

            int chunks = Rsa2048SigBlock + 0x9C4;
            // content 0: id 0, index 0, type encrypted (0x0001), size 0x1000
            WriteBe32(tmd, chunks + 0x00, 0x00000000);
            WriteBe16(tmd, chunks + 0x04, 0);
            WriteBe16(tmd, chunks + 0x06, 0x0001);
            WriteBe64(tmd, chunks + 0x08, 0x1000);
            // content 1: id 1, index 1, type optional (0x4000), size 0x2000
            WriteBe32(tmd, chunks + 0x30 + 0x00, 0x00000001);
            WriteBe16(tmd, chunks + 0x30 + 0x04, 1);
            WriteBe16(tmd, chunks + 0x30 + 0x06, 0x4000);
            WriteBe64(tmd, chunks + 0x30 + 0x08, 0x2000);

            var parsed = ThreeDsTitleMetadata.Parse(tmd);

            Assert.Equal(titleId, parsed.TitleId);
            Assert.Equal(0x0004000000123400UL, parsed.BaseTitleId);
            Assert.Equal(ThreeDsContentKind.Update, parsed.Kind);
            Assert.Equal((ushort)0x1234, parsed.TitleVersion);
            Assert.Equal(2, parsed.Contents.Count);
            Assert.True(parsed.Contents[1].IsOptional);
            Assert.True(parsed.Contents[0].IsEncrypted);
            Assert.Equal(0x3000UL, parsed.TotalContentSize);
        }

        [Fact]
        public void TicketDecrypt_RecoversTitleKeyFromKnownCommonKey()
        {
            ulong titleId = 0x0004000000123400UL;
            var commonKey = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

            // Encrypt the titlekey the way a real ticket stores it: AES-128-CBC, IV = titleId(8 BE) ‖ 0.
            var iv = new byte[16];
            WriteBe64(iv, 0, titleId);
            byte[] encTitleKey;
            using (var aes = Aes.Create())
            {
                aes.Key = commonKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor())
                {
                    encTitleKey = new byte[16];
                    enc.TransformBlock(titleKey, 0, 16, encTitleKey, 0);
                }
            }

            var ticket = BuildTicket(titleId, ticketId: 0x0123456789ABCDEFUL, commonKeyIndex: 0, encTitleKey: encTitleKey);
            var parsed = ThreeDsTicket.Parse(ticket);

            Assert.Equal(titleId, parsed.TitleId);
            Assert.Equal(0x0123456789ABCDEFUL, parsed.TicketId);
            Assert.Equal(0, parsed.CommonKeyIndex);
            Assert.Equal(titleKey, parsed.DecryptTitleKey(commonKey));
        }

        [Fact]
        public void CiaParse_LocatesSectionsAndStripsContentTitleKey()
        {
            ulong titleId = 0x0004000000123400UL;
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();

            var plaintext = Enumerable.Range(0, 0x40).Select(i => (byte)(i * 3 + 1)).ToArray();
            byte[] encContent;
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = new byte[16]; // content index 0 -> zero IV
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor())
                {
                    encContent = new byte[plaintext.Length];
                    enc.TransformBlock(plaintext, 0, plaintext.Length, encContent, 0);
                }
            }

            var ticket = BuildTicket(titleId, 0x1122334455667788UL, 0, new byte[16]);
            var tmd = BuildTmd(titleId, contentId: 0x12345678, contentIndex: 0, contentType: 0x0001, contentSize: (ulong)plaintext.Length);
            var ciaBytes = BuildCia(ticket, tmd, encContent, presentIndex: 0);

            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, ciaBytes);
                using (var cia = Cia.Open(path))
                {
                    Assert.Equal(titleId, cia.Tmd.TitleId);
                    Assert.Equal(titleId, cia.Ticket.TitleId);

                    var content = cia.PresentContents.Single();
                    using (var ms = new MemoryStream())
                    {
                        cia.DecryptContentTo(content, titleKey, ms, CancellationToken.None);
                        Assert.Equal(plaintext, ms.ToArray());
                    }
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CdnFolder_FindsNewestTmdAndStripsContentTitleKey()
        {
            ulong titleId = 0x0004000000123400UL;
            uint contentId = 0x000000FE;
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0xD0 + i)).ToArray();

            var plaintext = Enumerable.Range(0, 0x40).Select(i => (byte)(i * 5 + 2)).ToArray();
            byte[] encContent;
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = new byte[16]; // content index 0 -> zero IV
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor())
                {
                    encContent = new byte[plaintext.Length];
                    enc.TransformBlock(plaintext, 0, plaintext.Length, encContent, 0);
                }
            }

            var dir = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var tmd = BuildTmd(titleId, contentId, contentIndex: 0, contentType: 0x0001, contentSize: (ulong)plaintext.Length);
                // An older and a newer tmd live side by side; FindTmd must pick "tmd.9" over "tmd.8".
                File.WriteAllBytes(Path.Combine(dir, "tmd.8"), BuildTmd(titleId, 0xDEAD, 0, 0x0001, 0x10));
                File.WriteAllBytes(Path.Combine(dir, "tmd.9"), tmd);
                File.WriteAllBytes(Path.Combine(dir, contentId.ToString("x8")), encContent);

                var ticketPath = Path.Combine(dir, "title.tik");
                File.WriteAllBytes(ticketPath, BuildTicket(titleId, 0x2233445566778899UL, 0, new byte[16]));

                Assert.Equal(Path.Combine(dir, "tmd.9"), CdnTitle.FindTmd(dir));
                Assert.True(CdnTitle.IsComplete(dir, ThreeDsTitleMetadata.Parse(tmd)));

                using (var cdn = CdnTitle.Open(dir, ticketPath))
                {
                    Assert.Equal(titleId, cdn.Tmd.TitleId);
                    Assert.Equal(titleId, cdn.Ticket.TitleId);

                    var content = cdn.PresentContents.Single();
                    Assert.Equal(contentId, content.Id);
                    using (var ms = new MemoryStream())
                    {
                        cdn.DecryptContentTo(content, titleKey, ms, CancellationToken.None);
                        Assert.Equal(plaintext, ms.ToArray());
                    }
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CdnFolder_IsCompleteIsFalseWhenContentMissingOrWrongSize()
        {
            ulong titleId = 0x0004000000123400UL;
            uint contentId = 0x000000AB;
            var tmd = ThreeDsTitleMetadata.Parse(
                BuildTmd(titleId, contentId, 0, 0x0001, contentSize: 0x40));

            var dir = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // No content file at all -> incomplete.
                Assert.False(CdnTitle.IsComplete(dir, tmd));

                // A truncated content file (e.g. an interrupted download) is the wrong size -> still incomplete.
                File.WriteAllBytes(Path.Combine(dir, contentId.ToString("x8")), new byte[0x20]);
                Assert.False(CdnTitle.IsComplete(dir, tmd));

                // Exact size -> complete.
                File.WriteAllBytes(Path.Combine(dir, contentId.ToString("x8")), new byte[0x40]);
                Assert.True(CdnTitle.IsComplete(dir, tmd));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void NcsdParse_ReadsPresentPartitions()
        {
            // Two partitions: index 0 at media offset 1 (size 4), index 1 at media offset 5 (size 2); media
            // unit = 0x200 (flags[6] = 0). Indices 2..7 are absent (size 0).
            var ncsd = new byte[0x200];
            ncsd[0x100] = (byte)'N'; ncsd[0x101] = (byte)'C'; ncsd[0x102] = (byte)'S'; ncsd[0x103] = (byte)'D';
            WriteLe32(ncsd, 0x120 + 0 * 8, 1); WriteLe32(ncsd, 0x120 + 0 * 8 + 4, 4);
            WriteLe32(ncsd, 0x120 + 1 * 8, 5); WriteLe32(ncsd, 0x120 + 1 * 8 + 4, 2);

            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, ncsd);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    Assert.True(Ncsd.IsNcsd(fs));
                    var parts = Ncsd.ReadPartitions(fs);
                    Assert.Equal(2, parts.Count);
                    Assert.Equal(0x200L, parts[0].Offset);
                    Assert.Equal(0x800L, parts[0].Size);
                    Assert.Equal(0xA00L, parts[1].Offset);
                    Assert.Equal(0x400L, parts[1].Size);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Z3ds_DecompressesPayloadAcrossMetadataAndSkippableFrame()
        {
            // A payload large enough to actually compress, decoded across a non-zero metadata block and past a
            // trailing skippable seek-table frame (which DecompressionStream must skip without emitting bytes).
            var original = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 + 7)).ToArray();
            var z3ds = BuildZ3ds(original, metadataSize: 0x10);

            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, z3ds);
                Assert.True(Z3ds.IsZ3ds(path));
                using (var ms = new MemoryStream())
                {
                    Z3ds.DecompressTo(path, ms, CancellationToken.None);
                    Assert.Equal(original, ms.ToArray());
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Z3ds_ZciaOpensThroughCiaTransparently()
        {
            ulong titleId = 0x0004000000123400UL;
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0xE0 + i)).ToArray();

            var plaintext = Enumerable.Range(0, 0x40).Select(i => (byte)(i * 7 + 3)).ToArray();
            byte[] encContent;
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = new byte[16]; // content index 0 -> zero IV
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor())
                {
                    encContent = new byte[plaintext.Length];
                    enc.TransformBlock(plaintext, 0, plaintext.Length, encContent, 0);
                }
            }

            var ticket = BuildTicket(titleId, 0x9988776655443322UL, 0, new byte[16]);
            var tmd = BuildTmd(titleId, contentId: 0x0000ABCD, contentIndex: 0, contentType: 0x0001, contentSize: (ulong)plaintext.Length);
            var ciaBytes = BuildCia(ticket, tmd, encContent, presentIndex: 0);

            // Wrap the CIA in Z3DS; Cia.Open must detect the .zcia by magic, decompress, and parse it like a .cia.
            var z3ds = BuildZ3ds(ciaBytes, metadataSize: 0x20);
            var path = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N") + ".zcia");
            try
            {
                File.WriteAllBytes(path, z3ds);
                using (var cia = Cia.Open(path))
                {
                    Assert.Equal(titleId, cia.Tmd.TitleId);
                    Assert.Equal(titleId, cia.Ticket.TitleId);

                    var content = cia.PresentContents.Single();
                    using (var ms = new MemoryStream())
                    {
                        cia.DecryptContentTo(content, titleKey, ms, CancellationToken.None);
                        Assert.Equal(plaintext, ms.ToArray());
                    }
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Z3ds_PrefixParse_ReadsTmdWithoutDecompressingContent()
        {
            ulong titleId = 0x0004000000123400UL;

            // A large, incompressible content so the full payload dwarfs the header+ticket+tmd prefix and a
            // prefix decode is provably reading only a sliver of it.
            var content = new byte[4 * 1024 * 1024];
            new Random(1234).NextBytes(content);

            var ticket = BuildTicket(titleId, 0x9988776655443322UL, 0, new byte[16]);
            var tmd = BuildTmd(titleId, contentId: 0x0000ABCD, contentIndex: 0, contentType: 0x0001, contentSize: (ulong)content.Length);
            var ciaBytes = BuildCia(ticket, tmd, content, presentIndex: 0);
            var z3ds = BuildZ3ds(ciaBytes, metadataSize: 0x10);

            var path = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N") + ".zcia");
            try
            {
                File.WriteAllBytes(path, z3ds);

                // The prefix parse yields the same tmd/ticket as a full decode.
                var md = Cia.ReadMetadataFromZ3ds(path, CancellationToken.None);
                Assert.Equal(titleId, md.Tmd.TitleId);
                Assert.Equal(titleId, md.Ticket.TitleId);
                using (var full = Cia.Open(path))
                {
                    Assert.Equal(full.Tmd.TitleId, md.Tmd.TitleId);
                    Assert.Equal(full.Tmd.TitleVersion, md.Tmd.TitleVersion);
                    Assert.Equal(full.Tmd.Contents.Count, md.Tmd.Contents.Count);
                }

                // ...while decompressing far less than the whole CIA (the content is never touched).
                using (var prefix = Z3ds.OpenPrefix(path))
                {
                    var md2 = Cia.ReadMetadata(prefix, CancellationToken.None);
                    Assert.Equal(titleId, md2.Tmd.TitleId);
                    Assert.True(prefix.Available < 1024 * 1024,
                        $"prefix decompressed {prefix.Available} bytes; expected far less than the {ciaBytes.Length}-byte CIA");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ContentCbc_ReadRange_DecryptsArbitraryBlockRanges()
        {
            // A multi-block content encrypted exactly as a CIA stores it (CBC, IV = content index BE16). Reading
            // an interior range must match the corresponding plaintext slice without decrypting from the start.
            ushort index = 0x0005;
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();
            var plaintext = Enumerable.Range(0, 0x200).Select(i => (byte)(i * 11 + 5)).ToArray();

            var iv = new byte[16];
            iv[0] = (byte)(index >> 8);
            iv[1] = (byte)index;
            byte[] enc;
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var e = aes.CreateEncryptor())
                {
                    enc = new byte[plaintext.Length];
                    e.TransformBlock(plaintext, 0, plaintext.Length, enc, 0);
                }
            }

            var path = Path.GetTempFileName();
            try
            {
                // Place the content at a non-zero base offset to prove base-offset handling.
                long baseOffset = 0x40;
                var file = new byte[baseOffset + enc.Length];
                Buffer.BlockCopy(enc, 0, file, (int)baseOffset, enc.Length);
                File.WriteAllBytes(path, file);

                // Whole content, a block-aligned interior range, and an unaligned range.
                AssertRange(path, baseOffset, index, titleKey, plaintext, 0, plaintext.Length);
                AssertRange(path, baseOffset, index, titleKey, plaintext, 0x80, 0x40);
                AssertRange(path, baseOffset, index, titleKey, plaintext, 0x35, 0x4A);

                // A start past the end yields nothing; a length past the end is clamped.
                Assert.Empty(ContentCbc.ReadRange(path, baseOffset, index, (ulong)plaintext.Length, titleKey, plaintext.Length, 16));
                var tail = ContentCbc.ReadRange(path, baseOffset, index, (ulong)plaintext.Length, titleKey, plaintext.Length - 8, 64);
                Assert.Equal(8, tail.Length);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertRange(string path, long baseOffset, ushort index, byte[] titleKey, byte[] plaintext, int offset, int length)
        {
            var got = ContentCbc.ReadRange(path, baseOffset, index, (ulong)plaintext.Length, titleKey, offset, length);
            Assert.Equal(plaintext.Skip(offset).Take(length).ToArray(), got);
        }

        [Fact]
        public void Smdh_TryReadFromEncryptedTitle_ReadsTitleFromCiaContent()
        {
            ulong titleId = 0x0004000000123400UL;
            var commonKey = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0x90 + i)).ToArray();

            // A no_crypto NCCH whose ExeFS "icon" entry is an SMDH (titled). no_crypto isolates the new
            // CBC-range + ExeFS-staging logic from the NCCH CTR (validated separately by NcchDecryptor).
            var smdh = BuildSmdhWithTitle("Mario Kart 7");
            var ncch = BuildNoCryptoNcchWithIcon(smdh);

            // Encrypt the NCCH as CIA content index 0 (titlekey CBC, IV = 0).
            byte[] encContent;
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = new byte[16];
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var e = aes.CreateEncryptor())
                {
                    encContent = new byte[ncch.Length];
                    e.TransformBlock(ncch, 0, ncch.Length, encContent, 0);
                }
            }

            // The ticket stores the titlekey CBC-encrypted under the common key (IV = titleId BE64 ‖ 0).
            var ivTk = new byte[16];
            WriteBe64(ivTk, 0, titleId);
            byte[] encTitleKey;
            using (var aes = Aes.Create())
            {
                aes.Key = commonKey;
                aes.IV = ivTk;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var e = aes.CreateEncryptor())
                {
                    encTitleKey = new byte[16];
                    e.TransformBlock(titleKey, 0, 16, encTitleKey, 0);
                }
            }

            var ticket = BuildTicket(titleId, 0x1122334455667788UL, 0, encTitleKey);
            var tmd = BuildTmd(titleId, contentId: 0x00000000, contentIndex: 0, contentType: 0x0001, contentSize: (ulong)ncch.Length);
            var ciaBytes = BuildCia(ticket, tmd, encContent, presentIndex: 0);

            var ciaPath = Path.GetTempFileName();
            var keyPath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(ciaPath, ciaBytes);
                File.WriteAllText(keyPath, "slot0x3DKeyN=" + ToHex(commonKey) + "\n");
                var keys = ThreeDsKeys.Load(new[] { keyPath }, null, null);

                using (var cia = Cia.Open(ciaPath))
                {
                    var info = Smdh.TryReadFromEncryptedTitle(cia, keys);
                    Assert.NotNull(info);
                    Assert.Equal("Mario Kart 7", info.Title);
                    Assert.NotNull(info.IconBmp);
                }
            }
            finally
            {
                File.Delete(ciaPath);
                File.Delete(keyPath);
            }
        }

        [Fact]
        public void Smdh_TryReadSmdhFromZ3ds_ReadsIconFromCompressedCiaPrefix()
        {
            // A .zcia must yield the same SMDH as a .cia — read from a bounded front prefix, without decompressing
            // the whole payload. A large, incompressible trailing content proves only the front is touched.
            ulong titleId = 0x0004000000123400UL;
            var commonKey = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();
            var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(0x90 + i)).ToArray();

            var ncch = BuildNoCryptoNcchWithIcon(BuildSmdhWithTitle("Kid Icarus Uprising"));
            // Pad the content with incompressible random bytes after the icon so the full CIA dwarfs the prefix.
            var padded = new byte[ncch.Length + 4 * 1024 * 1024];
            Buffer.BlockCopy(ncch, 0, padded, 0, ncch.Length);
            var padding = new byte[padded.Length - ncch.Length];
            new Random(7).NextBytes(padding); // padding content is never read by the prefix reader
            Buffer.BlockCopy(padding, 0, padded, ncch.Length, padding.Length);

            byte[] encContent;
            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = new byte[16]; // content index 0 -> zero IV
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var e = aes.CreateEncryptor())
                {
                    encContent = new byte[padded.Length - (padded.Length % 16)];
                    e.TransformBlock(padded, 0, encContent.Length, encContent, 0);
                }
            }

            var ivTk = new byte[16];
            WriteBe64(ivTk, 0, titleId);
            byte[] encTitleKey;
            using (var aes = Aes.Create())
            {
                aes.Key = commonKey;
                aes.IV = ivTk;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var e = aes.CreateEncryptor())
                {
                    encTitleKey = new byte[16];
                    e.TransformBlock(titleKey, 0, 16, encTitleKey, 0);
                }
            }

            var ticket = BuildTicket(titleId, 0x1122334455667788UL, 0, encTitleKey);
            var tmd = BuildTmd(titleId, contentId: 0x00000000, contentIndex: 0, contentType: 0x0001, contentSize: (ulong)encContent.Length);
            var ciaBytes = BuildCia(ticket, tmd, encContent, presentIndex: 0);
            var z3ds = BuildZ3ds(ciaBytes, metadataSize: 0x10);

            var zPath = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N") + ".zcia");
            var keyPath = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(zPath, z3ds);
                File.WriteAllText(keyPath, "slot0x3DKeyN=" + ToHex(commonKey) + "\n");
                var keys = ThreeDsKeys.Load(new[] { keyPath }, null, null);

                var info = Cia.TryReadSmdhFromZ3ds(zPath, keys, CancellationToken.None);
                Assert.NotNull(info);
                Assert.Equal("Kid Icarus Uprising", info.Title);
                Assert.NotNull(info.IconBmp);
            }
            finally
            {
                File.Delete(zPath);
                File.Delete(keyPath);
            }
        }

        [Fact]
        public void ThreeDsLooseInfo_ReadsTitleIdAndIconFromNcsdCartridge()
        {
            // A .cci/.3ds cartridge: an NCSD whose partition 0 is a no_crypto NCCH carrying the SMDH. No keys are
            // needed (no_crypto); the title id comes from the NCSD media id.
            ulong titleId = 0x0004000000123400UL;
            var ncch = BuildNoCryptoNcchWithIcon(BuildSmdhWithTitle("Zelda OoT 3D"));
            var ncsd = BuildNcsdWithPartition0(titleId, ncch, part0MediaOffset: 8);

            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, ncsd);
                var info = ThreeDsLooseInfo.TryRead(path, null, null, CancellationToken.None);
                Assert.NotNull(info);
                Assert.Equal(titleId, info.TitleId);
                Assert.NotNull(info.Smdh);
                Assert.Equal("Zelda OoT 3D", info.Smdh.Title);
                Assert.NotNull(info.Smdh.IconBmp);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ThreeDsLooseInfo_ReadsFromBareNcchAndCompressedZ3ds()
        {
            // A bare .cxi (NCCH at offset 0) and its Z3DS-compressed .zcxi must both resolve the same title id +
            // SMDH — the compressed path decompresses only the small front prefix holding the ExeFS icon.
            ulong titleId = 0x0004000000ABCD00UL;
            var ncch = BuildNoCryptoNcchWithIcon(BuildSmdhWithTitle("Pushmo"));
            SetNcchPartitionId(ncch, titleId);

            var plainPath = Path.GetTempFileName();
            var zPath = Path.Combine(Path.GetTempPath(), "el3ds_" + Guid.NewGuid().ToString("N") + ".zcxi");
            try
            {
                File.WriteAllBytes(plainPath, ncch);
                var plain = ThreeDsLooseInfo.TryRead(plainPath, null, null, CancellationToken.None);
                Assert.NotNull(plain);
                Assert.Equal(titleId, plain.TitleId);
                Assert.Equal("Pushmo", plain.Smdh.Title);

                File.WriteAllBytes(zPath, BuildZ3ds(ncch, metadataSize: 0x10));
                var z = ThreeDsLooseInfo.TryRead(zPath, null, null, CancellationToken.None);
                Assert.NotNull(z);
                Assert.Equal(titleId, z.TitleId);
                Assert.Equal("Pushmo", z.Smdh.Title);
                Assert.NotNull(z.Smdh.IconBmp);
            }
            finally
            {
                File.Delete(plainPath);
                File.Delete(zPath);
            }
        }

        // Wraps a bare NCCH as NCSD partition 0 at the given media offset; the NCSD media id (0x108) is the title
        // id. Media unit is 0x200 (flags[6] = 0), matching the builders above.
        private static byte[] BuildNcsdWithPartition0(ulong mediaId, byte[] ncch, uint part0MediaOffset)
        {
            const int mediaUnit = 0x200;
            long part0Offset = (long)part0MediaOffset * mediaUnit;
            var ncsd = new byte[part0Offset + ncch.Length];
            Encoding.ASCII.GetBytes("NCSD").CopyTo(ncsd, 0x100);
            WriteLe64(ncsd, 0x108, mediaId); // media id == title id
            ncsd[0x188 + 6] = 0;             // media unit shift -> 0x200
            WriteLe32(ncsd, 0x120 + 0, part0MediaOffset);
            WriteLe32(ncsd, 0x120 + 4, (uint)((ncch.Length + mediaUnit - 1) / mediaUnit));
            Buffer.BlockCopy(ncch, 0, ncsd, (int)part0Offset, ncch.Length);
            return ncsd;
        }

        private static void SetNcchPartitionId(byte[] ncch, ulong titleId) => WriteLe64(ncch, 0x108, titleId);

        private static byte[] BuildSmdhWithTitle(string englishTitle)
        {
            var smdh = new byte[Smdh.Size];
            Encoding.ASCII.GetBytes("SMDH").CopyTo(smdh, 0);
            var title = Encoding.Unicode.GetBytes(englishTitle);
            Buffer.BlockCopy(title, 0, smdh, 0x08 + 1 * 0x200, title.Length); // language 1 = English short desc
            return smdh;
        }

        // Minimal no_crypto NCCH: 0x200 header (no_crypto set), ExeFS at media offset 1 whose first entry is
        // "icon" -> the SMDH.
        private static byte[] BuildNoCryptoNcchWithIcon(byte[] smdh)
        {
            const int mediaUnit = 0x200;
            int exefsOffset = mediaUnit;          // 0x200
            int iconData = exefsOffset + 0x200;   // 0x400
            int total = iconData + smdh.Length;
            int exefsMu = (total - exefsOffset + mediaUnit - 1) / mediaUnit;

            var ncch = new byte[total];
            Encoding.ASCII.GetBytes("NCCH").CopyTo(ncch, 0x100);
            ncch[0x188 + 6] = 0;     // media unit shift -> 0x200
            ncch[0x188 + 7] = 0x04;  // no_crypto
            WriteLe32(ncch, 0x1A0, 1);             // ExeFS offset (media units)
            WriteLe32(ncch, 0x1A4, (uint)exefsMu); // ExeFS size (media units)

            Encoding.ASCII.GetBytes("icon").CopyTo(ncch, exefsOffset + 0);
            WriteLe32(ncch, exefsOffset + 0x08, 0);
            WriteLe32(ncch, exefsOffset + 0x0C, (uint)smdh.Length);
            Buffer.BlockCopy(smdh, 0, ncch, iconData, smdh.Length);
            return ncch;
        }

        private static string ToHex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        #region Synthetic blob builders

        // Builds a Z3DS file wrapping `underlying`: 0x20-byte header, a zero metadata block of `metadataSize`,
        // then a single standard zstd frame (a valid degenerate seekable payload) followed by a skippable
        // seek-table frame. Mirrors Azahar's container layout closely enough to exercise our reader.
        private static byte[] BuildZ3ds(byte[] underlying, uint metadataSize)
        {
            byte[] frame;
            using (var compressor = new Compressor(3))
            {
                var dest = new byte[Compressor.GetCompressBound(underlying.Length)];
                int n = compressor.Wrap(underlying, dest);
                frame = dest.Take(n).ToArray();
            }

            // Skippable frame: magic 0x184D2A50 (LE) + u32 content size + content (a stand-in seek table).
            var seekTable = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var skippable = new byte[8 + seekTable.Length];
            WriteLe32(skippable, 0, 0x184D2A50);
            WriteLe32(skippable, 4, (uint)seekTable.Length);
            Buffer.BlockCopy(seekTable, 0, skippable, 8, seekTable.Length);

            var payload = frame.Concat(skippable).ToArray();

            const int headerSize = 0x20;
            var file = new byte[headerSize + (int)metadataSize + payload.Length];
            file[0] = (byte)'Z'; file[1] = (byte)'3'; file[2] = (byte)'D'; file[3] = (byte)'S';
            file[0x08] = 1; // version
            WriteLe16(file, 0x0A, (ushort)headerSize);
            WriteLe32(file, 0x0C, metadataSize);
            WriteLe64(file, 0x10, (ulong)payload.Length);
            WriteLe64(file, 0x18, (ulong)underlying.Length);
            Buffer.BlockCopy(payload, 0, file, headerSize + (int)metadataSize, payload.Length);
            return file;
        }


        private static byte[] BuildTicket(ulong titleId, ulong ticketId, byte commonKeyIndex, byte[] encTitleKey)
        {
            var ticket = new byte[Rsa2048SigBlock + 0x210];
            WriteBe32(ticket, 0, SigTypeRsa2048Sha256);
            Buffer.BlockCopy(encTitleKey, 0, ticket, Rsa2048SigBlock + 0x7F, 16);
            WriteBe64(ticket, Rsa2048SigBlock + 0x90, ticketId);
            WriteBe64(ticket, Rsa2048SigBlock + 0x9C, titleId);
            ticket[Rsa2048SigBlock + 0xB1] = commonKeyIndex;
            return ticket;
        }

        private static byte[] BuildTmd(ulong titleId, uint contentId, ushort contentIndex, ushort contentType, ulong contentSize)
        {
            var tmd = new byte[Rsa2048SigBlock + 0x9C4 + 0x30];
            WriteBe32(tmd, 0, SigTypeRsa2048Sha256);
            WriteBe64(tmd, Rsa2048SigBlock + 0x4C, titleId);
            WriteBe16(tmd, Rsa2048SigBlock + 0x9C, 0);
            WriteBe16(tmd, Rsa2048SigBlock + 0x9E, 1);
            int chunks = Rsa2048SigBlock + 0x9C4;
            WriteBe32(tmd, chunks + 0x00, contentId);
            WriteBe16(tmd, chunks + 0x04, contentIndex);
            WriteBe16(tmd, chunks + 0x06, contentType);
            WriteBe64(tmd, chunks + 0x08, contentSize);
            return tmd;
        }

        private static byte[] BuildCia(byte[] ticket, byte[] tmd, byte[] content, int presentIndex)
        {
            const int headerSize = 0x2020;
            int certOffset = Align(headerSize);
            int ticketOffset = Align(certOffset + 0);
            int tmdOffset = Align(ticketOffset + ticket.Length);
            int contentOffset = Align(tmdOffset + tmd.Length);
            int total = contentOffset + content.Length;

            var cia = new byte[total];
            WriteLe32(cia, 0x00, headerSize);
            WriteLe32(cia, 0x08, 0);                 // cert chain size
            WriteLe32(cia, 0x0C, (uint)ticket.Length);
            WriteLe32(cia, 0x10, (uint)tmd.Length);
            WriteLe32(cia, 0x14, 0);                 // meta size
            WriteLe64(cia, 0x18, (ulong)content.Length);
            cia[0x20 + presentIndex / 8] |= (byte)(0x80 >> (presentIndex % 8));

            Buffer.BlockCopy(ticket, 0, cia, ticketOffset, ticket.Length);
            Buffer.BlockCopy(tmd, 0, cia, tmdOffset, tmd.Length);
            Buffer.BlockCopy(content, 0, cia, contentOffset, content.Length);
            return cia;
        }

        private static int Align(int v) => (v + 0x3F) / 0x40 * 0x40;

        private static void WriteBe16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v >> 8);
            b[o + 1] = (byte)v;
        }

        private static void WriteBe32(byte[] b, int o, uint v)
        {
            for (int i = 0; i < 4; i++)
                b[o + i] = (byte)(v >> (24 - i * 8));
        }

        private static void WriteBe64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++)
                b[o + i] = (byte)(v >> (56 - i * 8));
        }

        private static void WriteLe16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void WriteLe32(byte[] b, int o, uint v)
        {
            for (int i = 0; i < 4; i++)
                b[o + i] = (byte)(v >> (i * 8));
        }

        private static void WriteLe64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++)
                b[o + i] = (byte)(v >> (i * 8));
        }

        #endregion
    }
}
