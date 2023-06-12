using System.Globalization;
using System.IO;
using System.Windows.Controls;

namespace EmuLibrary.Settings
{
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
