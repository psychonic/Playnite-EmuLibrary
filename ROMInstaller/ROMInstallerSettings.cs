using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;

namespace ROMManager
{
    [ValueConversion(typeof(Guid), typeof(Emulator))]
    public class EmulatorGuidConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert {targetType}");
            if (value is Guid)
            {
                Debug.Print("Value is Guid");
                return ROMInstallerSettings.Instance.Emulators.FirstOrDefault(e => e.Id == ((Guid)value));
            }
            Debug.Print("Value IS NOT Emulator");
            return null;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack {targetType}");
            if (value is Emulator)
            {
                Debug.Print("Value is Emulator");
                return (value as Emulator).Id;
            }

            Debug.Print("Value IS NOT Emulator");
            return null;
        }
    }

    [ValueConversion(typeof(Guid), typeof(EmulatorProfile))]
    public class EmulatorProfileGuidConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert EP {targetType}");
            if (value is Guid)
            {
                Debug.Print("Value is Guid");
                return ROMInstallerSettings.Instance.Emulators.SelectMany(e => e.Profiles).Where(ep => ep.Id == (Guid)value).First().Name;
            }
            Debug.Print("Value IS NOT Emulator");
            return null;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack EP {targetType}");
            if (value is EmulatorProfile)
            {
                Debug.Print("Value is EmulatorProfile");
                return (value as EmulatorProfile).Id;
            }

            Debug.Print("Value IS NOT EmulatorProfile");
            return null;
        }
    }

    public class EmulatorProfileListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            var emuId = ((ROMInstallerSettings.ROMInstallerEmulatorMapping)value).EmulatorId;
            return ROMInstallerSettings.Instance.Emulators.First(e => e.Id == emuId).Profiles;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    public class PathValidator : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            bool isValid = Directory.Exists(value as string);
            if (isValid)
            {
                return ValidationResult.ValidResult;
            }
            else
            {
                return new ValidationResult(false, "Please choose a valid folder");
            }
        }
    }

    public class ROMInstallerSettings : ObservableObject, ISettings
    {
        private readonly ROMManager plugin;
        private ROMInstallerSettings editingClone;

        public static ROMInstallerSettings Instance { get; private set; }

        [JsonIgnore]
        public IItemCollection<Emulator> Emulators {
            get
            {
                return plugin.PlayniteApi.Database.Emulators;
            }
        }

        public IItemCollection<EmulatorProfile> GetProfilesForEmulator(Guid emulatorId)
        {
            return (IItemCollection<EmulatorProfile>)plugin.PlayniteApi.Database.Emulators.Select(e => e.Profiles);
        }

        public class ROMInstallerEmulatorMapping : ObservableObject
        {
            public ROMInstallerEmulatorMapping() { }

            public Guid EmulatorId { get; set; }
            public Guid EmulatorProfileId { get; set; }
            public string SourcePath { get; set; }
            public string DestinationPath { get; set; }
        }

        public ObservableCollection<ROMInstallerEmulatorMapping> Mappings { get; set; }

        // Parameterless constructor must exist if you want to use LoadPluginSettings method.
        public ROMInstallerSettings()
        {
        }

        public ROMInstallerSettings(ROMManager plugin)
        {
            this.plugin = plugin;

            var settings = plugin.LoadPluginSettings<ROMInstallerSettings>();
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

        private void LoadValues(ROMInstallerSettings source)
        {
            source.CopyProperties(this, false, null, true);
        }
    }
}