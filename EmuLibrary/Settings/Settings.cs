using EmuLibrary.RomTypes;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        public bool AutoClearFinishedInstalls { get; set; } = false;
        public int AutoClearFinishedInstallsMinutes { get; set; } = 5;
        public bool ExtractDiscIcons { get; set; } = true;

        private string _sidebarIconPath = null;
        // Optional override for the Installs sidebar icon. May contain the {PlayniteDir} placeholder on
        // portable installs; resolved via PortablePathResolver when the sidebar item is built. Notifying so
        // the settings TextBox reflects browse/clear actions from the code-behind.
        public string SidebarIconPath
        {
            get => _sidebarIconPath;
            set => SetValue(ref _sidebarIconPath, value, new[] { nameof(SidebarIconPath) });
        }

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

        // Raised after the user commits settings edits, so live UI (e.g. the Installs sidebar item's
        // visibility) can react without requiring a Playnite restart.
        public event EventHandler Saved;

        // Mappings whose emulator/profile/platform changed and for which the user (in VerifySettings)
        // opted to push the change onto already-imported games. Applied in EndEdit, after the save.
        private List<EmulatorMapping> _pendingMetadataMigrations;

        public void EndEdit()
        {
            _plugin.SavePluginSettings(this);

            if (_pendingMetadataMigrations != null)
            {
                foreach (var mapping in _pendingMetadataMigrations)
                {
                    try
                    {
                        MappingEditReconciler.ApplyEmulatorPlatformMigration(PlayniteAPI, mapping, EmuLibrary.Logger);
                    }
                    catch (Exception ex)
                    {
                        EmuLibrary.Logger.Error(ex, $"Failed to migrate imported games for mapping {mapping.MappingId}.");
                    }
                }
                _pendingMetadataMigrations = null;
            }

            Saved?.Invoke(this, EventArgs.Empty);
        }

        public bool VerifySettings(out List<string> errors)
        {
            var mappingErrors = new List<string>();

            Mappings.Where(m => m.Enabled)?.ForEach(m =>
            {
                var scanner = EmuLibrary.GetScanner(m.RomType);
                if ((scanner?.RequiresProfileImageExtensions ?? true)
                    && (m.ImageExtensionsLower == null || !m.ImageExtensionsLower.Any()))
                {
                    mappingErrors.Add($"{m.MappingId}: No image extensions specified for profile {m.EmulatorProfile.Name} with emulator {m.Emulator.Name}. There is nothing for EmuLibrary to scan.");
                }

                if (string.IsNullOrEmpty(m.SourcePath))
                {
                    mappingErrors.Add($"{m.MappingId}: No source path specified.");
                }
                else if (!Directory.Exists(m.SourcePath))
                {
                    mappingErrors.Add($"{m.MappingId}: Source path doesn't exist ({m.SourcePath}).");
                }

                if (scanner?.RequiresDestinationPath ?? true)
                {
                    if (string.IsNullOrEmpty(m.DestinationPathResolved))
                    {
                        mappingErrors.Add($"{m.MappingId}: No destination path specified.");
                    }
                    else if (!Directory.Exists(m.DestinationPathResolved))
                    {
                        mappingErrors.Add($"{m.MappingId}: Destination path doesn't exist ({m.DestinationPathResolved}).");
                    }
                }
            });

            // Guardrail: changing a mapping's RomType rewrites the GameId format of its games (the RomType is
            // encoded in the serialized ELGameInfo), orphaning every game already imported under it. Block the
            // save while any such game exists - the remedy is to remove those games first or use a new mapping.
            if (_editingClone?.Mappings != null)
            {
                foreach (var current in Mappings)
                {
                    var previous = _editingClone.Mappings.FirstOrDefault(m => m.MappingId == current.MappingId);
                    if (previous == null || previous.RomType == current.RomType)
                        continue;

                    var count = MappingEditReconciler.GamesForMapping(PlayniteAPI, current.MappingId).Count();
                    if (count > 0)
                    {
                        mappingErrors.Add($"Cannot change ROM type from {previous.RomType} to {current.RomType} for mapping \"{current.DisplayName}\" while {count} imported game(s) reference it. Remove those games first, or create a separate mapping.");
                    }
                }
            }

            errors = mappingErrors;
            if (errors.Count > 0)
            {
                _pendingMetadataMigrations = null;
                return false;
            }

            OfferMetadataMigrations();
            return true;
        }

        // For each edited mapping whose emulator/profile/platform changed and that has already-imported games,
        // ask whether to push the change onto those games (they otherwise keep the old play action/platform
        // baked in at import - a rescan does not refresh them). Records the accepted mappings for EndEdit to
        // apply after the save. Called only once VerifySettings has otherwise passed.
        private void OfferMetadataMigrations()
        {
            _pendingMetadataMigrations = new List<EmulatorMapping>();
            if (_editingClone?.Mappings == null)
                return;

            foreach (var current in Mappings)
            {
                var previous = _editingClone.Mappings.FirstOrDefault(m => m.MappingId == current.MappingId);
                if (previous == null)
                    continue;

                var changed = previous.EmulatorId != current.EmulatorId
                    || previous.EmulatorProfileId != current.EmulatorProfileId
                    || previous.PlatformId != current.PlatformId;
                if (!changed)
                    continue;

                var count = MappingEditReconciler.GamesForMapping(PlayniteAPI, current.MappingId).Count();
                if (count == 0)
                    continue;

                var res = PlayniteAPI.Dialogs.ShowMessage(
                    $"You changed the emulator, profile, or platform for mapping \"{current.DisplayName}\".\r\n\r\n{count} already-imported game(s) still point at the previous settings and will not be updated by a rescan. Update them now?",
                    "Update imported games?",
                    System.Windows.MessageBoxButton.YesNo);

                if (res == System.Windows.MessageBoxResult.Yes)
                    _pendingMetadataMigrations.Add(current);
            }
        }

        private void LoadValues(Settings source)
        {
            source.CopyProperties(this, false, null, true);
        }
    }
}
