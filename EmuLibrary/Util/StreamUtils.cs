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

        // Reads exactly `count` bytes into `buffer` (from offset 0), looping until satisfied. Stream.Read is only
        // guaranteed to return at least one byte, not the full request (CA2022), so a single Read is unsafe for
        // fixed-layout parsing — always go through this. Throws EndOfStreamException on a short read; `what` names
        // the stream in that message (e.g. "CIA stream").
        public static void ReadExactly(Stream s, byte[] buffer, int count, string what = null)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException($"Unexpected end of {what ?? "stream"}.");
                read += n;
            }
        }

        // Allocates a `count`-byte buffer and fills it via ReadExactly.
        public static byte[] ReadExactly(Stream s, int count, string what = null)
        {
            var buffer = new byte[count];
            ReadExactly(s, buffer, count, what);
            return buffer;
        }

        // Seeks to `offset` then reads exactly `count` bytes into `buffer`.
        public static void ReadExactlyAt(FileStream fs, long offset, byte[] buffer, int count, string what = null)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            ReadExactly(fs, buffer, count, what);
        }

        // Best-effort read: reads up to `count` bytes into `buffer`, returning the number actually read (fewer
        // only at end of stream). Unlike ReadExactly, does not throw on a short read.
        public static int ReadUpTo(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read;
        }
    }
}
