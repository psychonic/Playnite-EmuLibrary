using EmuLibrary.RomTypes.WiiU.Crypto;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EmuLibrary.Util.ZArchive
{
    // Builds a Cemu .wua from one or more decrypted NUS title content units. Each unit becomes a top-level
    // archive folder named "<titleId>_v<version>" (the layout Cemu's title manager produces and reads), under
    // which the decrypted code/content/meta tree is written. A single .wua may bundle base + update + DLC, so
    // Cemu applies all of it with no NAND install.
    internal static class WuaBuilder
    {
        // `units` should be the base game first, then the (latest) update, then DLC — the order the title
        // manager uses. Order does not affect correctness (Cemu keys content by the folder title id).
        public static void Build(string outputWuaPath, IReadOnlyList<NusReader> units, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(outputWuaPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var fs = new FileStream(outputWuaPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new ZArchiveWriter(fs))
            {
                foreach (var unit in units)
                {
                    ct.ThrowIfCancellationRequested();

                    string root = $"{unit.TitleId:x16}_v{unit.TitleVersion}";
                    writer.MakeDir(root, true);

                    foreach (var file in unit.EnumerateFiles())
                    {
                        ct.ThrowIfCancellationRequested();

                        string full = root + "/" + file.Path;
                        int slash = full.LastIndexOf('/');
                        if (slash > 0)
                            writer.MakeDir(full.Substring(0, slash), true);

                        if (!writer.StartNewFile(full))
                            throw new InvalidOperationException($"Duplicate path in archive: \"{full}\".");

                        using (var appendStream = new AppendStream(writer))
                            unit.ExtractFile(file, appendStream);
                    }
                }

                writer.Complete();
            }
        }

        // Forwards Stream.Write straight into the archive's current file (the only operation NusReader needs).
        private sealed class AppendStream : Stream
        {
            private readonly ZArchiveWriter _writer;
            public AppendStream(ZArchiveWriter writer) { _writer = writer; }

            public override void Write(byte[] buffer, int offset, int count) => _writer.AppendData(buffer, offset, count);

            public override bool CanWrite => true;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override void Flush() { }
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
