using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private readonly EmuLibrary plugin;
        private EmuLibrarySettings editingClone;

        [JsonIgnore]
        public readonly IPlayniteAPI PlayniteAPI;

        public static EmuLibrarySettings Instance { get; private set; }

        [JsonIgnore]
        public IEnumerable<Emulator> Emulators {
            get
            {
                return plugin.PlayniteApi.Database.Emulators.OrderBy(x => x.Name);
            }
        }

        [JsonIgnore]
        public IEnumerable<EmulatedPlatform> Platforms
        {
            get
            {
                return plugin.PlayniteApi.Emulation.Platforms.OrderBy(x => x.Name);
            }
        }

        public class ROMInstallerEmulatorMapping : ObservableObject
        {
            public ROMInstallerEmulatorMapping() { }

            [DefaultValue(true)]
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
            public bool Enabled { get; set; }
            public Guid EmulatorId { get; set; }

            [JsonIgnore]
            public Emulator Emulator
            {
                get
                {
                    return Instance?.Emulators.FirstOrDefault(e => e.Id == EmulatorId);
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
                    return Instance?.Platforms.FirstOrDefault(p => p.Id == PlatformId);
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
            public bool GamesUseFolders { get; set; }

            [JsonIgnore]
            public IEnumerable<EmulatorProfile> AvailableProfiles
            {
                get
                {
                    var emulator = Instance?.Emulators.FirstOrDefault(e => e.Id == EmulatorId);
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

                    return EmuLibrarySettings.Instance.Platforms.Where(p => validPlatforms.Contains(p.Id));
                }
            }

            [JsonIgnore]
            [XmlIgnore]
            public string DestinationPathResolved { get
                {
                    var playnite = EmuLibrarySettings.Instance.PlayniteAPI;
                    return playnite.Paths.IsPortable ? DestinationPath.Replace(Playnite.SDK.ExpandableVariables.PlayniteDirectory, playnite.Paths.ApplicationPath) : DestinationPath;
                } }

            // Not using ToString as this will end up longer than appropriate for that
            public string GetDescription()
            {
                var sb = new System.Text.StringBuilder();
                var emulator = Instance?.Emulators.FirstOrDefault(e => e.Id == EmulatorId);
                sb.Append("Emulator: ");
                sb.AppendLine(emulator?.Name ?? "<Unknown>");
                sb.Append("Profile: ");
                sb.AppendLine(emulator?.SelectableProfiles.FirstOrDefault(p => p.Id == EmulatorProfileId)?.Name ?? "<Unknown>");
                sb.Append("Platform: ");
                sb.AppendLine(Instance?.Platforms.FirstOrDefault(p => p.Id == PlatformId)?.Name ?? "<Unknown>");

                return sb.ToString();
            }
        }

        public ObservableCollection<ROMInstallerEmulatorMapping> Mappings { get; set; }

        // Parameterless constructor must exist if you want to use LoadPluginSettings method.
        public EmuLibrarySettings()
        {
        }

        public EmuLibrarySettings(EmuLibrary plugin, IPlayniteAPI api)
        {
            this.PlayniteAPI = api;
            this.plugin = plugin;

            var settings = plugin.LoadPluginSettings<EmuLibrarySettings>();
            if (settings != null)
            {
                LoadValues(settings);
            }

            // Need to initialize this if missing, else we don't have a valid list for UI to add to
            if (Mappings == null)
            {
                Mappings = new ObservableCollection<ROMInstallerEmulatorMapping>();
            }

            Instance = this;
        }

        public void BeginEdit()
        {
            editingClone = this.GetClone();
        }

        public void CancelEdit()
        {
            LoadValues(editingClone);
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(this);
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