using EmuLibrary.RomTypes;
using EmuLibrary.Util;
using EmuLibrary.Util.FileCopier;
using Newtonsoft.Json;
using Playnite.SDK.Models;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Serialization;

namespace EmuLibrary.Settings
{
    public class EmulatorMapping : ObservableObject
    {
        public EmulatorMapping()
        {
            MappingId = Guid.NewGuid();
        }

        public Guid MappingId { get; set; }

        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool Enabled { get; set; }

        [JsonIgnore]
        public Emulator Emulator
        {
            get => AvailableEmulators.FirstOrDefault(e => e.Id == EmulatorId);
            set { EmulatorId = value.Id; }
        }
        public Guid EmulatorId { get; set; }

        [JsonIgnore]
        public EmulatorProfile EmulatorProfile
        {
            get => Emulator?.SelectableProfiles.FirstOrDefault(p => p.Id == EmulatorProfileId);
            set { EmulatorProfileId = value.Id; }
        }
        public string EmulatorProfileId { get; set; }

        [JsonIgnore]
        public EmulatedPlatform Platform
        {
            get => AvailablePlatforms.FirstOrDefault(p => p.Id == PlatformId);
            set { PlatformId = value.Id; }
        }
        public string PlatformId { get; set; }

        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }

        private RomType _romType;
        public RomType RomType
        {
            get => _romType;
            set => SetValue(ref _romType, value, new[] { nameof(RomType), nameof(SupportsDestinationPath), nameof(SupportsInstallMethod) });
        }

        // How the ROM is placed at the destination: Copy (default), Symlink, or Hardlink (issue #2). Only
        // honored for RomTypes that support linking (see SupportsInstallMethod); otherwise the install always
        // copies. Copy is enum value 0, so configs saved before this field existed deserialize to Copy.
        [DefaultValue(InstallMethod.Copy)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public InstallMethod InstallMethod { get; set; }

        // False for RomTypes that install into the emulator (e.g. Yuzu) and so don't use DestinationPath. The
        // settings UI binds this to disable the destination column, and VerifySettings skips validating it.
        [JsonIgnore]
        [XmlIgnore]
        public bool SupportsDestinationPath => Settings.Instance?.EmuLibrary?.GetScanner(RomType)?.RequiresDestinationPath ?? true;

        // True only for RomTypes that copy the source verbatim and so can link instead (SingleFile, MultiFile).
        // The settings UI binds this to disable the Install Method column for types that always copy.
        [JsonIgnore]
        [XmlIgnore]
        public bool SupportsInstallMethod => Settings.Instance?.EmuLibrary?.GetScanner(RomType)?.SupportsInstallLinking ?? false;

        public static IEnumerable<Emulator> AvailableEmulators => Settings.Instance.PlayniteAPI.Database.Emulators.OrderBy(x => x.Name);

        [JsonIgnore]
        public IEnumerable<EmulatorProfile> AvailableProfiles => Emulator?.SelectableProfiles;

        [JsonIgnore]
        public IEnumerable<EmulatedPlatform> AvailablePlatforms
        {
            get
            {
                var playnite = Settings.Instance.PlayniteAPI;
                HashSet<string> validPlatforms;

                if (EmulatorProfile is CustomEmulatorProfile)
                {
                    var customProfile = EmulatorProfile as CustomEmulatorProfile;
                    validPlatforms = new HashSet<string>(playnite.Database.Platforms.Where(p => customProfile.Platforms.Contains(p.Id)).Select(p => p.SpecificationId));
                }
                else if (EmulatorProfile is BuiltInEmulatorProfile)
                {
                    var builtInProfile = (EmulatorProfile as BuiltInEmulatorProfile);
                    validPlatforms = new HashSet<string>(
                        playnite.Emulation.Emulators
                        .FirstOrDefault(e => e.Id == Emulator.BuiltInConfigId)?
                        .Profiles
                        .FirstOrDefault(p => p.Name == builtInProfile.Name)?
                        .Platforms
                        );
                }
                else
                {
                    validPlatforms = new HashSet<string>();
                }

                return Settings.Instance.PlayniteAPI.Emulation.Platforms.Where(p => validPlatforms.Contains(p.Id));
            }
        }

        [JsonIgnore]
        [XmlIgnore]
        public string DestinationPathResolved
        {
            get
            {
                var playnite = Settings.Instance.PlayniteAPI;
                return PortablePathResolver.Resolve(DestinationPath, playnite.Paths.IsPortable, playnite.Paths.ApplicationPath);
            }
        }

        [JsonIgnore]
        [XmlIgnore]
        public string EmulatorBasePath => Emulator?.InstallDir;

        [JsonIgnore]
        [XmlIgnore]
        public string EmulatorBasePathResolved
        {
            get
            {
                var playnite = Settings.Instance.PlayniteAPI;
                return PortablePathResolver.Resolve(Emulator?.InstallDir, playnite.Paths.IsPortable, playnite.Paths.ApplicationPath);
            }
        }

        [JsonIgnore]
        public IEnumerable<string> ImageExtensionsLower
        {
            get
            {
                if (EmulatorProfile is CustomEmulatorProfile customProfile)
                {
                    return NormalizeImageExtensions(customProfile.ImageExtensions);
                }
                else if (EmulatorProfile is BuiltInEmulatorProfile builtInProfile)
                {
                    return ResolveBuiltInImageExtensionsLower(
                        Settings.Instance?.PlayniteAPI.Emulation.Emulators,
                        Emulator.BuiltInConfigId,
                        builtInProfile.Name);
                }
                else
                {
                    throw new NotImplementedException("Unknown emulator profile type.");
                }
            }
        }

        // Null-safe resolution of a built-in emulator profile's image extensions. Returns null (rather
        // than throwing) when the built-in emulator or its profile can't be resolved, or when the profile
        // declares no extensions - e.g. RPCS3, whose built-in profile lists none. VerifySettings treats a
        // null/empty result as "no extensions specified" and surfaces it as a validation error. Without
        // this the unguarded lookup threw a NullReferenceException that crashed Playnite (issue #13).
        internal static IEnumerable<string> ResolveBuiltInImageExtensionsLower(
            IEnumerable<EmulatorDefinition> builtInEmulators, string builtInConfigId, string profileName)
        {
            return NormalizeImageExtensions(
                builtInEmulators?
                .FirstOrDefault(e => e.Id == builtInConfigId)?
                .Profiles.FirstOrDefault(p => p.Name == profileName)?
                .ImageExtensions);
        }

        internal static IEnumerable<string> NormalizeImageExtensions(IEnumerable<string> extensions)
        {
            return extensions?.Where(ext => !ext.IsNullOrEmpty()).Select(ext => ext.Trim().ToLower());
        }

        public IEnumerable<string> GetDescriptionLines()
        {
            yield return $"{nameof(EmulatorId)}: {EmulatorId}";
            yield return $"{nameof(Emulator)}*: {Emulator?.Name ?? "<Unknown>"}";
            yield return $"{nameof(EmulatorProfileId)}: {EmulatorProfileId ?? "<Unknown>"}";
            yield return $"{nameof(EmulatorProfile)}*: {EmulatorProfile?.Name ?? "<Unknown>"}";
            yield return $"{nameof(PlatformId)}: {PlatformId ?? "<Unknown>"}";
            yield return $"{nameof(Platform)}*: {Platform?.Name ?? "<Unknown>"}";
            yield return $"{nameof(InstallMethod)}: {InstallMethod}";
            yield return $"{nameof(SourcePath)}: {SourcePath ?? "<Unknown>"}";
            yield return $"{nameof(DestinationPath)}: {DestinationPath ?? "<Unknown>"}";
            yield return $"{nameof(DestinationPathResolved)}*: {DestinationPathResolved ?? "<Unknown>"}";
            yield return $"{nameof(EmulatorBasePathResolved)}*: {EmulatorBasePathResolved ?? "<Unknown>"}";
        }
    }
}
