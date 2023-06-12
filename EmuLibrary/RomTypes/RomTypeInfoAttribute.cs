using System;
using System.Collections.Generic;
using System.Linq;

namespace EmuLibrary.RomTypes
{
    [AttributeUsage(AttributeTargets.Field)]
    public class RomTypeInfoAttribute : Attribute
    {
        public readonly Type GameInfoType;
        public readonly Type ScannerType;

        public RomTypeInfoAttribute(Type gameInfoType, Type scannerType)
        {
            GameInfoType = gameInfoType;
            if (!GameInfoType.IsSubclassOf(typeof(ELGameInfo)))
                throw new ArgumentException($"GameInfoType must implement {nameof(ELGameInfo)}", nameof(gameInfoType));

            ScannerType = scannerType;
            if (!ScannerType.IsSubclassOf(typeof(RomTypeScanner)))
                throw new ArgumentException($"ScannerType must implement {nameof(RomTypeScanner)}", nameof(scannerType));
        }
    }
}
