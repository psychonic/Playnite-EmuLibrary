using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // Supplies a title's still-encrypted content bytes to NusReader, abstracting where they come from: loose
    // .app files in an NUS/WUP directory, or ranges of a WUD/WUX disc partition. The crypto (title-key
    // decryption, FST walk, hash-block handling) is identical for both and lives in NusReader.
    internal interface INusContentSource : IDisposable
    {
        byte[] Tmd { get; }
        byte[] Ticket { get; }

        // Raw (still title-key-encrypted) bytes of content[0] — the FST. Small; read fully into memory.
        byte[] ReadFstContent();

        // The content layout parsed from the FST, pushed in once after the FST is decrypted. Disc sources use
        // it to locate content within the partition; directory sources ignore it (each content is its own file).
        void SetContentLayout(ContentFstInfo[] contentFstInfos);

        // Fill `count` raw bytes of content `contentIndex` starting at `offsetInContent`, zero-padding past the
        // content's end.
        void ReadRawContent(int contentIndex, long offsetInContent, byte[] buffer, int count);
    }

    // Decrypts a Wii U title (faithful to VitaSmith/cdecrypt): decrypts the ticket title key with the Wii U
    // common key, decrypts the FST (content[0]), and streams individual decrypted files on demand (so even
    // multi-GB hash-tree contents never load fully into memory). The byte source is pluggable (directory or
    // disc) via INusContentSource.
    internal sealed class NusReader : IDisposable
    {
        private readonly INusContentSource _source;
        private readonly TitleMetadata _tmd;
        private readonly AesCbc _titleAes;
        private readonly Fst _fst;

        public ulong TitleId => _tmd.TitleId;
        public ulong BaseTitleId => _tmd.BaseTitleId;
        public ushort TitleVersion => _tmd.TitleVersion;
        public WiiUContentKind Kind => _tmd.Kind;

        public NusReader(INusContentSource source, byte[] commonKey)
        {
            _source = source;
            _tmd = TitleMetadata.Parse(source.Tmd);

            // Decrypt the title key: AES-CBC with the common key, IV = title id (8 bytes) zero-padded to 16.
            var iv = new byte[16];
            Buffer.BlockCopy(source.Tmd, 0x18C, iv, 0, 8);
            var encTitleKey = new byte[16];
            Buffer.BlockCopy(source.Ticket, 0x1BF, encTitleKey, 0, 16);
            var titleKey = new byte[16];
            using (var commonAes = new AesCbc(commonKey))
                commonAes.Decrypt(iv, encTitleKey, 0, 16, titleKey, 0);
            _titleAes = new AesCbc(titleKey);

            // content[0] is the FST, encrypted as a single CBC stream with an all-zero IV.
            var rawFst = source.ReadFstContent();
            var fstBytes = DecryptWhole(rawFst, ivZero: true, contentIdLow: 0);
            _fst = Fst.Parse(fstBytes);
            source.SetContentLayout(_fst.Contents);
        }

        // Convenience for a loose NUS/WUP directory.
        public NusReader(string dir, byte[] commonKey) : this(new NusDirectorySource(dir), commonKey) { }

        private byte[] DecryptWhole(byte[] enc, bool ivZero, byte contentIdLow)
        {
            int len = enc.Length - (enc.Length % 16);
            var dec = new byte[len];
            var iv = new byte[16];
            if (!ivZero)
                iv[1] = contentIdLow;
            using (var t = _titleAes.CreateChained(iv))
                t.TransformBlock(enc, 0, len, dec, 0);
            return dec;
        }

        public IEnumerable<NusFileEntry> EnumerateFiles()
        {
            foreach (var f in _fst.Files)
            {
                var content = _tmd.Contents[f.ContentIndex];
                yield return new NusFileEntry
                {
                    Path = f.Path,
                    Size = f.Size,
                    OffsetInContent = f.OffsetInContent,
                    ContentIndex = f.ContentIndex,
                    Hashed = content.IsHashed,
                    ContentIdLow = f.ContentIdLow,
                };
            }
        }

        public void ExtractFile(NusFileEntry entry, Stream dest)
        {
            if (entry.Hashed)
                ExtractHashed(entry, dest);
            else
                ExtractPlain(entry, dest);
        }

        public byte[] ReadFile(NusFileEntry entry)
        {
            using (var ms = new MemoryStream((int)entry.Size))
            {
                ExtractFile(entry, ms);
                return ms.ToArray();
            }
        }

        // Reads meta/meta.xml as text (null if absent) — used by the scanner for the title name/product code.
        public string ReadMetaXml()
        {
            foreach (var f in EnumerateFiles())
                if (f.Path.Equals("meta/meta.xml", StringComparison.OrdinalIgnoreCase))
                    return Encoding.UTF8.GetString(ReadFile(f));
            return null;
        }

        // Plain content: one continuous CBC stream (IV = {0, contentIdLow, 0...}), block size 0x8000.
        private void ExtractPlain(NusFileEntry entry, Stream dest)
        {
            const int BlockSize = 0x8000;
            ulong roffset = entry.OffsetInContent / BlockSize * BlockSize;
            long soffset = (long)(entry.OffsetInContent - roffset);

            var iv = new byte[16];
            iv[1] = entry.ContentIdLow;

            long writeSize = BlockSize;
            if (soffset + entry.Size > writeSize)
                writeSize = writeSize - soffset;

            long readPos = (long)roffset;
            var enc = new byte[BlockSize];
            var dec = new byte[BlockSize];
            long remaining = entry.Size;

            using (var t = _titleAes.CreateChained(iv))
            {
                while (remaining > 0)
                {
                    if (writeSize > remaining)
                        writeSize = remaining;

                    _source.ReadRawContent(entry.ContentIndex, readPos, enc, BlockSize);
                    readPos += BlockSize;
                    t.TransformBlock(enc, 0, BlockSize, dec, 0);

                    dest.Write(dec, (int)soffset, (int)writeSize);
                    remaining -= writeSize;

                    if (soffset != 0)
                    {
                        writeSize = BlockSize;
                        soffset = 0;
                    }
                }
            }
        }

        // Hash-tree content: each 0x10000 block is 0x400 hashes + 0xFC00 data. Hashes are decrypted with an IV
        // from the content id; data with an IV taken from the decrypted hash for that sub-block. Each block is
        // independent (no chaining). H0 verification is intentionally skipped — we only need the plaintext.
        private void ExtractHashed(NusFileEntry entry, Stream dest)
        {
            const int BlockSize = 0x10000;
            const int DataSize = 0xFC00;
            const int HashesSize = 0x400;

            long blockNumber = (long)(entry.OffsetInContent / DataSize) & 0x0F;
            ulong roffset = entry.OffsetInContent / DataSize * BlockSize;
            long soffset = (long)(entry.OffsetInContent - entry.OffsetInContent / DataSize * DataSize);

            long writeSize = DataSize;
            if (soffset + entry.Size > writeSize)
                writeSize = writeSize - soffset;

            long readPos = (long)roffset;
            var enc = new byte[BlockSize];
            var dec = new byte[DataSize];
            var hashes = new byte[HashesSize];
            long remaining = entry.Size;

            while (remaining > 0)
            {
                if (writeSize > remaining)
                    writeSize = remaining;

                _source.ReadRawContent(entry.ContentIndex, readPos, enc, BlockSize);
                readPos += BlockSize;

                var ivHashes = new byte[16];
                ivHashes[1] = entry.ContentIdLow;
                _titleAes.Decrypt(ivHashes, enc, 0, HashesSize, hashes, 0);

                var ivData = new byte[16];
                Buffer.BlockCopy(hashes, (int)(0x14 * blockNumber), ivData, 0, 16);
                if (blockNumber == 0)
                    ivData[1] ^= entry.ContentIdLow;
                _titleAes.Decrypt(ivData, enc, HashesSize, DataSize, dec, 0);

                dest.Write(dec, (int)soffset, (int)writeSize);
                remaining -= writeSize;

                blockNumber++;
                if (blockNumber >= 16)
                    blockNumber = 0;

                if (soffset != 0)
                {
                    writeSize = DataSize;
                    soffset = 0;
                }
            }
        }

        public void Dispose()
        {
            _titleAes?.Dispose();
            _source?.Dispose();
        }
    }

    // One decrypted file in a title's FST plus where its still-encrypted bytes live.
    internal sealed class NusFileEntry
    {
        public string Path;            // forward-slash relative path, e.g. "meta/meta.xml"
        public uint Size;              // logical (decrypted) file length
        public ulong OffsetInContent;  // byte offset within its content
        public ushort ContentIndex;    // index into the TMD content list
        public bool Hashed;            // hash-tree protected content (different block layout)
        public byte ContentIdLow;      // low byte of the content index, used to seed the IV
    }

    // Reads content from a loose NUS/WUP directory: title.tmd + title.tik + per-content "%08x.app" files.
    internal sealed class NusDirectorySource : INusContentSource
    {
        private readonly string _dir;
        private readonly TitleMetadata _tmd;
        private readonly Dictionary<int, FileStream> _open = new Dictionary<int, FileStream>();

        public byte[] Tmd { get; }
        public byte[] Ticket { get; }

        public NusDirectorySource(string dir)
        {
            _dir = dir;
            Tmd = File.ReadAllBytes(FindRequired(dir, "title.tmd"));
            Ticket = File.ReadAllBytes(FindRequired(dir, "title.tik"));
            _tmd = TitleMetadata.Parse(Tmd);
        }

        public byte[] ReadFstContent() => File.ReadAllBytes(ResolveAppPath(_tmd.Contents[0].Id));

        public void SetContentLayout(ContentFstInfo[] contentFstInfos) { /* each content is its own .app at offset 0 */ }

        public void ReadRawContent(int contentIndex, long offsetInContent, byte[] buffer, int count)
        {
            var fs = GetStream(contentIndex);
            fs.Seek(offsetInContent, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buffer, read, count - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read < count)
                Array.Clear(buffer, read, count - read);
        }

        private FileStream GetStream(int contentIndex)
        {
            if (!_open.TryGetValue(contentIndex, out var fs))
            {
                fs = new FileStream(ResolveAppPath(_tmd.Contents[contentIndex].Id), FileMode.Open, FileAccess.Read, FileShare.Read);
                _open[contentIndex] = fs;
            }
            return fs;
        }

        private string ResolveAppPath(uint contentId)
        {
            foreach (var name in new[] { $"{contentId:x8}.app", $"{contentId:X8}.app", $"{contentId:x8}", $"{contentId:X8}" })
            {
                var p = Path.Combine(_dir, name);
                if (File.Exists(p))
                    return p;
            }
            throw new FileNotFoundException($"Could not find content .app for id {contentId:x8} in \"{_dir}\".");
        }

        private static string FindRequired(string dir, string fileName)
        {
            var p = Path.Combine(dir, fileName);
            if (!File.Exists(p))
                throw new FileNotFoundException($"Missing \"{fileName}\" in NUS directory \"{dir}\".", p);
            return p;
        }

        public void Dispose()
        {
            foreach (var fs in _open.Values)
                fs.Dispose();
            _open.Clear();
        }
    }
}
