using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK.Models;

namespace ROMManager
{
    class RMPathInfo
    {
        private readonly char FlagIsMultiFile = '*';

        // For multigame, we store...

        public RMPathInfo(Game game)
            : this(game.GameId)
        {
        }

        public RMPathInfo(GameInfo gameInfo)
            : this (gameInfo.GameId)
        {
        }

        public RMPathInfo(FileInfo info, bool isMultiFile)
        {
            IsMultiFile = isMultiFile;
            SourceRomFile = info;
            if (IsMultiFile)
            {
                SourceRomFolder = info.Directory;
            }
        }

        private RMPathInfo(string gameId)
        {
            var parts = gameId.Split('|');

            if (parts.Length > 1)
            {
                IsMultiFile = parts[0].Contains(FlagIsMultiFile);

                var fi = new FileInfo(parts[1]);

                SourceRomFile = fi;
                if (IsMultiFile)
                {
                    SourceRomFolder = fi.Directory;
                }
            }
            else
            {
                SourceRomFile = new FileInfo(parts[0]);
            }
        }

        public FileInfo SourceRomFile;
        public DirectoryInfo SourceRomFolder;
        public bool IsMultiFile;
        public string RelativeInstallPath
        {
            get
            {
                return IsMultiFile ? SourceRomFolder.Name : "";
            }
        }

        public string RelativeRomPath
        {
            get
            {
                return IsMultiFile ? Path.Combine(RelativeInstallPath, SourceRomFile.Name) : SourceRomFile.Name;
            }
        }

        public string ToGameId()
        {
            var flags = new System.Text.StringBuilder();
            if (IsMultiFile)
            {
                flags.Append(FlagIsMultiFile);
            }

            return string.Format("{0}|{1}", flags.ToString(), SourceRomFile.FullName);
        }

        public async Task CopyTo(string destinationFolder)
        {
            if (IsMultiFile)
            {
                var sourceFolder = new DirectoryInfo(SourceRomFolder.FullName);
                var fc = new FolderCopier()
                {
                    SourceFolder = sourceFolder,
                    DestinationFolder = new DirectoryInfo(Path.Combine(destinationFolder, sourceFolder.Name))
                };
                await fc.CopyAsync();
            }
            else
            {
                using (FileStream SourceStream = File.Open(SourceRomFile.FullName, FileMode.Open))
                {
                    using (FileStream DestinationStream = File.Create(Path.Combine(destinationFolder, SourceRomFile.Name)))
                    {
                        await SourceStream.CopyToAsync(DestinationStream);
                    }
                }
            }
        }
    }
}
