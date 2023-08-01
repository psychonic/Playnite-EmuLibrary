using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmuLibrary.Util
{
    static internal class FileNameUtils
    {
        private static readonly Regex _regionSingle = new Regex(@"\(([A-Za-z]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _regionDouble = new Regex(@"\(([A-Za-z]+), ([A-Za-z]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> _regionList = new HashSet<string>()
        {
            // Non-exaustive list of No-Intro countries, but we need a whitelist. (Can't just use first parenstheses group as game
            // name itself can contain them) This should have more missing matches than using first set would have bad matches.
            "Asia",
            "Australia",
            "Brazil",
            "Canada",
            "China",
            "Denmark",
            "Europe",
            "France",
            "Germany",
            "Greece",
            "Hong Kong",
            "Italy",
            "Japan",
            "Korea",
            "Mexico",
            "Netherlands",
            "Norway",
            "Poland",
            "Russia",
            "Spain",
            "Sweden",
            "Taiwan",
            "UK",
            "USA",
            "World",
        };

        private static readonly Dictionary<string, string> _regionMapping = new Dictionary<string, string>()
        {
            // Map GOODNES region codes to No-Intro
            { "A", "Australia" },
            { "As", "Asia" },
            { "B", "Brazil" },
            { "C", "Canada" },
            { "Ch", "China" },
            { "D", "Netherlands" },
            { "E", "Europe" },
            { "F", "France" },
            { "G", "Germany" },
            { "Gr", "Greece" },
            { "HK", "Hong Kong" },
            { "I", "Italy" },
            { "J", "Japan" },
            { "K", "Korea" },
            { "M", "Mexico" },
            { "Nl", "Netherlands" },
            { "No", "Norway" },
            { "R", "Russia" },
            { "S", "Spain" },
            { "Sw", "Sweden" },
            { "UK", "UK" },
            { "U", "USA" },
            { "W", "World" },

        };

        public static IList<string> GuessRegionsFromRomName(string name)
        {
            var ret = new List<string>();

            var match = _regionDouble.Match(name);
            if (match.Success)
            {
                if (_regionList.Contains(match.Groups[1].Value))
                    ret.Add(match.Groups[1].Value);
                else if (_regionMapping.TryGetValue(match.Groups[1].Value, out var mappedRegion))
                    ret.Add(mappedRegion);

                if (_regionList.Contains(match.Groups[2].Value))
                    ret.Add(match.Groups[2].Value);
                else if (_regionMapping.TryGetValue(match.Groups[2].Value, out var mappedRegion))
                    ret.Add(mappedRegion);
            }

            if (!ret.Any())
            {
                match = _regionSingle.Match(name);
                if (match.Success)
                {
                    if (_regionList.Contains(match.Groups[1].Value))
                        ret.Add(match.Groups[1].Value);
                    else if (_regionMapping.TryGetValue(match.Groups[1].Value, out var mappedRegion))
                        ret.Add(mappedRegion);
                }
            }

            return ret;
        }
    }
}
