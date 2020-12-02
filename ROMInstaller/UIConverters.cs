using Playnite.SDK.Models;
using System;
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
                var emu = ROMInstallerSettings.Instance.Emulators.FirstOrDefault(e => e.Id == ((Guid)value));
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
                return ROMInstallerSettings.Instance.Emulators.SelectMany(e => e.Profiles).Where(ep => ep.Id == (Guid)value).FirstOrDefault()?.Name;
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

    [ValueConversion(typeof(Guid), typeof(Platform))]
    public class PlatformGuidConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert {targetType}");
            if (value is Guid)
            {
                Debug.Print("Value is Guid");
                var platform = ROMInstallerSettings.Instance.Platforms.FirstOrDefault(e => e.Id == ((Guid)value));
                return platform ?? new Platform() { };
            }
            Debug.Print("Value IS NOT Platform");
            return null;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack {targetType}");
            if (value is Platform)
            {
                Debug.Print("Value is Platform");
                return (value as Platform).Id;
            }

            Debug.Print("Value IS NOT Platform");
            return Guid.Empty;
        }
    }

    public class EmulatorProfileListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert EPL {targetType}");
            var emuId = ((ROMInstallerSettings.ROMInstallerEmulatorMapping)value).EmulatorId;
            return ROMInstallerSettings.Instance.Emulators.FirstOrDefault(e => e.Id == emuId)?.Profiles;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In ConvertBack EPL {targetType}");
            return null;
        }
    }

    public class PlatformListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            Debug.Print($"In Convert EL {targetType}");
            var emuId = ((ROMInstallerSettings.ROMInstallerEmulatorMapping)value).EmulatorId;
            var profileId = ((ROMInstallerSettings.ROMInstallerEmulatorMapping)value).EmulatorProfileId;
            var validPlatforms = ROMInstallerSettings.Instance.Emulators.FirstOrDefault(e => e.Id == emuId)?.Profiles.FirstOrDefault(p => p.Id == profileId)?.Platforms;
            if (validPlatforms != null)
            {
                return ROMInstallerSettings.Instance.Platforms.Where(p => validPlatforms.Contains(p.Id));
            }

            return new Platform() { };
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
