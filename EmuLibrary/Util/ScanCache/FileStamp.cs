using System;
using System.IO;
using System.IO.Abstractions;

namespace EmuLibrary.Util.ScanCache
{
    internal struct FileStamp : IEquatable<FileStamp>
    {
        public long SizeBytes { get; private set; }
        public long ModifiedUtcTicks { get; private set; }

        public FileStamp(long sizeBytes, long modifiedUtcTicks)
        {
            SizeBytes = sizeBytes;
            ModifiedUtcTicks = modifiedUtcTicks;
        }

        public static FileStamp FromFileSystemInfo(FileSystemInfoBase info)
        {
            var fi = info as FileInfoBase;
            long size = fi != null ? fi.Length : 0L;
            return new FileStamp(size, info.LastWriteTimeUtc.Ticks);
        }

        public static bool TryCapture(string path, out FileStamp stamp)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    stamp = default(FileStamp);
                    return false;
                }
                stamp = new FileStamp(fi.Length, fi.LastWriteTimeUtc.Ticks);
                return true;
            }
            catch
            {
                stamp = default(FileStamp);
                return false;
            }
        }

        public bool Equals(FileStamp other) =>
            SizeBytes == other.SizeBytes && ModifiedUtcTicks == other.ModifiedUtcTicks;

        public override bool Equals(object obj) => obj is FileStamp other && Equals(other);

        public override int GetHashCode() =>
            unchecked((SizeBytes.GetHashCode() * 397) ^ ModifiedUtcTicks.GetHashCode());
    }
}
