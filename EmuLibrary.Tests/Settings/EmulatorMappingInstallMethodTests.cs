using EmuLibrary.Settings;
using EmuLibrary.Util.FileCopier;
using Newtonsoft.Json;
using Xunit;

namespace EmuLibrary.Tests.Settings
{
    // Guards the persistence contract for the install-method dropdown (issue #2): Copy must be enum value 0
    // so mappings saved before the field existed deserialize to Copy, and chosen values must round-trip.
    public class EmulatorMappingInstallMethodTests
    {
        [Fact]
        public void Copy_IsDefaultEnumValue()
        {
            Assert.Equal(0, (int)InstallMethod.Copy);
            Assert.Equal(InstallMethod.Copy, default(InstallMethod));
            Assert.Equal(InstallMethod.Copy, new EmulatorMapping().InstallMethod);
        }

        [Fact]
        public void ConfigWithoutInstallMethod_DeserializesToCopy()
        {
            // Mappings persisted before this field existed have no InstallMethod key.
            var mapping = JsonConvert.DeserializeObject<EmulatorMapping>("{ \"SourcePath\": \"X\" }");

            Assert.Equal(InstallMethod.Copy, mapping.InstallMethod);
        }

        [Theory]
        [InlineData(InstallMethod.Copy)]
        [InlineData(InstallMethod.Symlink)]
        [InlineData(InstallMethod.Hardlink)]
        public void InstallMethod_RoundTripsThroughJson(InstallMethod method)
        {
            var json = JsonConvert.SerializeObject(new EmulatorMapping { InstallMethod = method });
            var restored = JsonConvert.DeserializeObject<EmulatorMapping>(json);

            Assert.Equal(method, restored.InstallMethod);
        }
    }
}
