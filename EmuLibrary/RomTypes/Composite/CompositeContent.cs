using System;
using System.Collections.Generic;
using System.Linq;

namespace EmuLibrary.RomTypes
{
    // The one genuinely shared algorithm across composite-content RomTypes: given a title's update files and
    // a per-platform version key, decide which updates to install and in what order. Everything else (how a
    // base/update/DLC is actually applied) is per-platform and lives behind the install controllers.
    internal static class CompositeContent
    {
        // Updates to install, in install order. The version key is the ONLY thing that orders updates — never
        // the filename or enumeration order. InstallAllUpdatesInOrder returns every update ascending;
        // InstallLatestUpdateOnly returns just the highest-versioned one as a 0-or-1 element list.
        public static IReadOnlyList<T> SelectUpdatesToInstall<T>(
            IEnumerable<T> updates,
            UpdateInstallStrategy strategy,
            Func<T, IComparable> versionKey)
        {
            if (updates == null)
                return new T[0];

            var ordered = updates.OrderBy(u => versionKey(u)).ToList();

            switch (strategy)
            {
                case UpdateInstallStrategy.InstallAllUpdatesInOrder:
                    return ordered;
                case UpdateInstallStrategy.InstallLatestUpdateOnly:
                    return ordered.Count == 0
                        ? (IReadOnlyList<T>)new T[0]
                        : new[] { ordered[ordered.Count - 1] };
                default:
                    throw new ArgumentOutOfRangeException(nameof(strategy));
            }
        }
    }
}
