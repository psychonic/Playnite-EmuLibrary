using EmuLibrary;
using EmuLibrary.Installs;
using EmuLibrary.RomTypes;
using EmuLibrary.RomTypes.Vita;
using EmuLibrary.Settings;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.ScanConcurrency;
using EmuLibrary.Util.Vita;
using Playnite.SDK;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Vita
{
    // PSN encrypted PKG + zRIF coverage: the scanner's PSN folder-shape association + content-id /
    // update-order parsing via BuildTitle, a zRIF decode round-trip (base64 + zlib inflate + adler + RIF field
    // extraction, exercised over a .NET-produced deflate stream — the no-dictionary path), and a self-consistent
    // Vita PKG decrypt round-trip that validates the key derivation + AES-128-CTR extraction end to end.
    //
    // The preset-dictionary zRIF path and decrypt against REAL Sony packages are [VERIFY] — deferred to the
    // manual test, as no real .pkg / work.bin samples were available.
    public class VitaPhase2Tests
    {
        private const string TitleId = "PCSE00890";
        private const string BaseContentId = "UP4395-PCSE00890_00-10SECNINJAVITAUS";

        // Public "pkg_vita_2" key (henkaku wiki), used here to forge a valid synthetic package for the round-trip.
        private static readonly byte[] PkgVita2 =
        {
            0xE3, 0x1A, 0x70, 0xC9, 0xCE, 0x1D, 0xD7, 0x2B,
            0xF3, 0xC0, 0x62, 0x29, 0x63, 0xF2, 0xEC, 0xCB,
        };

        private sealed class StubEmuLibrary : IEmuLibrary
        {
            public ILogger Logger => LogManager.GetLogger();
            public IPlayniteAPI Playnite => null;
            public global::EmuLibrary.Settings.Settings Settings => null;
            public IScanCache ScanCache => null;
            public IScanConcurrency ScanConcurrency => null;
            public IInstallProgressService InstallProgress => null;
            public string GetPluginUserDataPath() => null;
            public RomTypeScanner GetScanner(RomType romType) => null;
        }

        private static VitaScanner NewScanner() => new VitaScanner(new StubEmuLibrary());

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-vita-p2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string MakePsnFolder(string src, string folderName, string pkgFileName, bool withWorkBin)
        {
            var folder = Path.Combine(src, folderName);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, pkgFileName), new byte[16]);
            if (withWorkBin)
                File.WriteAllBytes(Path.Combine(folder, "work.bin"), new byte[512]);
            return folder;
        }

        [Fact]
        public void BuildTitle_Psn_GroupsBaseUpdatesDlcByTitleId()
        {
            var src = NewTempDir();
            try
            {
                MakePsnFolder(src, "10 Second Ninja X (USA)",
                    BaseContentId + "_bg_1_abc.pkg", withWorkBin: true);
                MakePsnFolder(src, "10 Second Ninja X (USA) (v1.02) (Update)",
                    BaseContentId + "-A0102-V0100-abcdef-PE.pkg", withWorkBin: false);
                MakePsnFolder(src, "10 Second Ninja X (USA) (v1.01) (Update)",
                    BaseContentId + "-A0101-V0100-abcdef-PE.pkg", withWorkBin: false);
                MakePsnFolder(src, "10 Second Ninja X (USA) (DLC)",
                    "UP4395-PCSE00890_00-DLC0000000000001_bg.pkg", withWorkBin: true);

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, src, TitleId, CancellationToken.None);

                Assert.NotNull(title);
                Assert.Equal(TitleId, title.TitleId);

                // Base is a pkg-extract item with the parsed content-id.
                Assert.Equal(VitaSourceKind.Pkg, title.BaseRef.SourceKind);
                Assert.Equal(BaseContentId, title.BaseRef.ContentId);

                // Updates ordered numerically ascending (01.01 before 01.02), latest is the reported version.
                Assert.Equal(new[] { "01.01", "01.02" }, title.Updates.Select(u => u.AppVer).ToArray());
                Assert.Equal("01.02", title.Version);
                Assert.All(title.Updates, u => Assert.Equal(VitaSourceKind.Pkg, u.SourceKind));

                // One DLC, and two licenses (the base work.bin + the DLC work.bin).
                Assert.Single(title.DlcList);
                Assert.Equal(VitaContentKind.AddCont, title.DlcList[0].Kind);
                Assert.Equal(2, title.LicenseList.Count);
                Assert.All(title.LicenseList, l => Assert.Equal(VitaSourceKind.License, l.SourceKind));
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_Psn_UpdatesInOrderMatchesCompositeSelection()
        {
            var src = NewTempDir();
            try
            {
                MakePsnFolder(src, "Game (USA)", BaseContentId + "_bg.pkg", withWorkBin: true);
                MakePsnFolder(src, "Game (USA) (v1.10) (Update)", BaseContentId + "-A0110-V0100-x-PE.pkg", false);
                MakePsnFolder(src, "Game (USA) (v1.02) (Update)", BaseContentId + "-A0102-V0100-x-PE.pkg", false);

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, src, TitleId, CancellationToken.None);

                var ordered = CompositeContent.SelectUpdatesToInstall(
                    title.Updates, UpdateInstallStrategy.InstallAllUpdatesInOrder, x => x.AppVerParsed);

                // 01.10 must sort AFTER 01.02 (numeric, not lexical).
                Assert.Equal(new[] { "01.02", "01.10" }, ordered.Select(u => u.AppVer).ToArray());
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_Psn_OrphanUpdatesWithNoBase_ReturnsNull()
        {
            var src = NewTempDir();
            try
            {
                MakePsnFolder(src, "Game (USA) (v1.01) (Update)", BaseContentId + "-A0101-V0100-x-PE.pkg", false);
                MakePsnFolder(src, "Game (USA) (DLC)", "UP4395-PCSE00890_00-DLC0000000000001_bg.pkg", true);

                var mapping = new EmulatorMapping { SourcePath = src };
                Assert.Null(NewScanner().BuildTitle(mapping, src, TitleId, CancellationToken.None));
            }
            finally { Directory.Delete(src, true); }
        }

        // ---- zRIF decode (no-dictionary path) ----

        [Fact]
        public void Zrif_Decode_RoundTripsRifWithContentIdAndKlicensee()
        {
            var rif = new byte[512];
            var contentId = Encoding.ASCII.GetBytes(BaseContentId);
            Buffer.BlockCopy(contentId, 0, rif, 0x10, contentId.Length);
            var klic = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            Buffer.BlockCopy(klic, 0, rif, 0x50, klic.Length);
            // Some payload so deflate has content to compress beyond the fields.
            for (int i = 0x70; i < rif.Length; i++)
                rif[i] = (byte)(i * 7);

            var zrif = Convert.ToBase64String(ZlibCompressNoDict(rif));

            var decoded = Zrif.DecodeToRif(zrif);

            Assert.Equal(rif, decoded);
            Assert.Equal(BaseContentId, Zrif.GetContentId(decoded));
            Assert.Equal(klic, Zrif.GetKlicensee(decoded));
        }

        [Fact]
        public void Zrif_ReadRifFromWorkBin_PassesThroughRawRif()
        {
            var rif = new byte[512];
            var contentId = Encoding.ASCII.GetBytes(BaseContentId);
            Buffer.BlockCopy(contentId, 0, rif, 0x10, contentId.Length);
            // A raw (binary) work.bin: byte 0 is a non-base64 control byte, so it is NOT treated as a zRIF string.
            rif[0] = 0x01;

            var path = Path.Combine(NewTempDir(), "work.bin");
            File.WriteAllBytes(path, rif);
            try
            {
                var result = Zrif.ReadRifFromWorkBin(path);
                Assert.Equal(rif, result);
                Assert.Equal(BaseContentId, Zrif.GetContentId(result));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path), true); }
        }

        // ---- VitaPkg decrypt round-trip (self-consistent: forge a package with the real public key) ----

        [Fact]
        public void VitaPkg_ExtractTo_DecryptsSyntheticPackage()
        {
            var content = Encoding.ASCII.GetBytes("HELLOVITA1234567"); // exactly 16 bytes
            var pkgBytes = BuildSyntheticVitaPkg(BaseContentId, content, out var iv);
            Assert.Equal(16, iv.Length);

            var dir = NewTempDir();
            var pkgPath = Path.Combine(dir, "test.pkg");
            File.WriteAllBytes(pkgPath, pkgBytes);
            var outDir = Path.Combine(dir, "out");
            try
            {
                using (var pkg = VitaPkg.Open(pkgPath))
                {
                    Assert.Equal(BaseContentId, pkg.ContentId);
                    Assert.Equal(TitleId, pkg.TitleId);
                    Assert.Equal(2, pkg.KeyType);
                    pkg.ExtractTo(outDir, CancellationToken.None);
                }

                var eboot = Path.Combine(outDir, "eboot.bin");
                Assert.True(File.Exists(eboot));
                Assert.Equal(content, File.ReadAllBytes(eboot));

                // The reconstructed sce_sys/package/ metadata is present.
                Assert.True(File.Exists(Path.Combine(outDir, "sce_sys", "package", "head.bin")));
                Assert.True(File.Exists(Path.Combine(outDir, "sce_sys", "package", "stat.bin")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void VitaPkg_TryExtractItem_ReturnsSingleEntryDecrypted()
        {
            var content = Encoding.ASCII.GetBytes("HELLOVITA1234567"); // exactly 16 bytes
            var pkgBytes = BuildSyntheticVitaPkg(BaseContentId, content, out var iv);
            Assert.Equal(16, iv.Length);

            var dir = NewTempDir();
            var pkgPath = Path.Combine(dir, "test.pkg");
            File.WriteAllBytes(pkgPath, pkgBytes);
            try
            {
                using (var pkg = VitaPkg.Open(pkgPath))
                {
                    // Case-insensitive name match; decrypted bytes returned without writing the whole package.
                    Assert.True(pkg.TryExtractItem("EBOOT.BIN", out var bytes));
                    Assert.Equal(content, bytes);

                    // A missing entry (e.g. this synthetic pkg carries no icon) is a clean miss, not an exception.
                    Assert.False(pkg.TryExtractItem("sce_sys/icon0.png", out var missing));
                    Assert.Null(missing);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void VitaPkg_TitleIdFromContentId_Parses()
        {
            Assert.Equal(TitleId, VitaPkg.TitleIdFromContentId(BaseContentId));
            Assert.Null(VitaPkg.TitleIdFromContentId(null));
            Assert.Null(VitaPkg.TitleIdFromContentId("garbage"));
        }

        // Builds a minimal but format-valid Vita PKG: plaintext header + ext header + one metadata (type 13)
        // record, and an AES-128-CTR encrypted data segment holding a 1-entry item table, the name "eboot.bin"
        // and the file content. Key type 2 → main key = AES-ECB(pkg_vita_2, iv).
        private static byte[] BuildSyntheticVitaPkg(string contentId, byte[] content, out byte[] iv)
        {
            iv = new byte[16];
            for (int i = 0; i < 16; i++) iv[i] = (byte)(0x10 + i);

            const int encOffset = 0x400;
            const int itemTableOffset = 0;   // relative to the data segment
            const int nameOffset = 32;
            const int nameSize = 9;          // "eboot.bin"
            const int dataOffset = 48;       // 16-aligned, after the name
            int dataSize = content.Length;
            int encSize = dataOffset + dataSize;

            // Plaintext data segment.
            var seg = new byte[((encSize + 15) / 16) * 16];
            // Item entry (32 bytes).
            WriteU32BE(seg, 0x00, nameOffset);
            WriteU32BE(seg, 0x04, nameSize);
            WriteU64BE(seg, 0x08, dataOffset);
            WriteU64BE(seg, 0x10, (ulong)dataSize);
            // flags byte at 0x1B = 0 → a file (not 4/18).
            var nameBytes = Encoding.ASCII.GetBytes("eboot.bin");
            Buffer.BlockCopy(nameBytes, 0, seg, nameOffset, nameBytes.Length);
            Buffer.BlockCopy(content, 0, seg, dataOffset, content.Length);

            var mainKey = AesEcbEncryptBlock(PkgVita2, iv);
            var encSeg = CtrXor(mainKey, iv, seg);

            int itemsSize = dataOffset; // item table + names region
            int fileSize = encOffset + encSeg.Length;
            var file = new byte[fileSize];

            WriteU32BE(file, 0x00, 0x7F504B47);           // magic
            WriteU32BE(file, 0x08, 0x100);                // meta offset
            WriteU32BE(file, 0x0C, 1);                    // meta count
            WriteU32BE(file, 0x14, 1);                    // item count
            WriteU64BE(file, 0x20, encOffset);            // enc offset
            WriteU64BE(file, 0x28, (ulong)encSize);       // enc size
            var cid = Encoding.ASCII.GetBytes(contentId);
            Buffer.BlockCopy(cid, 0, file, 0x30, cid.Length);
            Buffer.BlockCopy(iv, 0, file, 0x70, 16);      // pkg_data_riv
            WriteU32BE(file, 0xC0, 0x7F657874);           // ext magic
            file[0xE7] = 2;                               // key type

            // metadata record type 13 at 0x100: items_offset, items_size.
            WriteU32BE(file, 0x100, 13);
            WriteU32BE(file, 0x104, 8);
            WriteU32BE(file, 0x108, itemTableOffset);
            WriteU32BE(file, 0x10C, itemsSize);

            Buffer.BlockCopy(encSeg, 0, file, encOffset, encSeg.Length);
            return file;
        }

        private static byte[] AesEcbEncryptBlock(byte[] key, byte[] block)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = key;
                using (var enc = aes.CreateEncryptor())
                {
                    var outb = new byte[16];
                    enc.TransformBlock(block, 0, 16, outb, 0);
                    return outb;
                }
            }
        }

        // AES-128-CTR XOR of `data` with keystream block N = AES-ECB(key, iv + N).
        private static byte[] CtrXor(byte[] key, byte[] iv, byte[] data)
        {
            var result = (byte[])data.Clone();
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = key;
                using (var enc = aes.CreateEncryptor())
                {
                    int blocks = data.Length / 16;
                    var counter = new byte[16];
                    var keystream = new byte[16];
                    for (int b = 0; b < blocks; b++)
                    {
                        BuildCounter(counter, iv, (ulong)b);
                        enc.TransformBlock(counter, 0, 16, keystream, 0);
                        for (int i = 0; i < 16; i++)
                            result[b * 16 + i] ^= keystream[i];
                    }
                }
            }
            return result;
        }

        private static void BuildCounter(byte[] dst, byte[] iv, ulong blockIndex)
        {
            Buffer.BlockCopy(iv, 0, dst, 0, 16);
            int idx = 15;
            ulong add = blockIndex;
            uint carry = 0;
            while (idx >= 0 && (add != 0 || carry != 0))
            {
                ulong val = (ulong)dst[idx] + (byte)(add & 0xFF) + carry;
                dst[idx] = (byte)val;
                carry = (uint)(val >> 8);
                add >>= 8;
                idx--;
            }
        }

        // Wraps raw DEFLATE of `data` in a zlib stream WITHOUT a preset dictionary (header 0x78 0x9C, trailing
        // big-endian adler-32). Mirrors what Zrif.DecodeToRif consumes on the no-dictionary path.
        private static byte[] ZlibCompressNoDict(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                    ds.Write(data, 0, data.Length);
                uint adler = Adler32(data);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
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

        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        private static void WriteU32BE(byte[] b, int o, int v) => WriteU32BE(b, o, (uint)v);

        private static void WriteU64BE(byte[] b, int o, ulong v)
        {
            WriteU32BE(b, o, (uint)(v >> 32));
            WriteU32BE(b, o + 4, (uint)v);
        }
    }
}
