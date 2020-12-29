using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

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
        public IEnumerable<Platform> Platforms
        {
            get
            {
                return plugin.PlayniteApi.Database.Platforms.OrderBy(x => x.Name);
            }
        }

        public class ROMInstallerEmulatorMapping : ObservableObject
        {
            public ROMInstallerEmulatorMapping() { }

            public Guid EmulatorId { get; set; }
            public Guid EmulatorProfileId { get; set; }
            public Guid PlatformId { get; set; }
            public string SourcePath { get; set; }
            public string DestinationPath { get; set; }
            public bool GamesUseFolders { get; set; }

            // Not using ToString as this will end up longer than appropriate for that
            public string GetDescription()
            {
                var sb = new System.Text.StringBuilder();
                var emulator = Instance?.Emulators.FirstOrDefault(e => e.Id == EmulatorId);
                sb.Append("Emulator: ");
                sb.AppendLine(emulator?.Name ?? "<Unknown>");
                sb.Append("Profile: ");
                sb.AppendLine(emulator?.Profiles.FirstOrDefault(p => p.Id == EmulatorProfileId)?.Name ?? "<Unknown>");
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