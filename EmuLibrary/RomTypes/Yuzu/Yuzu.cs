using EmuLibrary.PlayniteCommon;
using IniParser.Model.Configuration;
using IniParser.Parser;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;
using LibHac.FsSystem.NcaUtils;
using LibHac.Ns;
using LibHac.Spl;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using ZstdSharp;

namespace EmuLibrary.RomTypes.Yuzu
{
    class Yuzu
    {
        public enum ExternalGameFileType
        {
            Game,
            Update,
            DLC,
        }

        public enum FileType
        {
            XCI,
            NSP,
        }

        public class ExternalGameFileInfo
        {
            public string FilePath;
            public FileType FileType;

            // From CNMT
            public string TitleIdHex
            {
                get
                {
                    return TitleId.ToString("x16");
                }
            }
            public ulong TitleId;
            public string BaseTitleIdHex
            {
                get
                {
                    return BaseTitleId.ToString("x16");
                }
            }
            public ulong BaseTitleId;
            public ExternalGameFileType Type;
            public uint Version;
            // Override with displayversion from NACP if present
            public string DisplayVersion;

            // From NACP
            public string TitleName;
            public string Publisher;

            // From Program NCA
            public string LaunchSubPath;
        }

        public string BasePath { get; private set; }

        public string UserPath
        {
            get
            {
                var portableYuzuUserDir = Path.Combine(BasePath, "user");
                if (Directory.Exists(portableYuzuUserDir))
                {
                    return portableYuzuUserDir;
                }
                else
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuzu");
                }
            }
        }

        public string NandPath
        {
            get
            {
                string yuzuConfigPath = Path.Combine(new string[] { UserPath, "config", "qt-config.ini" });

                var parser = new IniDataParser(
                    new IniParserConfiguration()
                    {
                        SkipInvalidLines = true
                    }
                );

                var iniData = parser.Parse(File.ReadAllText(yuzuConfigPath));
                return iniData["Data%20Storage"]["nand_directory"].Replace("\\\\", "\\").Replace('/', '\\');
            }
        }

        public string KeysPath
        {
            get
            {
                return Path.Combine(UserPath, "keys");
            }
        }

        private Keyset KeySet;

        private static string GetHactoolKeyFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch");
        }

        private static bool IsYuzuRunning()
        {
            return Process.GetProcessesByName("yuzu").Length > 0;
        }

        private readonly ulong GameTitleMask = 0xFFFFFFFFFFFFF000;

        enum KeyFileType
        {
            Standard,
            Title,
            Console,
        }

        [Flags]
        enum KeySource
        {
            Yuzu,
            Hactool,
        }

        class NczSection
        {
            public ulong Offset;
            public ulong Size;
            public NcaEncryptionType CryptoType;
            public byte[] CryptoKey;
            public byte[] CryptoCounter;
        }

        // From LibHac
        private static void UpdateCounter(Span<byte> counter, long offset)
        {
            ulong off = (ulong)offset >> 4;
            for (uint j = 0; j < 0x7; j++)
            {
                counter[(int)(0x10 - j - 1)] = (byte)(off & 0xFF);
                off >>= 8;
            }

            // Because the value stored in the counter is offset >> 4, the top 4 bits 
            // of byte 8 need to have their original value preserved
            counter[8] = (byte)((counter[8] & 0xF0) | (int)(off & 0x0F));
        }
        //

        private void TryLoadKeysFromYuzuOrHactool(KeyFileType fileType, string fileName, KeySource source)
        {
            string keyPath = null;

            // Prefer Yuzu keys when able
            if (source.HasFlag(KeySource.Yuzu))
            {
                var keyPathTmp = Path.Combine(KeysPath, fileName);
                if (File.Exists(keyPathTmp))
                {
                    keyPath = keyPathTmp;
                }
                else if (source.HasFlag(KeySource.Hactool))
                {
                    keyPathTmp = Path.Combine(GetHactoolKeyFolder(), fileName);
                    if (File.Exists(keyPathTmp))
                    {
                        keyPath = keyPathTmp;
                    }
                }
            }

            if (keyPath != null)
            {
                switch (fileType)
                {
                    case KeyFileType.Standard:
                        ExternalKeyReader.ReadKeyFile(KeySet, keyPath);
                        break;
                    case KeyFileType.Title:
                        ExternalKeyReader.ReadKeyFile(KeySet, null, keyPath);
                        break;
                    case KeyFileType.Console:
                        ExternalKeyReader.ReadKeyFile(KeySet, null, null, keyPath);
                        break;
                }
            }            
        }

        private void InitKeySet()
        {
            KeySet = new Keyset();
            TryLoadKeysFromYuzuOrHactool(KeyFileType.Standard, "prod.keys", KeySource.Yuzu | KeySource.Hactool);
            TryLoadKeysFromYuzuOrHactool(KeyFileType.Standard, "prod.keys_autogenerated", KeySource.Yuzu);
            TryLoadKeysFromYuzuOrHactool(KeyFileType.Title, "title.keys", KeySource.Yuzu | KeySource.Hactool);
            TryLoadKeysFromYuzuOrHactool(KeyFileType.Title, "title.keys_autogenerated", KeySource.Yuzu);
            TryLoadKeysFromYuzuOrHactool(KeyFileType.Console, "console.keys", KeySource.Yuzu | KeySource.Hactool);
            TryLoadKeysFromYuzuOrHactool(KeyFileType.Console, "console.keys_autogenerated", KeySource.Yuzu);
        }

        private void SaveKeys()
        {
            var titleKeysPath = Path.Combine(KeysPath, "title.keys_autogenerated");

            var sb = new System.Text.StringBuilder();
            // Okay, so we're lying
            sb.AppendLine("# This file is autogenerated by Yuzu");
            sb.AppendLine("# It serves to store keys that were automatically generated from the normal keys");
            sb.AppendLine("# If you are experiencing issues involving keys, it may help to delete this file");
            sb.AppendLine();
            sb.Append(ExternalKeyReader.PrintTitleKeys(KeySet));
            File.WriteAllText(titleKeysPath, sb.ToString());
        }

        private void ImportTickets(PartitionFileSystem fs)
        {
            foreach (DirectoryEntryEx ticketEntry in fs.EnumerateEntries("/", "*.tik"))
            {
                Result result = fs.OpenFile(out IFile ticketFile, ticketEntry.FullPath, OpenMode.Read);

                if (result.IsSuccess())
                {
                    Ticket ticket = new Ticket(ticketFile.AsStream());

                    if (ticket.TitleKeyType == TitleKeyType.Common)
                    {
                        KeySet.ExternalKeySet.Add(new RightsId(ticket.RightsId), new AccessKey(ticket.GetTitleKey(KeySet)));
                    }
                }
            }
        }

        private readonly ILogger _logger;

        public Yuzu(string basePath, ILogger logger)
        {
            BasePath = basePath;
            _logger = logger;

            InitKeySet();
        }

        public void InstallFileToNand(string fileToInstall)
        {
            if (IsYuzuRunning())
                throw new Exception("Cannot install game while Yuzu is running");

            var fileInfo = new FileInfo(fileToInstall);
            if (!fileInfo.Exists)
            {
                throw new ArgumentException($"File \"{fileToInstall}\" does not exist!");
            }

            using (var fileStream = new FileStream(fileToInstall, FileMode.Open, FileAccess.Read))
            {
                PartitionFileSystem pfs;

                switch (fileInfo.Extension)
                {
                    case ".xci":
                    case ".xcz":
                        var xci = new Xci(KeySet, fileStream.AsStorage());
                        pfs = xci.OpenPartition(XciPartitionType.Secure);
                        break;
                    case ".nsp":
                    case ".nsz":
                        pfs = new PartitionFileSystem(fileStream.AsStorage());
                        break;
                    default:
                        throw new ArgumentException($"File \"{fileToInstall}\" is not an XCI or NSP file.");
                }

                var ncaFileNamesToInstall = new List<string>();

                foreach (var fileEntry in pfs.EnumerateEntries("/", "*"))
                {
                    if (fileEntry.FullPath.ToLower().Contains("cnmt") && Path.GetExtension(fileEntry.FullPath).ToLower() == ".nca")
                    {
                        ncaFileNamesToInstall.Add(fileEntry.Name);

                        ImportTickets(pfs);

                        pfs.OpenFile(out IFile ncaFile, fileEntry.FullPath, OpenMode.Read).ThrowIfFailure();
                        using (ncaFile)
                        {
                            var cnmtNca = new Nca(KeySet, ncaFile.AsStorage());
                            using (var fs = cnmtNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.IgnoreOnInvalid))
                            {
                                string cnmtPath = fs.EnumerateEntries("/", "*.cnmt").Single().FullPath;
                                if (fs.OpenFile(out var metaFile, cnmtPath, OpenMode.Read).IsSuccess())
                                {
                                    using (metaFile)
                                    {
                                        var meta = new Cnmt(metaFile.AsStream());
                                        foreach (var entry in meta.ContentEntries)
                                        {
                                            if (entry.Type != LibHac.Ncm.ContentType.DeltaFragment)
                                            {
                                                ncaFileNamesToInstall.Add(BitConverter.ToString(entry.NcaId).Replace("-", "").ToLower() + ".nca");
                                            }
                                        }
                                    }
                                }

                                break;
                            }
                        }
                    }
                }

                ncaFileNamesToInstall.ForEach(ncaFileName =>
                {
                    bool isCompressed = false;
                    var fileEntry = pfs.Files.FirstOrDefault(f => f.Name == ncaFileName);
                    if (fileEntry == null)
                    {
                        fileEntry = pfs.Files.FirstOrDefault(f => f.Name == ncaFileName.Replace(".nca", ".ncz"));
                        isCompressed = true;
                    }

                    var outPath = Path.Combine(new string[] { NandPath, "user", "Contents", "registered", GetRelativePathFromNcaId(fileEntry.Name) });
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                    using (var ncaFile = pfs.OpenFile(fileEntry, OpenMode.Read))
                    using (var outStream = File.Open(outPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        var nca = new Nca(KeySet, ncaFile.AsStorage());
                        outStream.SetLength(nca.Header.NcaSize);

                        if (!isCompressed)
                        {
                            ncaFile.AsStream().CopyTo(outStream, 4 * 1024 * 1024);
                        }
                        else
                        {
                            ncaFile.GetSize(out long ncaSize).ThrowIfFailure();
                            if (ncaSize < 0x4000 + 16 + 1)
                            {
                                throw new Exception("Invalid NCZ fileEntry");
                            }

                            using (var inStream = ncaFile.AsStream())
                            using (var br = new BinaryReader(inStream))
                            {
                                br.BaseStream.Seek(0, SeekOrigin.Begin);

                                var ncaHeader = new byte[0x4000];
                                br.BaseStream.Read(ncaHeader, 0, ncaHeader.Length);

                                outStream.Write(ncaHeader, 0, ncaHeader.Length);

                                br.BaseStream.Seek(0x4000, SeekOrigin.Begin);
                                string magic = br.ReadAscii(8);
                                if (magic != "NCZSECTN")
                                {
                                    throw new Exception("Invalid NCZ fileEntry");
                                }

                                var sections = new List<NczSection>();

                                ulong sectionCount = br.ReadUInt64();
                                for (ulong i = 0; i < sectionCount; ++i)
                                {
                                    var s = new NczSection
                                    {
                                        Offset = br.ReadUInt64(),
                                        Size = br.ReadUInt64(),
                                        CryptoType = (NcaEncryptionType)br.ReadUInt64(),
                                    };

                                    if (s.CryptoType == NcaEncryptionType.AesCtrEx)
                                        throw new NotImplementedException("NCZ uses AesCtrEx crypto, which is not yet supported");

                                    br.ReadUInt64(); // Padding

                                    s.CryptoKey = br.ReadBytes(16);
                                    s.CryptoCounter = br.ReadBytes(16);

                                    sections.Add(s);
                                }

                                long pos = br.BaseStream.Position;
                                ulong blockMagic = br.ReadUInt64();
                                if (blockMagic == 0x44414E414E43465F) // NCZSECTN
                                {
                                    br.ReadByte(); // Version
                                    br.ReadByte(); // Type
                                    br.ReadByte(); // Unused
                                    br.ReadByte(); // Block size exponent
                                    var numBlocks = br.ReadInt32(); // Number of blocks
                                    br.ReadInt64(); // Decompressed size
                                    br.ReadBytes(4 * numBlocks); // Blocks

                                    throw new NotImplementedException("NCZ uses block compression, which is not yet supported");
                                }
                                else
                                {
                                    br.BaseStream.Position = pos;
                                }

                                long nczDataStartPos = br.BaseStream.Position;
                                var nczSize = ncaSize - nczDataStartPos;

                                using (var decompressionStream = new DecompressionStream(br.BaseStream))
                                {
                                    sections.ForEach(section =>
                                    {
                                        Aes128CtrTransform aes = null;
                                        bool isEncrypted = section.CryptoType == NcaEncryptionType.AesCtr;
                                        if (isEncrypted)
                                        {
                                            aes = new Aes128CtrTransform(section.CryptoKey, section.CryptoCounter);
                                        }

                                        var i = section.Offset;
                                        var end = i + section.Size;

                                        if (section == sections.First())
                                        {
                                            var uncompressedSize = 0x4000 - section.Offset;
                                            if (uncompressedSize > 0)
                                                i += uncompressedSize;
                                        }

                                        Debug.Assert(outStream.Position == (long)section.Offset);
                                        var initialCounter = new byte[16];
                                        Array.Copy(section.CryptoCounter, initialCounter, 8);

                                        var inputChunk = new byte[0x100000];

                                        while (i < end)
                                        {
                                            var chunkSz = (int)Math.Min(end - i, 0x100000);
                                            var readCnt = decompressionStream.Read(inputChunk.AsSpan(0, chunkSz));

                                            Debug.Assert(readCnt == chunkSz);

                                            if (readCnt == 0)
                                                break;

                                            if (isEncrypted)
                                            {
                                                var cl = (byte[])inputChunk.Clone();

                                                using (var ms = new MemoryStream(cl))
                                                {
                                                    UpdateCounter(aes.Counter, (long)i);
                                                    aes.TransformBlock(inputChunk.AsSpan());
                                                }
                                            }

                                            outStream.Write(inputChunk, 0, readCnt);

                                            i += (ulong)readCnt;
                                        }
                                    });
                                }
                            }
                        }
                    }
                });
            }

            SaveKeys();
        }

        public void UninstallTitleFromNand(string titleIdHex, bool strict = false)
        {
            UninstallTitleFromNand(ulong.Parse(titleIdHex, System.Globalization.NumberStyles.HexNumber), strict);
        }

        public void UninstallTitleFromNand(ulong titleId, bool strict)
        {
            if (IsYuzuRunning())
                throw new Exception("Cannot uninstall game while Yuzu is running");

            if (!strict)
            {
                titleId &= GameTitleMask;
            }

            var toDelete = new List<string>();

            var fileEnumerator = new SafeFileEnumerator(Path.Combine(NandPath, "user", "Contents", "registered"), "*.nca", SearchOption.AllDirectories);
            foreach (var ncaFileInfo in fileEnumerator)
            {
                using (var fs = File.Open(ncaFileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var nca = new Nca(KeySet, fs.AsStorage());
                    var titleToMatch = strict ? nca.Header.TitleId : (nca.Header.TitleId & GameTitleMask);
                    if (titleId == titleToMatch)
                    {
                        toDelete.Add(ncaFileInfo.FullName);
                    }
                }
            }

            toDelete.ForEach(f =>
            {
                var info = new FileInfo(f);
                info.Delete();
                if (!info.Directory.EnumerateFileSystemInfos().Any())
                {
                    info.Directory.Delete();
                }
            });
        }

        public string GetLaunchPathFromTitleId(ulong titleId)
        {
            return Path.Combine(new string[] { Path.Combine(NandPath), "user", "Contents", "registered", GetLaunchSubPathFromTitleId(titleId) });
        }

        public string GetLaunchSubPathFromTitleId(ulong titleId)
        {
            var fileEnumerator = new SafeFileEnumerator(Path.Combine(NandPath, "user", "Contents", "registered"), "*.nca", SearchOption.AllDirectories);
            foreach (var ncaFileInfo in fileEnumerator)
            {
                using (var fs = File.Open(ncaFileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var nca = new Nca(KeySet, fs.AsStorage());
                    if (nca.Header.TitleId != titleId || nca.Header.ContentType != NcaContentType.Meta)
                        continue;

                    var gameNcas = new List<string>();
                    using (var ncaFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.IgnoreOnInvalid))
                    {
                        string cnmtPath = ncaFs.EnumerateEntries("/", "*.cnmt").Single().FullPath;
                        if (ncaFs.OpenFile(out var metaFile, cnmtPath, OpenMode.Read).IsSuccess())
                        {
                            using (metaFile)
                            {
                                var meta = new Cnmt(metaFile.AsStream());
                                foreach (var entry in meta.ContentEntries)
                                {
                                    if (entry.Type == LibHac.Ncm.ContentType.Program)
                                    {
                                        return GetRelativePathFromNcaId(entry.NcaId).Replace('/', '\\');
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public string GetLaunchPathFromFile(string filePath)
        {
            return Path.Combine(Path.Combine(NandPath), GetLaunchSubPathFromFile(filePath));
        }

        public string GetLaunchSubPathFromFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                throw new ArgumentException($"File \"{filePath}\" does not exist!");
            }

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                PartitionFileSystem pfs;

                switch (fileInfo.Extension)
                {
                    case ".xci":
                    case ".xcz":
                        var xci = new Xci(KeySet, fileStream.AsStorage());
                        pfs = xci.OpenPartition(XciPartitionType.Secure);
                        break;
                    case ".nsp":
                    case ".nsz":
                        pfs = new PartitionFileSystem(fileStream.AsStorage());
                        break;
                    default:
                        throw new ArgumentException($"File \"{filePath}\" is not an XCI or NSP file.");
                }

                using (pfs)
                {
                    foreach (var fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                    {
                        ImportTickets(pfs);

                        pfs.OpenFile(out IFile ncaFile, fileEntry.FullPath, OpenMode.Read).ThrowIfFailure();
                        using (ncaFile)
                        {
                            var nca = new Nca(KeySet, ncaFile.AsStorage());
                            if (nca.Header.ContentType == NcaContentType.Program)
                            {
                                return GetRelativePathFromNcaId(fileEntry.Name);
                            }
                        }
                    }
                }
            }

            return null;
        }

        private ExternalGameFileInfo ProcessInstalledGameFromCmntNca(Nca cnmtNca)
        {
            var info = new ExternalGameFileInfo();
            bool haveCnmt = false;
            bool haveNacp = false;
            bool haveProgram = false;

            using (var fs = cnmtNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.IgnoreOnInvalid))
            {
                string cnmtPath = fs.EnumerateEntries("/", "*.cnmt").Single().FullPath;
                if (fs.OpenFile(out var metaFile, cnmtPath, OpenMode.Read).IsSuccess())
                {
                    using (metaFile)
                    {
                        haveCnmt = true;
                        var cnmt = new Cnmt(metaFile.AsStream());
                        info.BaseTitleId = cnmt.ApplicationTitleId;
                        info.TitleId = cnmt.TitleId;
                        info.DisplayVersion = cnmt.TitleVersion.ToString();
                        info.Version = cnmt.TitleVersion.Version;
                        switch (cnmt.Type)
                        {
                            case LibHac.Ncm.ContentMetaType.Application:
                                info.Type = ExternalGameFileType.Game;
                                break;
                            case LibHac.Ncm.ContentMetaType.Patch:
                                info.Type = ExternalGameFileType.Update;
                                break;
                            case LibHac.Ncm.ContentMetaType.AddOnContent:
                                info.Type = ExternalGameFileType.DLC;
                                break;
                        }

                        if (info.Type != ExternalGameFileType.DLC)
                        {
                            foreach (var entry in cnmt.ContentEntries)
                            {
                                if (entry.Type != LibHac.Ncm.ContentType.DeltaFragment)
                                {
                                    var ncaPathOther = Path.Combine(new string[] { NandPath, "user", "Contents", "registered", GetRelativePathFromNcaId(entry.NcaId) });
                                    using (var ncaFileOther = File.Open(ncaPathOther, FileMode.Open, FileAccess.Read, FileShare.Read))
                                    {
                                        var ncaOther = new Nca(KeySet, ncaFileOther.AsStorage());
                                        switch (ncaOther.Header.ContentType)
                                        {
                                            case NcaContentType.Control:
                                                var cfs = ncaOther.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                                                cfs.OpenFile(out IFile controlFile, "/control.nacp", OpenMode.Read);
                                                using (controlFile)
                                                {
                                                    var nacp = new BlitStruct<ApplicationControlProperty>(1);
                                                    controlFile.Read(out _, 0, nacp.ByteSpan, ReadOption.None);

                                                    haveNacp = true;
                                                    info.TitleName = nacp.Value.Titles.ToArray().FirstOrDefault(t => !t.Name.ToString().IsNullOrWhiteSpace()).Name.ToString();
                                                    if (info.TitleName.IsNullOrWhiteSpace())
                                                    {
                                                        _logger.Warn("Empty title name");
                                                    }
                                                    info.Publisher = nacp.Value.Titles.ToArray().FirstOrDefault(t => !t.Publisher.ToString().IsNullOrWhiteSpace()).Publisher.ToString();
                                                    var displayVersion = nacp.Value.DisplayVersion.ToString();
                                                    if (!displayVersion.IsNullOrWhiteSpace())
                                                    {
                                                        info.DisplayVersion = displayVersion;
                                                    }
                                                }
                                                break;
                                            case NcaContentType.Program:
                                                haveProgram = true;
                                                info.LaunchSubPath = GetRelativePathFromNcaId(entry.NcaId).Replace('/', '\\'); ;
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (!haveCnmt || (info.Type != ExternalGameFileType.DLC && (!haveNacp || !haveProgram)))
                    return null;
            }

            return info;
        }

        public IEnumerable<SourceDirCache.CacheGameInstalled> GetInstalledGames(CancellationToken tk)
        {
            var ret = new List<SourceDirCache.CacheGameInstalled>();
            var intermediate = new Dictionary<ulong, List<ExternalGameFileInfo>>();

            var installedNcaDir = Path.Combine(NandPath, "user", "Contents", "registered");
            if (!Directory.Exists(installedNcaDir))
            {
                throw new DirectoryNotFoundException(installedNcaDir);
            }

            var fileEnumerator = new SafeFileEnumerator(installedNcaDir, "*.nca", SearchOption.AllDirectories);
            foreach (var file in fileEnumerator)
            {
                if (tk.IsCancellationRequested)
                    break;

                using (var ncaFile = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var cnmtNca = new Nca(KeySet, ncaFile.AsStorage());
                    if (cnmtNca.Header.ContentType != NcaContentType.Meta)
                        continue;

                    ExternalGameFileInfo info = null;
                    try
                    {
                        info = ProcessInstalledGameFromCmntNca(cnmtNca);
                    }
                    catch
                    {
                        _logger.Warn($"Failed to process installed game from {file.FullName}");
                    }

                    if (info != null)
                    {
                        if (!intermediate.ContainsKey(info.BaseTitleId))
                            intermediate.Add(info.BaseTitleId, new List<ExternalGameFileInfo>());

                        intermediate[info.BaseTitleId].Add(info);
                    }
                }
            }

            foreach (var k in intermediate.Keys)
            {
                var cgu = new SourceDirCache.CacheGameInstalled();
                var game = intermediate[k].FirstOrDefault(x => x.Type == ExternalGameFileType.Game);
                if (game == null)
                    continue;

                var latestUpdate = intermediate[k].Where(x => x.Type == ExternalGameFileType.Update).OrderByDescending(x => x.Version).FirstOrDefault();

                cgu.TitleId = game.TitleId;
                cgu.Title = game.TitleName;
                cgu.Version = latestUpdate?.DisplayVersion ?? game.DisplayVersion;
                cgu.Publisher = game.Publisher;

                cgu.ProgramNcaSubPath = game.LaunchSubPath;

                ret.Add(cgu);
            }

            return ret;
        }

        public ExternalGameFileInfo GetExternalGameFileInfo(string filePath)
        {
            var info = new ExternalGameFileInfo();
            bool haveCnmt = false;
            bool haveNacp = false;
            bool haveProgram = false;

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                throw new ArgumentException($"Path \"{filePath}\" does not exist");

            if (!ValidGameExtensions.Contains(fileInfo.Extension))
                throw new ArgumentException($"Unsupported file extension \"{fileInfo.Extension}\"");

            info.FilePath = filePath;

            using (var fileStream = fileInfo.OpenRead())
            {
                PartitionFileSystem pfs;
                try
                {
                    switch (fileInfo.Extension)
                    {
                        case ".xci":
                        case ".xcz":
                            var xci = new Xci(KeySet, fileStream.AsStorage());
                            pfs = xci.OpenPartition(XciPartitionType.Secure);
                            info.FileType = FileType.XCI;
                            break;
                        case ".nsp":
                        case ".nsz":
                            pfs = new PartitionFileSystem(fileStream.AsStorage());
                            info.FileType = FileType.NSP;
                            break;
                        default:
                            throw new ArgumentException($"File \"{filePath}\" is not an XCI or NSP file.");
                    }
                }
                catch
                {
                    _logger.Warn($"Failed to open partition. Skipping file \"{filePath}\".");
                    return null;
                }

                using (pfs)
                {
                    ImportTickets(pfs);

                    var fileEntries = pfs.EnumerateEntries("/", "*").Where(f => { return f.FullPath.ToLower().Contains("cnmt") && Path.GetExtension(f.FullPath).ToLower() == ".nca"; });
                    if (!fileEntries.Any())
                    {
                        _logger.Warn($"Failed to find any cnmt entry. Skipping file \"{filePath}\".");
                        return null;
                    }
                    else if (fileEntries.Count() > 1)
                    {
                        _logger.Warn($"Found more than one cnmt entry. Using first one found in \"{filePath}\".");
                    }

                    var fileEntry = fileEntries.First();

                    pfs.OpenFile(out IFile ncaFile, fileEntry.FullPath, OpenMode.Read).ThrowIfFailure();
                    using (ncaFile)
                    {
                        var cnmtNca = new Nca(KeySet, ncaFile.AsStorage());
                        using (var fs = cnmtNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.IgnoreOnInvalid))
                        {
                            string cnmtPath = fs.EnumerateEntries("/", "*.cnmt").Single().FullPath;
                            if (fs.OpenFile(out var metaFile, cnmtPath, OpenMode.Read).IsSuccess())
                            {
                                using (metaFile)
                                {
                                    var cnmt = new Cnmt(metaFile.AsStream());
                                    haveCnmt = true;
                                    info.BaseTitleId = cnmt.ApplicationTitleId;
                                    info.TitleId = cnmt.TitleId;
                                    info.DisplayVersion = cnmt.TitleVersion.ToString();
                                    info.Version = cnmt.TitleVersion.Version;
                                    switch (cnmt.Type)
                                    {
                                        case LibHac.Ncm.ContentMetaType.Application:
                                            info.Type = ExternalGameFileType.Game;
                                            break;
                                        case LibHac.Ncm.ContentMetaType.Patch:
                                            info.Type = ExternalGameFileType.Update;
                                            break;
                                        case LibHac.Ncm.ContentMetaType.AddOnContent:
                                            info.Type = ExternalGameFileType.DLC;
                                            break;
                                    }

                                    if (info.Type != ExternalGameFileType.DLC)
                                    {
                                        foreach (var entry in cnmt.ContentEntries.Where(e => e.Type != LibHac.Ncm.ContentType.DeltaFragment))
                                        {
                                            bool isNcz = false;
                                            var ncaOtherFileName = string.Format("{0}.nca", BitConverter.ToString(entry.NcaId).Replace("-", "").ToLower());
                                            if (!pfs.FileExists(ncaOtherFileName))
                                            {
                                                // If file exists but with ncz, that's not worth logging. The data is there, just not readable directly.
                                                // However, do log if it's completely missing. (Corrupt PFS?)
                                                var altName = ncaOtherFileName.Replace(".nca", ".ncz");
                                                if (pfs.FileExists(altName))
                                                {
                                                    ncaOtherFileName = altName;
                                                    isNcz = true;
                                                }
                                                else
                                                {
                                                    _logger.Warn($"Failed to find NCA file \"{ncaOtherFileName}\" in \"{filePath}\". Skipping...");
                                                }
                                            }

                                            pfs.OpenFile(out var ncaFileOther, ncaOtherFileName, OpenMode.Read).ThrowIfFailure();
                                            using (ncaFileOther)
                                            {
                                                var ncaOther = new Nca(KeySet, ncaFileOther.AsStorage());
                                                switch (ncaOther.Header.ContentType)
                                                {
                                                    case NcaContentType.Control:
                                                        if (isNcz)
                                                        {
                                                            // This shouldn't happen. XCZ/NSZ files typically don't use NCZ for control NCA
                                                            _logger.Warn($"Control NCA is inside NCZ for \"{filePath}\". Cannot look up NACP data.");
                                                            info.TitleName = fileInfo.Extension.Length > 0 ? fileInfo.Name.Remove(fileInfo.Name.Length - fileInfo.Extension.Length) : fileInfo.Name;
                                                            info.Publisher = "";
                                                            info.DisplayVersion = "";
                                                            continue;
                                                        }

                                                        var cfs = ncaOther.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                                                        cfs.OpenFile(out IFile controlFile, "/control.nacp", OpenMode.Read);

                                                        var nacp = new BlitStruct<ApplicationControlProperty>(1);
                                                        controlFile.Read(out long nacpReadCnt, 0, nacp.ByteSpan, ReadOption.None);

                                                        if (nacpReadCnt != nacp.ByteSpan.Length)
                                                        {
                                                            _logger.Warn($"Failed to read NACP data for \"{filePath}\". Skipping...");
                                                            continue;
                                                        }


                                                        haveNacp = true;
                                                        // (first is American English, then British English, and then fall back to any)
                                                        var titlesArr = nacp.Value.Titles.ToArray();
                                                        var nameToUse = titlesArr.Select(t =>
                                                        {
                                                            return t.Name.ToString();
                                                        }).FirstOrDefault(t =>
                                                        {
                                                            return !t.IsNullOrWhiteSpace();
                                                        });
                                                        info.TitleName = nameToUse;

                                                        if (info.TitleName.IsNullOrWhiteSpace())
                                                        {
                                                            _logger.Warn("Empty title name");
                                                        }
                                                        var publisherToUse = titlesArr.Select(p =>
                                                        {
                                                            return p.Publisher.ToString();
                                                        }).FirstOrDefault(p =>
                                                        {
                                                            return p.IsNullOrWhiteSpace();
                                                        });

                                                        info.Publisher = publisherToUse;

                                                        var displayVersion = nacp.Value.DisplayVersion.ToString();
                                                        if (!displayVersion.IsNullOrWhiteSpace())
                                                        {
                                                            info.DisplayVersion = displayVersion;
                                                        }
                                                        break;
                                                    case NcaContentType.Program:
                                                        haveProgram = true;
                                                        info.LaunchSubPath = GetRelativePathFromNcaId(entry.NcaId).Replace('/', '\\');
                                                        break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (!haveCnmt || (info.Type != ExternalGameFileType.DLC && (!haveNacp || !haveProgram)))
                return null;

            return info;
        }

        private readonly HashSet<string> ValidGameExtensions = new HashSet<string>()
        {
            ".xci",
            ".xcz",
            ".nsp",
            ".nsz",
        };

        public IEnumerable<SourceDirCache.CacheGameUninstalled> GetUninstalledGamesFromDir(string path, CancellationToken tk)
        {
            var ret = new List<SourceDirCache.CacheGameUninstalled>();
            var intermediate = new Dictionary<ulong, List<ExternalGameFileInfo>>();

            var fileEnumerator = new SafeFileEnumerator(path, "*.*", SearchOption.AllDirectories);

            foreach (var file in fileEnumerator.Where(f => { return (!f.Attributes.HasFlag(FileAttributes.Directory)) && ValidGameExtensions.Contains(f.Extension); }))
            {
                if (tk.IsCancellationRequested)
                    break;

                ExternalGameFileInfo extGameInfo = null;
                try
                {
                    extGameInfo = GetExternalGameFileInfo(file.FullName);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to get external game info for {file.FullName}. Skipping...");
                }

                if (extGameInfo != null)
                {
                    if (!intermediate.TryGetValue(extGameInfo.BaseTitleId, out List<ExternalGameFileInfo> value))
                    {
                        value = new List<ExternalGameFileInfo>();
                        intermediate.Add(extGameInfo.BaseTitleId, value);
                    }

                    value.Add(extGameInfo);
                }
            }

            foreach (var k in intermediate.Keys)
            {
                var cgu = new SourceDirCache.CacheGameUninstalled();
                var game = intermediate[k].FirstOrDefault(x => x.FileType == FileType.NSP && x.Type == ExternalGameFileType.Game)
                    ?? intermediate[k].FirstOrDefault(x => x.FileType == FileType.XCI);
                if (game == null)
                    continue;

                cgu.TitleId = game.TitleId;
                cgu.Title = game.TitleName;
                cgu.Version = game.DisplayVersion;
                cgu.Publisher = game.Publisher;

                cgu.ProgramFile = game.FilePath;

                var update = intermediate[k].Where(x => x.FileType == FileType.NSP && x.Type == ExternalGameFileType.Update).OrderByDescending(x => x.Version).FirstOrDefault();
                if (update != null)
                {
                    cgu.UpdateFile = update.FilePath;
                }

                cgu.DlcFiles.AddRange(
                    intermediate[k].Where(x => x.FileType == FileType.NSP && x.Type == ExternalGameFileType.DLC)
                    .GroupBy(x => x.TitleId, (key, g) => g.First().FilePath)
                    );

                ret.Add(cgu);
            }

            return ret;
        }

        private static string GetRelativePathFromNcaId(string ncaFileName)
        {
            string ncaFileNameHex = ncaFileName.Substring(0, 32);
            var ncaId = Enumerable.Range(0, ncaFileNameHex.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(ncaFileNameHex.Substring(x, 2), 16)).ToArray();
            using (var hasher = SHA256.Create())
            {
                var hash = hasher.ComputeHash(ncaId);
                return string.Format(@"000000{0:X2}{1}{2}.nca", hash[0], Path.DirectorySeparatorChar, ncaFileNameHex.ToLower());
            }
        }

        private static string GetRelativePathFromNcaId(byte[] ncaId)
        {
            string ncaFileNameHex = string.Format("{0}", BitConverter.ToString(ncaId).Replace("-", "").ToLower());
            using (var hasher = SHA256.Create())
            {
                var hash = hasher.ComputeHash(ncaId);
                return string.Format(@"000000{0:X2}{1}{2}.nca", hash[0], Path.DirectorySeparatorChar, ncaFileNameHex.ToLower());
            }
        }
    }
}
