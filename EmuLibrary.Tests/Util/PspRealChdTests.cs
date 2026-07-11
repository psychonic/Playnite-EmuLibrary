using EmuLibrary.Util.Psp;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.Util
{
    // Real-fixture validation for the CHD reader: opens genuine chdman-produced .chd files and reads the
    // DISC_ID back through PspImageReader. The synthetic PspImageReaderTests cross-check the decoder against an
    // independent encoder, but both are our own code and could share a misreading of the format; these tests
    // close that gap by parsing files a real chdman actually wrote (container header + compressed hunk map +
    // codec stream).
    //
    // No such fixture is committed (real PSP CHDs are ~1.8 GB), so the tests are skipped unless a directory of
    // .chd files is provided — set EL_PSP_CHD_DIR, or have the default PSP share reachable. When present they
    // sample a few files so the run stays quick over SMB (the reader only decodes the hunks near the ISO9660
    // volume start, so each file is a few MB of reads, not a full download).
    public class PspRealChdTests
    {
        // Default is the known PSP collection (all real chdman v5 zstd DVD images); override with EL_PSP_CHD_DIR.
        private const string DefaultChdDir = @"\\omv03\downloads\psp";
        private const int SampleCount = 5;

        [Fact]
        public void RealChds_ReadDiscId()
        {
            var dir = Environment.GetEnvironmentVariable("EL_PSP_CHD_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = DefaultChdDir;

            // Environment-gated: with no reachable fixture directory the test passes trivially (xunit 2.x has no
            // runtime Assert.Skip). It does real work only when a real-CHD directory is actually present.
            if (!SafeDirExists(dir))
                return;

            string[] chds;
            try
            {
                chds = Directory.EnumerateFiles(dir, "*.chd", SearchOption.TopDirectoryOnly)
                    .Take(SampleCount).ToArray();
            }
            catch (IOException)
            {
                return; // share vanished mid-run; nothing to validate
            }

            if (chds.Length == 0)
                return;

            foreach (var chd in chds)
            {
                Assert.True(PspImageReader.TryReadTitleId(chd, out var id),
                    $"Failed to read a title id from real CHD: {Path.GetFileName(chd)}");
                Assert.False(string.IsNullOrWhiteSpace(id),
                    $"Read an empty title id from real CHD: {Path.GetFileName(chd)}");
            }
        }

        // Directory.Exists on an unreachable SMB path can hang/throw; guard it.
        private static bool SafeDirExists(string dir)
        {
            try { return Directory.Exists(dir); }
            catch { return false; }
        }
    }
}
