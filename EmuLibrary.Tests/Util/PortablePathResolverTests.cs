using EmuLibrary.Util;
using Playnite.SDK;
using Xunit;

namespace EmuLibrary.Tests.Util
{
    public class PortablePathResolverTests
    {
        private const string AppPath = @"C:\Playnite";

        [Fact]
        public void NonPortable_ReturnsPathUnchanged()
        {
            // When not portable, the stored path is absolute and used verbatim - even if it happens to
            // contain the variable, it is not expanded.
            var stored = ExpandableVariables.PlayniteDirectory + @"\roms";
            Assert.Equal(stored, PortablePathResolver.Resolve(stored, isPortable: false, applicationPath: AppPath));
        }

        [Fact]
        public void Portable_ExpandsPlayniteDirVariable()
        {
            var stored = ExpandableVariables.PlayniteDirectory + @"\roms\snes";
            Assert.Equal(@"C:\Playnite\roms\snes", PortablePathResolver.Resolve(stored, isPortable: true, applicationPath: AppPath));
        }

        [Fact]
        public void Portable_PathWithoutVariable_IsUnchanged()
        {
            Assert.Equal(@"D:\roms", PortablePathResolver.Resolve(@"D:\roms", isPortable: true, applicationPath: AppPath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NullPath_ReturnsNull(bool isPortable)
        {
            Assert.Null(PortablePathResolver.Resolve(null, isPortable, AppPath));
        }
    }
}
