using EmuLibrary.RomTypes.Ps3;
using EmuLibrary.Util.Ps3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Xunit;

namespace EmuLibrary.Tests.Util
{
    public class Ps3Tests
    {
        // Public PS3 retail PKG AES-128 key (psdevwiki). Repeated here so the test independently builds
        // ciphertext rather than reaching into Ps3Pkg's private copy.
        private static readonly byte[] GpkgKey =
        {
            0x2E, 0x7B, 0x71, 0xD7, 0xC9, 0xC9, 0xA1, 0x4E,
            0xA3, 0x22, 0x1F, 0x18, 0x88, 0x28, 0xB8, 0xF8,
        };

        #region ParamSfo

        [Fact]
        public void ParamSfo_ParsesStringEntries()
        {
            var sfo = BuildSfo(new[]
            {
                ("CATEGORY", "HG"),
                ("TITLE", "Demon's Souls"),
                ("TITLE_ID", "BLES01234"),
                ("APP_VER", "01.02"),
            });

            Assert.True(ParamSfo.TryParse(sfo, out var parsed));
            Assert.Equal("Demon's Souls", parsed.Title);
            Assert.Equal("BLES01234", parsed.TitleId);
            Assert.Equal("01.02", parsed.AppVer);
            Assert.Equal("HG", parsed.Category);
        }

        [Fact]
        public void ParamSfo_RejectsNonSfoData()
        {
            Assert.False(ParamSfo.TryParse(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }, out _));
        }

        #endregion

        #region APP_VER ordering

        [Fact]
        public void AppVer_OrdersAsVersions()
        {
            var unordered = new[] { "02.00", "01.10", "01.00", "01.02" }
                .Select(v => new Ps3FileInfo { AppVer = v });

            var ordered = unordered.OrderBy(u => u.AppVerParsed).Select(u => u.AppVer).ToList();

            // "01.10" must sort above "01.02" (numeric minor, not lexical).
            Assert.Equal(new[] { "01.00", "01.02", "01.10", "02.00" }, ordered);
        }

        #endregion

        #region content-id / title-id

        [Theory]
        [InlineData("EP0001-BLES01234_00-0000000000000000", "BLES01234")]
        [InlineData("UP0001-NPUB30001_00-ADDCONT000000001", "NPUB30001")]
        [InlineData("EP0001-BLES01234_00-DLC0001", "BLES01234")]
        public void TitleIdFromContentId_Parses(string contentId, string expected)
        {
            Assert.Equal(expected, Ps3FileInfo.TitleIdFromContentId(contentId));
        }

        [Fact]
        public void TitleIdFromContentId_RapFilenameIsContentId()
        {
            // A RAP's filename (sans extension) IS the content-id.
            const string rapName = "EP0001-BLES01234_00-DLC0001";
            Assert.Equal("BLES01234", Ps3FileInfo.TitleIdFromContentId(rapName));
        }

        #endregion

        #region content classification

        // Pins the classifier against representative real-data cases. Primary update signal is the
        // PKG metadata patch flag (isPatch), validated 400/400 vs NoIntro "(Update)" tags. CATEGORY alone is
        // ambiguous; for non-patches, absence of APP_VER means DLC. (expected as int so the internal
        // Ps3ContentType isn't exposed in a public signature.)
        [Theory]
        // Patch flag wins regardless of category/version — fixes base-vs-update PSN ambiguity.
        [InlineData(true,  "HG", "01.00", null, (int)Ps3ContentType.Update)]
        [InlineData(true,  "GD", "01.36", null, (int)Ps3ContentType.Update)]
        // Fallback: TARGET_APP_VER set ⇒ update even if the flag couldn't be read.
        [InlineData(false, "HG", "01.02", "01.00", (int)Ps3ContentType.Update)]
        // DLC: not a patch, no APP_VER (the dominant 5145 GD + 14 HG rows + unlock keys).
        [InlineData(false, "GD", null, null, (int)Ps3ContentType.Dlc)]
        [InlineData(false, "HG", null, null, (int)Ps3ContentType.Dlc)]
        // Base games / trials: not a patch, HG/DG with APP_VER.
        [InlineData(false, "HG", "01.00", null, (int)Ps3ContentType.PkgGame)]
        [InlineData(false, "DG", "01.00", null, (int)Ps3ContentType.PkgGame)]
        // A bare disc-game pkg with no APP_VER is still a base.
        [InlineData(false, "DG", null, null, (int)Ps3ContentType.PkgGame)]
        public void Classify_PinsCorpusDerivedRule(bool isPatch, string category, string appVer, string targetAppVer, int expected)
        {
            Assert.Equal(expected, (int)Ps3FileInfo.Classify(isPatch, category, appVer, targetAppVer));
        }

        #endregion

        #region PKG header parse + native decrypt/extract

        [Fact]
        public void Ps3Pkg_ReadsHeaderAndDecryptsParamSfoAndExtracts()
        {
            const string contentId = "EP0001-BLES01234_00-0000000000000000";
            var riv = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

            var sfoBytes = BuildSfo(new[]
            {
                ("CATEGORY", "HG"),
                ("TITLE", "Demon's Souls"),
                ("TITLE_ID", "BLES01234"),
                ("APP_VER", "01.00"),
            });
            var ebootBytes = Encoding.ASCII.GetBytes("FAKE-EBOOT-CONTENT-1234567890");

            var pkgPath = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            try
            {
                WriteSyntheticPkg(pkgPath, contentId, riv, isPatch: false,
                    new[]
                    {
                        ("PARAM.SFO", sfoBytes),
                        ("USRDIR/EBOOT.BIN", ebootBytes),
                    });

                using (var pkg = Ps3Pkg.Open(pkgPath))
                {
                    Assert.True(pkg.IsFinalizedRetail);
                    Assert.False(pkg.IsPatch);
                    Assert.Equal(contentId, pkg.ContentId);
                    Assert.Equal("BLES01234", pkg.TitleId);

                    var sfo = pkg.ReadParamSfo();
                    Assert.NotNull(sfo);
                    Assert.Equal("Demon's Souls", sfo.Title);
                    Assert.Equal("HG", sfo.Category);

                    var outDir = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        pkg.ExtractTo(outDir, CancellationToken.None);

                        var extractedSfo = File.ReadAllBytes(Path.Combine(outDir, "PARAM.SFO"));
                        Assert.Equal(sfoBytes, extractedSfo);

                        var extractedEboot = File.ReadAllBytes(Path.Combine(outDir, "USRDIR", "EBOOT.BIN"));
                        Assert.Equal(ebootBytes, extractedEboot);
                    }
                    finally
                    {
                        if (Directory.Exists(outDir))
                            Directory.Delete(outDir, true);
                    }
                }
            }
            finally
            {
                if (File.Exists(pkgPath))
                    File.Delete(pkgPath);
            }
        }

        [Fact]
        public void Ps3Pkg_ExtractTo_NonBootableAddOnDoesNotClobberBootableRootSfo()
        {
            // Base game SFO (bootable HG) vs an add-on's own root SFO (CATEGORY=GD, game data). The add-on
            // (DLC, or an oddly-packaged update), extracted on top of the installed base, must not replace the
            // base's bootable root PARAM.SFO, but must still write its own files (e.g. C00/...). Mirrors a base
            // PKG + non-bootable add-on PKG installed into the same dev_hdd0/game/<id> dir.
            var riv = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();

            var baseSfo = BuildSfo(new[] { ("CATEGORY", "HG"), ("TITLE", "Quantum Conundrum"), ("TITLE_ID", "NPUB30601") });
            var dlcRootSfo = BuildSfo(new[] { ("CATEGORY", "GD"), ("TITLE", "Quantum Conundrum"), ("TITLE_ID", "NPUB30601") });
            var dlcContent = Encoding.ASCII.GetBytes("DLC-PAYLOAD");

            var basePkg = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            var dlcPkg = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            var outDir = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteSyntheticPkg(basePkg, "UP0082-NPUB30601_00-QUANTUMCONUNDRUM", riv, isPatch: false,
                    new[] { ("PARAM.SFO", baseSfo), ("USRDIR/EBOOT.BIN", Encoding.ASCII.GetBytes("EBOOT")) });
                WriteSyntheticPkg(dlcPkg, "UP0082-NPUB30601_00-QUANTPUZZLEPACK1", riv, isPatch: false,
                    new[] { ("PARAM.SFO", dlcRootSfo), ("C00/PARAM.SFO", dlcRootSfo), ("C00/DATA.BIN", dlcContent) });

                using (var pkg = Ps3Pkg.Open(basePkg))
                    pkg.ExtractTo(outDir, CancellationToken.None);
                using (var pkg = Ps3Pkg.Open(dlcPkg))
                    pkg.ExtractTo(outDir, CancellationToken.None, protectBootableRootParamSfo: true);

                // Base game's bootable SFO survived; the add-on's own files landed under C00.
                Assert.Equal(baseSfo, File.ReadAllBytes(Path.Combine(outDir, "PARAM.SFO")));
                Assert.Equal(dlcRootSfo, File.ReadAllBytes(Path.Combine(outDir, "C00", "PARAM.SFO")));
                Assert.Equal(dlcContent, File.ReadAllBytes(Path.Combine(outDir, "C00", "DATA.BIN")));
            }
            finally
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                if (File.Exists(basePkg)) File.Delete(basePkg);
                if (File.Exists(dlcPkg)) File.Delete(dlcPkg);
            }
        }

        [Fact]
        public void Ps3Pkg_ExtractTo_BootableUpdateStillReplacesRootSfo()
        {
            // A version-bumping HG update is itself bootable, so it must still overwrite the base's root
            // PARAM.SFO — the protection only blocks DOWNGRADES to a non-bootable category, not legit updates.
            var riv = Enumerable.Range(0, 16).Select(i => (byte)(0x20 + i)).ToArray();

            var baseSfo = BuildSfo(new[] { ("CATEGORY", "HG"), ("TITLE", "Game"), ("TITLE_ID", "NPUB30601"), ("APP_VER", "01.00") });
            var updateSfo = BuildSfo(new[] { ("CATEGORY", "HG"), ("TITLE", "Game"), ("TITLE_ID", "NPUB30601"), ("APP_VER", "01.03") });

            var basePkg = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            var updatePkg = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            var outDir = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteSyntheticPkg(basePkg, "UP0082-NPUB30601_00-GAME0000000000000", riv, isPatch: false,
                    new[] { ("PARAM.SFO", baseSfo), ("USRDIR/EBOOT.BIN", Encoding.ASCII.GetBytes("OLD")) });
                WriteSyntheticPkg(updatePkg, "UP0082-NPUB30601_00-GAME0000000000000", riv, isPatch: true,
                    new[] { ("PARAM.SFO", updateSfo), ("USRDIR/EBOOT.BIN", Encoding.ASCII.GetBytes("NEW")) });

                using (var pkg = Ps3Pkg.Open(basePkg))
                    pkg.ExtractTo(outDir, CancellationToken.None);
                using (var pkg = Ps3Pkg.Open(updatePkg))
                    pkg.ExtractTo(outDir, CancellationToken.None, protectBootableRootParamSfo: true);

                // The bootable update replaced both the SFO (new version) and the patched EBOOT.
                Assert.Equal(updateSfo, File.ReadAllBytes(Path.Combine(outDir, "PARAM.SFO")));
                Assert.Equal(Encoding.ASCII.GetBytes("NEW"), File.ReadAllBytes(Path.Combine(outDir, "USRDIR", "EBOOT.BIN")));
            }
            finally
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                if (File.Exists(basePkg)) File.Delete(basePkg);
                if (File.Exists(updatePkg)) File.Delete(updatePkg);
            }
        }

        [Fact]
        public void Ps3Pkg_ReadsPatchFlagFromMetadata()
        {
            var riv = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            var pkgPath = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            try
            {
                WriteSyntheticPkg(pkgPath, "UP9000-NPUA80007_00-GSOCOMCONF000001", riv, isPatch: true,
                    new[] { ("PARAM.SFO", BuildSfo(new[] { ("CATEGORY", "HG"), ("APP_VER", "01.60") })) });

                using (var pkg = Ps3Pkg.Open(pkgPath))
                {
                    Assert.True(pkg.IsPatch);
                }
            }
            finally
            {
                if (File.Exists(pkgPath))
                    File.Delete(pkgPath);
            }
        }

        [Theory]
        // DRM Type Network(1)/Local(2) require a RAP; Free(3)/no-DRM(0) don't. -1 = no metadata entry.
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(0, false)]
        public void Ps3Pkg_ReadsDrmTypeAndRequiresLicense(int drmType, bool expectedRequiresLicense)
        {
            var riv = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();
            var pkgPath = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            try
            {
                WriteSyntheticPkg(pkgPath, "UP0102-NPUB30024_00-1942XXXXX0123456", riv, isPatch: false,
                    new[] { ("PARAM.SFO", BuildSfo(new[] { ("CATEGORY", "HG"), ("APP_VER", "01.00") })) },
                    drmType: drmType);

                using (var pkg = Ps3Pkg.Open(pkgPath))
                {
                    Assert.Equal(drmType, pkg.DrmType);
                    Assert.Equal(expectedRequiresLicense, pkg.RequiresLicense);
                }
            }
            finally
            {
                if (File.Exists(pkgPath))
                    File.Delete(pkgPath);
            }
        }

        [Fact]
        public void Ps3Pkg_DrmTypeIsMinusOneWhenMetadataEntryAbsent()
        {
            // No DRM Type record emitted (only the content-flags id 0x03) ⇒ unknown, treated as not requiring.
            var riv = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();
            var pkgPath = Path.Combine(Path.GetTempPath(), "eltest-" + Guid.NewGuid().ToString("N") + ".pkg");
            try
            {
                WriteSyntheticPkg(pkgPath, "UP0102-NPUB30024_00-1942XXXXX0123456", riv, isPatch: false,
                    new[] { ("PARAM.SFO", BuildSfo(new[] { ("CATEGORY", "HG") })) });

                using (var pkg = Ps3Pkg.Open(pkgPath))
                {
                    Assert.Equal(-1, pkg.DrmType);
                    Assert.False(pkg.RequiresLicense);
                }
            }
            finally
            {
                if (File.Exists(pkgPath))
                    File.Delete(pkgPath);
            }
        }

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        public void Ps3FileInfo_RequiresLicenseMirrorsDrmType(int drmType, bool expected)
        {
            Assert.Equal(expected, new Ps3FileInfo { DrmType = drmType }.RequiresLicense);
        }

        #endregion

        #region disc ISO PARAM.SFO

        [Fact]
        public void Ps3Iso_ReadsParamSfoFromUnencryptedRegion()
        {
            var sfoBytes = BuildSfo(new[]
            {
                ("CATEGORY", "DG"),
                ("TITLE", "Demon's Souls"),
                ("TITLE_ID", "BLES00932"),
                ("APP_VER", "01.01"),
            });

            var iso = BuildPs3Iso(sfoBytes);

            using (var ms = new MemoryStream(iso, false))
            {
                Assert.True(Ps3Iso.TryReadParamSfo(ms, out var sfo));
                Assert.Equal("Demon's Souls", sfo.Title);
                Assert.Equal("BLES00932", sfo.TitleId);
                Assert.Equal("01.01", sfo.AppVer);
                Assert.Equal("DG", sfo.Category);
            }
        }

        [Fact]
        public void Ps3Iso_ReturnsFalseWhenSfoBytesAreNotValid()
        {
            // Simulates PARAM.SFO landing in an encrypted region: the directory walk locates it, but the
            // bytes aren't a valid SFO — the reader must fail cleanly so the scanner can fall back.
            var garbage = new byte[256];
            for (int i = 0; i < garbage.Length; i++)
                garbage[i] = (byte)(i * 7 + 1);

            var iso = BuildPs3Iso(garbage);

            using (var ms = new MemoryStream(iso, false))
            {
                Assert.False(Ps3Iso.TryReadParamSfo(ms, out var sfo));
                Assert.Null(sfo);
            }
        }

        [Fact]
        public void Ps3Iso_ReturnsFalseWhenNotIso9660()
        {
            var notAnIso = new byte[64 * 1024]; // no "CD001" at sector 16
            using (var ms = new MemoryStream(notAnIso, false))
            {
                Assert.False(Ps3Iso.TryReadParamSfo(ms, out var sfo));
                Assert.Null(sfo);
            }
        }

        #endregion

        #region vfs.yml parsing

        [Theory]
        [InlineData("/dev_hdd0/: D:/RPCS3/dev_hdd0\n/dev_hdd1/: \"\"\n", "D:/RPCS3/dev_hdd0")]
        [InlineData("/dev_hdd0/: \"/mnt/games/rpcs3/dev_hdd0\"\n/dev_hdd1/: \"\"\n", "/mnt/games/rpcs3/dev_hdd0")]
        // Windows path with escaped backslashes inside double-quoted YAML value
        [InlineData("/dev_hdd0/: \"D:\\\\RPCS3\\\\dev_hdd0\"\n", "D:\\RPCS3\\dev_hdd0")]
        public void ParseDevHdd0FromVfsYml_ReturnsCustomPath(string yaml, string expected)
        {
            Assert.Equal(expected, Rpcs3Emulator.ParseDevHdd0FromVfsYml(yaml));
        }

        [Theory]
        [InlineData("/dev_hdd0/: \"\"\n/dev_hdd1/: \"\"\n")]   // empty value = use default
        [InlineData("/dev_hdd1/: someplace\n")]                  // /dev_hdd0/ key absent
        [InlineData("")]                                          // empty file
        [InlineData(null)]                                        // null input
        public void ParseDevHdd0FromVfsYml_ReturnsNullForDefault(string yaml)
        {
            Assert.Null(Rpcs3Emulator.ParseDevHdd0FromVfsYml(yaml));
        }

        [Fact]
        public void GetDevHdd0_ReadsVfsYmlNextToExe()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-vfs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var customHdd0 = Path.Combine(dir, "custom_hdd0");
                File.WriteAllText(Path.Combine(dir, "vfs.yml"),
                    $"/dev_hdd0/: {customHdd0}\n/dev_hdd1/: \"\"\n");

                Assert.Equal(customHdd0, Rpcs3Emulator.GetDevHdd0(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void GetDevHdd0_ReadsVfsYmlFromConfigSubdir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-vfs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            try
            {
                var customHdd0 = Path.Combine(dir, "custom_hdd0");
                File.WriteAllText(Path.Combine(dir, "config", "vfs.yml"),
                    $"/dev_hdd0/: {customHdd0}\n");

                Assert.Equal(customHdd0, Rpcs3Emulator.GetDevHdd0(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void GetDevHdd0_FallsBackToDefaultWhenNoVfsYml()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-vfs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Equal(Path.Combine(dir, "dev_hdd0"), Rpcs3Emulator.GetDevHdd0(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void GetDevHdd0_FallsBackToDefaultWhenVfsYmlHasEmptyEntry()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-vfs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "vfs.yml"),
                    "/dev_hdd0/: \"\"\n/dev_hdd1/: \"\"\n");

                Assert.Equal(Path.Combine(dir, "dev_hdd0"), Rpcs3Emulator.GetDevHdd0(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        #endregion

        #region helpers

        // Builds a minimal PARAM.SFO blob with UTF-8 string entries (data_fmt 0x0204).
        private static byte[] BuildSfo(IReadOnlyList<(string Key, string Value)> entries)
        {
            // Keys are stored sorted in real SFOs but the parser is order-independent.
            var keyBytes = new List<byte>();
            var keyOffsets = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                keyOffsets[i] = keyBytes.Count;
                keyBytes.AddRange(Encoding.UTF8.GetBytes(entries[i].Key));
                keyBytes.Add(0);
            }

            var dataBytes = new List<byte>();
            var dataOffsets = new int[entries.Count];
            var dataLens = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                dataOffsets[i] = dataBytes.Count;
                var v = Encoding.UTF8.GetBytes(entries[i].Value);
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
                w.Write(new byte[] { 0x00, (byte)'P', (byte)'S', (byte)'F' }); // magic
                w.Write((uint)0x00000101); // version 1.1
                w.Write((uint)keyTableStart);
                w.Write((uint)dataTableStart);
                w.Write((uint)entries.Count);

                for (int i = 0; i < entries.Count; i++)
                {
                    w.Write((ushort)keyOffsets[i]);
                    w.Write((ushort)0x0204); // utf8, null-terminated
                    w.Write((uint)dataLens[i]);
                    w.Write((uint)dataLens[i]);
                    w.Write((uint)dataOffsets[i]);
                }

                w.Write(keyBytes.ToArray());
                w.Write(dataBytes.ToArray());
                return ms.ToArray();
            }
        }

        // Builds a finalized retail PS3 PKG whose encrypted data segment holds the given files, with a
        // minimal plaintext metadata block (content-flags id 0x03, patch bit 0x10 set per isPatch). When
        // drmType >= 0 a DRM Type record (id 0x01) is emitted too, so RAP-required detection can be exercised.
        private static void WriteSyntheticPkg(string path, string contentId, byte[] riv, bool isPatch,
            IReadOnlyList<(string Name, byte[] Data)> files, int drmType = -1)
        {
            // Lay out the plaintext data segment: file records, then names, then file data.
            int recordsSize = files.Count * 0x20;
            var names = files.Select(f => Encoding.UTF8.GetBytes(f.Name)).ToArray();

            int cursor = recordsSize;
            var nameOffsets = new int[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                nameOffsets[i] = cursor;
                cursor += names[i].Length;
            }
            var dataOffsets = new int[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                dataOffsets[i] = cursor;
                cursor += files[i].Data.Length;
            }
            int segmentSize = cursor;

            var segment = new byte[segmentSize];
            for (int i = 0; i < files.Count; i++)
            {
                int o = i * 0x20;
                WriteU32BE(segment, o + 0x00, (uint)nameOffsets[i]);
                WriteU32BE(segment, o + 0x04, (uint)names[i].Length);
                WriteU64BE(segment, o + 0x08, (ulong)dataOffsets[i]);
                WriteU64BE(segment, o + 0x10, (ulong)files[i].Data.Length);
                WriteU32BE(segment, o + 0x18, 1); // flags: normal file
                WriteU32BE(segment, o + 0x1C, 0);

                Buffer.BlockCopy(names[i], 0, segment, nameOffsets[i], names[i].Length);
                Buffer.BlockCopy(files[i].Data, 0, segment, dataOffsets[i], files[i].Data.Length);
            }

            var encryptedSegment = CtrTransform(segment, GpkgKey, riv);

            // Metadata records (each: u32 id, u32 size, payload). Always id 0x03 (content flags, patch bit per
            // isPatch); optionally id 0x01 (DRM Type) when drmType >= 0. Each record here is 4-byte payload.
            const int metaOffset = 0x80;
            var records = new List<byte[]>();
            if (drmType >= 0)
            {
                var drm = new byte[12];
                WriteU32BE(drm, 0x00, 0x01);
                WriteU32BE(drm, 0x04, 4);
                WriteU32BE(drm, 0x08, (uint)drmType);
                records.Add(drm);
            }
            var flags = new byte[12];
            WriteU32BE(flags, 0x00, 0x03);
            WriteU32BE(flags, 0x04, 4);
            WriteU32BE(flags, 0x08, isPatch ? 0x5Eu : 0x4Eu);
            records.Add(flags);

            var meta = records.SelectMany(r => r).ToArray();

            int dataOffset = metaOffset + meta.Length;
            var header = new byte[metaOffset];
            WriteU32BE(header, 0x00, 0x7F504B47); // magic
            WriteU16BE(header, 0x04, 0x8000);     // revision: finalized retail
            WriteU16BE(header, 0x06, 0x0001);     // type: PS3
            WriteU32BE(header, 0x08, metaOffset);  // metadata_offset
            WriteU32BE(header, 0x0C, (uint)records.Count); // metadata_count
            WriteU32BE(header, 0x14, (uint)files.Count); // item_count
            WriteU64BE(header, 0x18, (ulong)(dataOffset + encryptedSegment.Length)); // total_size
            WriteU64BE(header, 0x20, (ulong)dataOffset); // data_offset
            WriteU64BE(header, 0x28, (ulong)encryptedSegment.Length); // data_size
            var cid = Encoding.ASCII.GetBytes(contentId);
            Buffer.BlockCopy(cid, 0, header, 0x30, cid.Length);
            Buffer.BlockCopy(riv, 0, header, 0x70, 16); // pkg_data_riv (AES-CTR IV); 0x60 is the QA digest

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(header, 0, header.Length);
                fs.Write(meta, 0, meta.Length);
                fs.Write(encryptedSegment, 0, encryptedSegment.Length);
            }
        }

        // AES-128-CTR over a segment starting at counter block 0 (= riv). Symmetric: same routine
        // encrypts and decrypts.
        private static byte[] CtrTransform(byte[] data, byte[] key, byte[] riv)
        {
            var outp = new byte[data.Length];
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = key;
                using (var enc = aes.CreateEncryptor())
                {
                    int blocks = (data.Length + 15) / 16;
                    var ks = new byte[blocks * 16];
                    for (int b = 0; b < blocks; b++)
                        WriteCounter(ks, b * 16, riv, (ulong)b);
                    enc.TransformBlock(ks, 0, blocks * 16, ks, 0);
                    for (int i = 0; i < data.Length; i++)
                        outp[i] = (byte)(data[i] ^ ks[i]);
                }
            }
            return outp;
        }

        private static void WriteCounter(byte[] dst, int dstOff, byte[] riv, ulong blockIndex)
        {
            Buffer.BlockCopy(riv, 0, dst, dstOff, 16);
            int idx = dstOff + 15;
            ulong add = blockIndex;
            uint carry = 0;
            while (idx >= dstOff && (add != 0 || carry != 0))
            {
                ulong val = (ulong)dst[idx] + (byte)(add & 0xFF) + carry;
                dst[idx] = (byte)val;
                carry = (uint)(val >> 8);
                add >>= 8;
                idx--;
            }
        }

        // Builds a minimal ISO9660 image with a /PS3_GAME/PARAM.SFO file holding sfoBytes. Mirrors the
        // unencrypted region of a PS3 disc: PVD at sector 16, root dir at 17, PS3_GAME dir at 18, SFO at 19.
        private static byte[] BuildPs3Iso(byte[] sfoBytes)
        {
            const int sector = 2048;
            const long rootLba = 17, gameLba = 18, sfoLba = 19;
            const int totalSectors = 20;
            var iso = new byte[totalSectors * sector];

            // Primary Volume Descriptor at sector 16.
            int pvd = 16 * sector;
            iso[pvd + 0] = 1; // type: primary
            iso[pvd + 1] = (byte)'C'; iso[pvd + 2] = (byte)'D'; iso[pvd + 3] = (byte)'0';
            iso[pvd + 4] = (byte)'0'; iso[pvd + 5] = (byte)'1';
            iso[pvd + 6] = 1; // version
            var rootRec = BuildDirRecord("\0", rootLba, sector, isDir: true);
            Buffer.BlockCopy(rootRec, 0, iso, pvd + 156, rootRec.Length);

            WriteDirSector(iso, (int)rootLba * sector,
                BuildDirRecord("\0", rootLba, sector, true),     // .
                BuildDirRecord("\x01", rootLba, sector, true),   // ..
                BuildDirRecord("PS3_GAME", gameLba, sector, true));

            WriteDirSector(iso, (int)gameLba * sector,
                BuildDirRecord("\0", gameLba, sector, true),     // .
                BuildDirRecord("\x01", rootLba, sector, true),   // ..
                BuildDirRecord("PARAM.SFO;1", sfoLba, sfoBytes.Length, false));

            Buffer.BlockCopy(sfoBytes, 0, iso, (int)sfoLba * sector, Math.Min(sfoBytes.Length, sector));
            return iso;
        }

        private static void WriteDirSector(byte[] iso, int offset, params byte[][] records)
        {
            int o = offset;
            foreach (var r in records)
            {
                Buffer.BlockCopy(r, 0, iso, o, r.Length);
                o += r.Length;
            }
        }

        // Builds an ISO9660 directory record. id "\0"/"\x01" are the self/parent entries.
        private static byte[] BuildDirRecord(string id, long lba, long len, bool isDir)
        {
            byte[] idBytes = id == "\0" ? new byte[] { 0x00 }
                : id == "\x01" ? new byte[] { 0x01 }
                : Encoding.ASCII.GetBytes(id);

            int recLen = 33 + idBytes.Length;
            if ((recLen & 1) != 0)
                recLen++; // records are padded to an even length

            var rec = new byte[recLen];
            rec[0] = (byte)recLen;
            WriteU32LE(rec, 2, (uint)lba);
            WriteU32BE(rec, 6, (uint)lba);
            WriteU32LE(rec, 10, (uint)len);
            WriteU32BE(rec, 14, (uint)len);
            rec[25] = (byte)(isDir ? 0x02 : 0x00); // file flags
            WriteU16LE(rec, 28, 1);                // volume sequence number (both-endian)
            WriteU16BE(rec, 30, 1);
            rec[32] = (byte)idBytes.Length;
            Buffer.BlockCopy(idBytes, 0, rec, 33, idBytes.Length);
            return rec;
        }

        private static void WriteU16LE(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static void WriteU16BE(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v >> 8);
            b[o + 1] = (byte)v;
        }

        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        private static void WriteU64BE(byte[] b, int o, ulong v)
        {
            WriteU32BE(b, o, (uint)(v >> 32));
            WriteU32BE(b, o + 4, (uint)v);
        }

        #endregion
    }
}
