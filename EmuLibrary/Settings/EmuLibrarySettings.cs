using EmuLibrary.RomTypes;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Xml.Serialization;

namespace EmuLibrary
{
    public class EmuLibrarySettings : ObservableObject, ISettings
    {
        private readonly EmuLibrary _plugin;
        private EmuLibrarySettings _editingClone;

        public int Version { get; set; }

        [JsonIgnore]
        public readonly IPlayniteAPI PlayniteAPI;

        public static EmuLibrarySettings Instance { get; private set; }

        public class ROMInstallerEmulatorMapping : ObservableObject
        {
            public ROMInstallerEmulatorMapping() { }

            public Guid MappingId { get; set; }

            [DefaultValue(true)]
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
            public bool Enabled { get; set; }
            public Guid EmulatorId { get; set; }

            [JsonIgnore]
            public Emulator Emulator
            {
                get
                {
                    return AvailableEmulators.FirstOrDefault(e => e.Id == EmulatorId);
                }
                set
                {
                    EmulatorId = value.Id;
                }
            }
            public string EmulatorProfileId { get; set; }

            [JsonIgnore]
            public EmulatorProfile EmulatorProfile
            {
                get
                {
                    return Emulator?.SelectableProfiles.FirstOrDefault(p => p.Id == EmulatorProfileId);
                }
                set
                {
                    EmulatorProfileId = value.Id;
                }
            }

            public string PlatformId { get; set; }

            [JsonIgnore]
            public EmulatedPlatform Platform
            {
                get
                {
                    return AvailablePlatforms.FirstOrDefault(p => p.Id == PlatformId);
                }
                set
                {
                    PlatformId = value.Id;
                }
            }

            [JsonIgnore]
            public IEnumerable<string> ImageExtensionsLower
            {
                get
                {
                    IEnumerable<string> imageExtensionsLower;
                    if (EmulatorProfile is CustomEmulatorProfile)
                    {
                        imageExtensionsLower = (EmulatorProfile as CustomEmulatorProfile).ImageExtensions.Where(ext => !ext.IsNullOrEmpty()).Select(ext => ext.Trim().ToLower());
                    }
                    else if (EmulatorProfile is BuiltInEmulatorProfile)
                    {
                        imageExtensionsLower = Instance?.PlayniteAPI.Emulation.Emulators.First(e => e.Id == Emulator.BuiltInConfigId).Profiles.FirstOrDefault(p => p.Name == EmulatorProfile.Name).ImageExtensions.Where(ext => !ext.IsNullOrEmpty()).Select(ext => ext.Trim().ToLower());
                    }
                    else
                    {
                        throw new NotImplementedException("Unknown emulator profile type.");
                    }

                    return imageExtensionsLower;
                }
            }

            public string SourcePath { get; set; }
            public string DestinationPath { get; set; }
            public RomType RomType { get; set; }

            [JsonIgnore]
            public IEnumerable<Emulator> AvailableEmulators
            {
                get
                {
                    return Instance.PlayniteAPI.Database.Emulators.OrderBy(x => x.Name);
                }
            }

            [JsonIgnore]
            public IEnumerable<EmulatorProfile> AvailableProfiles
            {
                get
                {
                    var emulator = AvailableEmulators.FirstOrDefault(e => e.Id == EmulatorId);
                    return emulator?.SelectableProfiles;
                }
            }

            [JsonIgnore]
            public IEnumerable<EmulatedPlatform> AvailablePlatforms
            {
                get
                {
                    IEnumerable<string> validPlatforms;

                    if (EmulatorProfile is CustomEmulatorProfile)
                    {
                        var customProfile = EmulatorProfile as CustomEmulatorProfile;
                        validPlatforms = Instance.PlayniteAPI.Database.Platforms.Where(p => customProfile.Platforms.Contains(p.Id)).Select(p => p.SpecificationId);
                    }
                    else if (EmulatorProfile is BuiltInEmulatorProfile)
                    {
                        var builtInProfile = (EmulatorProfile as BuiltInEmulatorProfile);
                        validPlatforms = Instance.PlayniteAPI.Emulation.Emulators.FirstOrDefault(e => e.Id == Emulator.BuiltInConfigId)?.Profiles.FirstOrDefault(p => p.Name == builtInProfile.Name)?.Platforms;
                    }
                    else
                    {
                        validPlatforms = new List<string>();
                    }

                    return Instance.PlayniteAPI.Emulation.Platforms.Where(p => validPlatforms.Contains(p.Id));
                }
            }

            [JsonIgnore]
            [XmlIgnore]
            public string DestinationPathResolved
            {
                get
                {
                    var playnite = Instance.PlayniteAPI;
                    return playnite.Paths.IsPortable ? DestinationPath.Replace(ExpandableVariables.PlayniteDirectory, playnite.Paths.ApplicationPath) : DestinationPath;
                }
            }

            public IEnumerable<string> GetDescriptionLines()
            {
                yield return $"{nameof(EmulatorId)}: {EmulatorId}";
                yield return $"{nameof(Emulator)}*: {Emulator?.Name ?? "<Unknown>"}";
                yield return $"{nameof(EmulatorProfileId)}: {EmulatorProfileId}";
                yield return $"{nameof(EmulatorProfile)}*: {EmulatorProfile?.Name ?? "<Unknown>"}";
                yield return $"{nameof(PlatformId)}: {PlatformId}";
                yield return $"{nameof(Platform)}*: {Platform?.Name ?? "<Unknown>"}";
                yield return $"{nameof(SourcePath)}: {SourcePath}";
                yield return $"{nameof(DestinationPath)}: {DestinationPath}";
                yield return $"{nameof(DestinationPathResolved)}*: {DestinationPathResolved}";
            }
        }

        public bool ScanGamesInFullScreen { get; set; } = false;
        public bool NotifyOnInstallComplete { get; set; } = false;
        public ObservableCollection<ROMInstallerEmulatorMapping> Mappings { get; set; }

        // Parameterless constructor must exist if you want to use LoadPluginSettings method.
        public EmuLibrarySettings()
        {
        }

        public EmuLibrarySettings(EmuLibrary plugin, IPlayniteAPI api)
        {
            PlayniteAPI = api;
            Instance = this;
            _plugin = plugin;

            bool forceSave = false;

            var settings = plugin.LoadPluginSettings<EmuLibrarySettings>();
            if (settings == null || settings.Version == 0)
            {
                // Settings didn't load cleanly or need to be upgraded. Make sure we save in new format
                forceSave = true;

                var settingsV0 = plugin.LoadPluginSettings<EmuLibrarySettingsV0>();
                if (settingsV0 != null)
                {
                    settings = settingsV0.ToV1Settings();
                }
            }

            if (settings != null)
            {
                settings.Version = 1;
                LoadValues(settings);
            }

            // Need to initialize this if missing, else we don't have a valid list for UI to add to
            if (Mappings == null)
            {
                Mappings = new ObservableCollection<ROMInstallerEmulatorMapping>();
            }

            var mappingsWithoutId = Mappings.Where(m => m.MappingId == default);
            if (mappingsWithoutId.Any())
            {
                mappingsWithoutId.ForEach(m => m.MappingId = Guid.NewGuid());
                forceSave = true;
            }

            if (forceSave)
            {
                _plugin.SavePluginSettings(this);
            }
        }

        public void BeginEdit()
        {
            _editingClone = this.GetClone();
        }

        public void CancelEdit()
        {
            LoadValues(_editingClone);
        }

        public void EndEdit()
        {
            _plugin.SavePluginSettings(this);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }

        private void LoadValues(EmuLibrarySettings source)
        {
            source.CopyProperties(this, false, null, true);
        }
    }
}