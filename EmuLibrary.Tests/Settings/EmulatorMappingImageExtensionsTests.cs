using EmuLibrary.Settings;
using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.Settings
{
    // Regression coverage for issue #13: enabling/saving a mapping for a built-in emulator (RPCS3)
    // crashed Playnite with a NullReferenceException out of EmulatorMapping.ImageExtensionsLower,
    // because the built-in emulator/profile lookup was unguarded. The resolution must degrade to null
    // (which VerifySettings reports as a validation error) instead of throwing.
    public class EmulatorMappingImageExtensionsTests
    {
        private const string ConfigId = "rpcs3";

        private static List<EmulatorDefinition> Emulators(string id, EmulatorDefinitionProfile profile) =>
            new List<EmulatorDefinition>
            {
                new EmulatorDefinition
                {
                    Id = id,
                    Profiles = profile == null ? new List<EmulatorDefinitionProfile>()
                        : new List<EmulatorDefinitionProfile> { profile },
                },
            };

        [Fact]
        public void NullEmulatorCollection_ReturnsNull()
        {
            Assert.Null(EmulatorMapping.ResolveBuiltInImageExtensionsLower(null, ConfigId, "Default"));
        }

        [Fact]
        public void UnknownEmulatorConfigId_ReturnsNull()
        {
            var emulators = Emulators("something-else",
                new EmulatorDefinitionProfile { Name = "Default", ImageExtensions = new List<string> { "iso" } });

            Assert.Null(EmulatorMapping.ResolveBuiltInImageExtensionsLower(emulators, ConfigId, "Default"));
        }

        [Fact]
        public void UnknownProfileName_ReturnsNull()
        {
            // The exact crash path: emulator resolves, but the profile name doesn't match, so the
            // inner FirstOrDefault returns null and ImageExtensions was dereferenced on null.
            var emulators = Emulators(ConfigId,
                new EmulatorDefinitionProfile { Name = "Default", ImageExtensions = new List<string> { "iso" } });

            Assert.Null(EmulatorMapping.ResolveBuiltInImageExtensionsLower(emulators, ConfigId, "Nonexistent"));
        }

        [Fact]
        public void ProfileWithNullImageExtensions_ReturnsNull()
        {
            // RPCS3's built-in profile declares no image extensions.
            var emulators = Emulators(ConfigId,
                new EmulatorDefinitionProfile { Name = "Default", ImageExtensions = null });

            Assert.Null(EmulatorMapping.ResolveBuiltInImageExtensionsLower(emulators, ConfigId, "Default"));
        }

        [Fact]
        public void MatchingProfile_NormalizesExtensions()
        {
            var emulators = Emulators(ConfigId,
                new EmulatorDefinitionProfile
                {
                    Name = "Default",
                    ImageExtensions = new List<string> { " ISO ", "", "Bin", null },
                });

            var result = EmulatorMapping.ResolveBuiltInImageExtensionsLower(emulators, ConfigId, "Default");

            Assert.NotNull(result);
            Assert.Equal(new[] { "iso", "bin" }, result.ToArray());
        }

        [Fact]
        public void NormalizeImageExtensions_Null_ReturnsNull()
        {
            Assert.Null(EmulatorMapping.NormalizeImageExtensions(null));
        }

        [Fact]
        public void NormalizeImageExtensions_TrimsLowercasesAndDropsEmpty()
        {
            var result = EmulatorMapping.NormalizeImageExtensions(new[] { "  NES ", "", "Zip", null });

            Assert.Equal(new[] { "nes", "zip" }, result.ToArray());
        }
    }
}
