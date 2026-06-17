using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EmuLibrary.Util.ScanConcurrency
{
    // Maps a path to the physical storage endpoint that backs it, so a single concurrency budget can be
    // shared by every scan touching the same device (one NAS host across its shares, one local volume).
    internal static class ScanEndpointKey
    {
        internal struct Endpoint
        {
            public string Key;
            public bool IsNetwork;
        }

        // Seam: the real implementation is WNetGetConnection (below); overridden in tests so the host-parse
        // and keying logic is unit-testable without a real mapped drive on the test box.
        internal static Func<string, string> DriveToUnc = TryResolveUncForDrive;

        // Seam: real impl is DriveInfo.DriveType; overridden in tests so the Network-coalescing branch is
        // reachable without an actual mapped network drive on the test box. Takes a path root (e.g. "Z:\").
        internal static Func<string, DriveType> DriveTypeOf = root => new DriveInfo(root).DriveType;

        // UNC \\server\share\...        => key "unc:server" (host only, per decision #2), IsNetwork = true.
        // Mapped NETWORK drive (Z:)     => resolve backing UNC via DriveToUnc, then key "unc:server" too, so
        //                                  Z: and \\server coalesce. If resolution fails, key "drive:<L>",
        //                                  IsNetwork = true (still gets the network budget).
        // Local Fixed/Removable drive   => key "vol:<L>", IsNetwork = false.
        // Unresolvable/empty            => key "", IsNetwork = true (assume remote — conservative for throughput).
        public static Endpoint For(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new Endpoint { Key = "", IsNetwork = true };

            try
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    var hostKey = HostKeyFromUnc(path);
                    return new Endpoint { Key = hostKey, IsNetwork = true };
                }

                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                    return new Endpoint { Key = "", IsNetwork = true };

                if (root.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    var hostKey = HostKeyFromUnc(root);
                    return new Endpoint { Key = hostKey, IsNetwork = true };
                }

                var driveType = DriveTypeOf(root);
                if (driveType == DriveType.Network)
                {
                    // "Z:" (colon, no trailing slash) is what WNetGetConnection expects.
                    var driveWithColon = root.TrimEnd('\\');
                    var unc = DriveToUnc(driveWithColon);
                    if (!string.IsNullOrEmpty(unc) && unc.StartsWith(@"\\", StringComparison.Ordinal))
                        return new Endpoint { Key = HostKeyFromUnc(unc), IsNetwork = true };

                    return new Endpoint { Key = "drive:" + DriveLetterOf(root), IsNetwork = true };
                }

                return new Endpoint { Key = "vol:" + DriveLetterOf(root), IsNetwork = false };
            }
            catch
            {
                // An offline/invalid path must not throw out of key derivation.
                return new Endpoint { Key = "", IsNetwork = true };
            }
        }

        // "\\server\share\..." -> "unc:server" (lower-cased so keys are stable).
        private static string HostKeyFromUnc(string unc)
        {
            var rest = unc.Substring(2);
            var sep = rest.IndexOf('\\');
            var host = sep >= 0 ? rest.Substring(0, sep) : rest;
            return "unc:" + host.ToLowerInvariant();
        }

        // "Z:\" or "Z:" -> "z".
        private static string DriveLetterOf(string root)
        {
            return root.TrimEnd('\\', ':').ToLowerInvariant();
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        // "Z:" (note: colon, NO trailing slash) -> "\\server\share", or null if not a mapped network drive.
        private static string TryResolveUncForDrive(string driveLetterWithColon)
        {
            try
            {
                var sb = new StringBuilder(260); // MAX_PATH; \\server\share never needs more in practice
                int len = sb.Capacity;
                int rc = WNetGetConnection(driveLetterWithColon, sb, ref len);
                if (rc == 234 /*ERROR_MORE_DATA*/) { sb = new StringBuilder(len); rc = WNetGetConnection(driveLetterWithColon, sb, ref len); }
                return rc == 0 ? sb.ToString() : null; // ERROR_NOT_CONNECTED (2250) etc. => null
            }
            catch { return null; }
        }
    }
}
