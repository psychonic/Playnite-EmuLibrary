using EmuLibrary.Installs;
using Xunit;

namespace EmuLibrary.Tests.Installs
{
    // Phase 3: the byte-progress status line. ProgressFormat is pure, so pin the pieces and the composed line.
    public class ProgressFormatTests
    {
        [Theory]
        [InlineData(512, "512 B/s")]
        [InlineData(1024, "1 KB/s")]
        [InlineData(1536, "1.5 KB/s")]
        [InlineData(18L * 1024 * 1024 + 400 * 1024, "18.4 MB/s")]
        [InlineData(2L * 1024 * 1024 * 1024, "2 GB/s")]
        public void Rate_FormatsWithBinaryUnits(double bytesPerSec, string expected)
        {
            Assert.Equal(expected, ProgressFormat.Rate(bytesPerSec));
        }

        [Theory]
        [InlineData(0, "0s")]
        [InlineData(0.4, "0s")]
        [InlineData(42, "42s")]
        [InlineData(200, "3m 20s")]
        [InlineData(3840, "1h 04m")]
        public void Eta_FormatsCompactly(double seconds, string expected)
        {
            Assert.Equal(expected, ProgressFormat.Eta(seconds));
        }

        [Theory]
        [InlineData("Installing base…", "Installing base")]
        [InlineData("Copying…", "Copying")]
        [InlineData("Finishing…", "Finishing")]
        [InlineData("", "Installing")]
        [InlineData(null, "Installing")]
        public void StageLabel_StripsTrailingEllipsisAndFallsBack(string input, string expected)
        {
            Assert.Equal(expected, ProgressFormat.StageLabel(input));
        }

        [Fact]
        public void Line_WithRate_IncludesPercentRateAndEta()
        {
            // 50 MiB of 100 MiB at 10 MiB/s -> 50%, "10 MB/s", 5s remaining.
            var line = ProgressFormat.Line("Installing base", 50L * 1024 * 1024, 100L * 1024 * 1024, 10L * 1024 * 1024);
            Assert.Equal("Installing base 50% · 10 MB/s · 5s left", line);
        }

        [Fact]
        public void Line_WithoutRate_IsJustStageAndPercent()
        {
            var line = ProgressFormat.Line("Copying", 25, 100, null);
            Assert.Equal("Copying 25%", line);
        }

        [Fact]
        public void Line_AtCompletion_OmitsEta()
        {
            var line = ProgressFormat.Line("Installing base", 100, 100, 5L * 1024 * 1024);
            Assert.Equal("Installing base 100% · 5 MB/s", line);
        }
    }
}
