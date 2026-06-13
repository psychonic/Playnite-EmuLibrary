using EmuLibrary.RomTypes;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    public class DiscFilterTests
    {
        [Theory]
        // Names carrying a disc/disk marker are excluded (case-insensitive, "Disc" or "Disk").
        [InlineData("Final Fantasy VII (USA) (Disc 1).bin", true)]
        [InlineData("Final Fantasy VII (USA) (Disc 2).bin", true)]
        [InlineData("Some Game (Disc 10).cue", true)]
        [InlineData("Some Game (Disk 2).img", true)]
        [InlineData("Some Game (DISC 3).iso", true)]
        // No marker -> not excluded.
        [InlineData("Chrono Trigger (USA).sfc", false)]
        [InlineData("Some Game (Demo).zip", false)]
        // Marker must be a parenthesized "Disc/Disk <number>"; near-misses are not excluded.
        [InlineData("Disc Golf (USA).zip", false)]      // no parenthesis before Disc
        [InlineData("Some Game (Disc One).bin", false)] // word, not a digit
        public void IsExcludedDisc_MatchesDiscMarkers(string name, bool expectedExcluded)
        {
            Assert.Equal(expectedExcluded, DiscFilter.IsExcludedDisc(name));
        }
    }
}
