using EmuLibrary.RomTypes;
using System.IO;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    public class LoadFileSelectorTests
    {
        // Mirrors how the scanner extracts an extension: lowercased, no leading dot, "" for none.
        private static string Select(string[] files, params string[] exts)
            => LoadFileSelector.Select(files, f => Path.GetExtension(f).TrimStart('.').ToLower(), exts);

        [Fact]
        public void PicksHighestPriorityExtension_RegardlessOfInputPosition()
        {
            // bin appears first in the file list, but cue outranks it, so cue wins.
            var result = Select(new[] { "song.bin", "disc.cue", "playlist.m3u" }, "m3u", "cue", "bin");
            Assert.Equal("playlist.m3u", result);
        }

        [Fact]
        public void FallsThroughToNextExtension_WhenHigherPriorityHasNoMatch()
        {
            var result = Select(new[] { "track01.bin", "disc.cue" }, "m3u", "cue", "bin");
            Assert.Equal("disc.cue", result);
        }

        [Fact]
        public void WithinSameExtension_KeepsInputOrder_NoAlphabeticalSort()
        {
            // "b.cue" precedes "a.cue" in input, so it wins despite not being alphabetically first.
            var result = Select(new[] { "b.cue", "a.cue" }, "cue");
            Assert.Equal("b.cue", result);
        }

        [Fact]
        public void ReturnsNull_WhenNothingMatches()
        {
            Assert.Null(Select(new[] { "readme.txt", "art.png" }, "cue", "bin"));
        }

        [Fact]
        public void NonePseudoExtension_MatchesExtensionlessFiles()
        {
            Assert.Equal("DISC", Select(new[] { "DISC", "notes.txt" }, "<none>"));
        }

        [Fact]
        public void ExtensionlessFile_NotSelected_WhenNoneNotRequested()
        {
            Assert.Null(Select(new[] { "DISC" }, "cue", "bin"));
        }

        [Fact]
        public void ReturnsNull_ForEmptyFileList()
        {
            Assert.Null(Select(new string[0], "cue"));
        }
    }
}
