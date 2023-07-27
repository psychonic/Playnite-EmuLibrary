using Newtonsoft.Json;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace EmuLibrary.RomTypes.Yuzu
{
    public class YuzuLegacySettings
    {
        public class ROMInstallerEmulatorMapping
        {
            public ROMInstallerEmulatorMapping() { }

            [DefaultValue(false)]
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
            public bool Enabled { get; set; }
            public Guid EmulatorId { get; set; }
            public string EmulatorProfileId { get; set; }
            public string PlatformId { get; set; }
            public string SourcePath { get; set; }
        }

        // Singlular mapping named "Mappings", ugh
        public List<ROMInstallerEmulatorMapping> Mappings { get; set; }

        private readonly Plugin _plugin;

        // Parameterless constructor must exist if you want to use LoadPluginSettings method.
        public YuzuLegacySettings(Plugin plugin)
        {
            _plugin = plugin;

            var settings = _plugin.LoadPluginSettings<YuzuLegacySettings>();
            if (settings != null)
            {
                LoadValues(settings);
            }

            // Need to initialize this if missing, else we don't have a valid list for UI to add to
            if (Mappings == null)
            {
                Mappings = new List<ROMInstallerEmulatorMapping>();
            }
        }

        private void LoadValues(YuzuLegacySettings source)
        {
            source.CopyProperties(this, false, null, true);
        }

        public void Save()
        {
            _plugin.SavePluginSettings(this);
        }
    }
}