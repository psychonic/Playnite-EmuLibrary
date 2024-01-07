using EmuLibrary.RomTypes;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace EmuLibrary.Settings
{
    public class Settings : ObservableObject, ISettings
    {
        private readonly Plugin _plugin;
        private Settings _editingClone;

        [JsonIgnore]
        internal readonly IPlayniteAPI PlayniteAPI;

        [JsonIgnore]
        internal readonly IEmuLibrary EmuLibrary;

        public static Settings Instance { get; private set; }

        public bool ScanGamesInFullScreen { get; set; } = false;
        public bool NotifyOnInstallComplete { get; set; } = false;
        public bool AutoRemoveUninstalledGamesMissingFromSource { get; set; } = false;
        public bool UseWindowsCopyDialogInDesktopMode { get; set; } = false;
        public bool UseWindowsCopyDialogInFullscreenMode { get; set; } = false;
        public ObservableCollection<EmulatorMapping> Mappings { get; set; }

        // Hidden settings
        public int Version { get; set; }
        public Dictionary<RomType, bool> MigratedLegacySettings { get; set; }


        // Parameterless constructor must exist if you want to use LoadPluginSettings method.
        public Settings()
        {
        }

        internal Settings(Plugin plugin, IEmuLibrary emuLibrary)
        {
            EmuLibrary = emuLibrary;
            PlayniteAPI = emuLibrary.Playnite;
            Instance = this;
            _plugin = plugin;

            bool forceSave = false;

            var settings = plugin.LoadPluginSettings<Settings>();
            if (settings == null || settings.Version == 0)
            {
                // Settings didn't load cleanly or need to be upgraded. Make sure we save in new format
                forceSave = true;

                var settingsV0 = plugin.LoadPluginSettings<SettingsV0>();
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
                Mappings = new ObservableCollection<EmulatorMapping>();
            }

            var mappingsWithoutId = Mappings.Where(m => m.MappingId == default);
            if (mappingsWithoutId.Any())
            {
                mappingsWithoutId.ForEach(m => m.MappingId = Guid.NewGuid());
                forceSave = true;
            }

            if (Mappings.Count == 0)
            {
                // We want this to default to true for new installs, but not enable automatically for existing users
                AutoRemoveUninstalledGamesMissingFromSource = true;
            }

            Enum.GetValues(typeof(RomType)).Cast<RomType>().ForEach(rt =>
            {
                var scanner = emuLibrary.GetScanner(rt);
                if (scanner == null)
                    return;

                var legacyPlugin = PlayniteAPI.Addons.Plugins.FirstOrDefault(p => p.Id == scanner.LegacyPluginId);
                if (legacyPlugin == null)
                    return;

                if (!MigratedLegacySettings.TryGetValue(rt, out bool migrated))
                {
                    EmulatorMapping newMapping = null;
                    var res = emuLibrary.GetScanner(rt)?.MigrateLegacyPluginSettings(legacyPlugin, out newMapping);

                    switch (res)
                    {
                        case LegacySettingsMigrationResult.Success:
                            Mappings.Add(newMapping);
                            MigratedLegacySettings.Add(rt, false);
                            break;
                        case LegacySettingsMigrationResult.Failure:
                            // Nothing to do here. Let it try again next time, maybe after plugin update
                            break;
                        case LegacySettingsMigrationResult.Unnecessary:
                            MigratedLegacySettings.Add(rt, false);
                            break;
                    }
                }
            });

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

        private void LoadValues(Settings source)
        {
            source.CopyProperties(this, false, null, true);
        }
    }
}
