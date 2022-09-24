using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;

namespace EmuLibrary
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
                var emu = EmuLibrarySettings.Instance.Emulators.FirstOrDefault(e => e.Id == ((Guid)value));
                return emu ?? new Emulator() { };
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
            return Guid.Empty;
        }
    }

    [ValueConversion(typeof(string), typeof(EmulatorProfile))]
    public class EmulatorProfileIdConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert EP {targetType}");
            if (value is string)
            {
                Debug.Print("Value is string");
                return EmuLibrarySettings.Instance.Emulators.SelectMany(e => e.SelectableProfiles).Where(ep => ep.Id == (string)value).FirstOrDefault()?.Name;
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

    [ValueConversion(typeof(string), typeof(EmulatedPlatform))]
    public class EmulatedPlatformIdConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert {targetType}");
            if (value is string)
            {
                Debug.Print("Value is string");
                var platform = EmuLibrarySettings.Instance.Platforms.FirstOrDefault(e => e.Id == ((string)value));
                return platform ?? new EmulatedPlatform() { };
            }
            Debug.Print("Value IS NOT Platform");
            return null;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack {targetType}");
            if (value is EmulatedPlatform)
            {
                Debug.Print("Value is EmulatedPlatform");
                return (value as EmulatedPlatform).Id;
            }

            Debug.Print("Value IS NOT EmulatedPlatform");
            return null;
        }
    }

    public class EmulatorProfileListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert EPL {targetType}");
            var emuId = ((EmuLibrarySettings.ROMInstallerEmulatorMapping)value).EmulatorId;
            return EmuLibrarySettings.Instance.Emulators.FirstOrDefault(e => e.Id == emuId)?.SelectableProfiles;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack EPL {targetType}");
            return null;
        }
    }

    public class EmulatedPlatformListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert EL {targetType}");
            var emuId = ((EmuLibrarySettings.ROMInstallerEmulatorMapping)value).EmulatorId;
            var profileId = ((EmuLibrarySettings.ROMInstallerEmulatorMapping)value).EmulatorProfileId;
            var emulator = EmuLibrarySettings.Instance.PlayniteAPI.Database.Emulators.First(e => e.Id == emuId);
            var emuProfile = emulator.SelectableProfiles.First(p => p.Id == profileId);
            var validPlatforms = EmuLibrarySettings.Instance.PlayniteAPI.Emulation.Emulators.First(e => e.Id == emulator.BuiltInConfigId).Profiles.First(p => p.Name == emuProfile.Name).Platforms;

            return EmuLibrarySettings.Instance.Platforms.Where(p => validPlatforms.Contains(p.Id));
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack EL {targetType}");
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
}
