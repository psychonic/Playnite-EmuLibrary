using System;
using System.IO;

namespace EmuLibrary.Util
{
    internal static class StreamUtils
    {
        // Like Stream.CopyTo, but reports the INCREMENTAL bytes written per chunk so a long copy can drive a
        // real percentage bar. Uses a 4 MiB chunk.
        public static void CopyWithProgress(Stream source, Stream destination, IProgress<long> progress)
        {
            var buffer = new byte[4 * 1024 * 1024];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, read);
                progress?.Report(read);
            }
        }
    }
}
