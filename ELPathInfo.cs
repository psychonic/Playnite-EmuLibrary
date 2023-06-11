using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK.Models;

namespace EmuLibrary
{
    class ELPathInfo
    {
        // GameId format - segments divided by '|'.
        // 0 - Was flag string, with only flag ever being * for multi-file. Now is base game path if multifile
        // 1 - Full Rom file source path
        // If no segments present (no '|'), then entire value is Full Rom file source path (1)

        public ELPathInfo(Game game)
            : this(game.GameId)
        {
        }

        public ELPathInfo(GameMetadata gameInfo)
            : this (gameInfo.GameId)
        {
        }

        public ELPathInfo(FileInfo fileInfo, DirectoryInfo dirInfo = null)
        {
            IsMultiFile = dirInfo != null;
            SourceRomFile = fileInfo;
            if (IsMultiFile)
            {
                SourceRomFolder = dirInfo;
            }
        }

        private ELPathInfo(string gameId)
        {
            var parts = gameId.Split('|');

            if (parts.Length > 1)
            {
                IsMultiFile = !string.IsNullOrEmpty(parts[0]);

                var fi = new FileInfo(parts[1]);

                SourceRomFile = fi;
                if (IsMultiFile)
                {
                    if (parts[0][0] != '*')
                    {
                        SourceRomFolder = new DirectoryInfo(parts[0]);
                    }
                    else
                    {
                        SourceRomFolder = fi.Directory;
                    }
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

        // Game folder/file relative to source or dest. (Ex. mygame.ext or mygame)
        public string RelativeInstallPath
        {
            get
            {
                return IsMultiFile ? SourceRomFolder.Name : "";
            }
        }

        // Game file relative to source or dest, for multifile games (Ex. mygame/somedir/blah.rpx)
        public string RelativeRomPath
        {
            get
            {
                return IsMultiFile ? Path.Combine(new string[] {SourceRomFolder.Name, SourceRomFile.Directory.FullName.Replace(SourceRomFolder.FullName, "").TrimStart('\\'), SourceRomFile.Name }) : SourceRomFile.Name;
            }
        }

        public string ToGameId()
        {
            return string.Format("{0}|{1}", IsMultiFile ? SourceRomFolder.FullName : "", SourceRomFile.FullName);
        }

        public async Task CopyTo(string destinationFolder, CancellationToken cancellationToken)
        {
            if (IsMultiFile)
            {
                var sourceFolder = new DirectoryInfo(SourceRomFolder.FullName);
                var fc = new FolderCopier()
                {
                    SourceFolder = sourceFolder,
                    DestinationFolder = new DirectoryInfo(Path.Combine(destinationFolder, sourceFolder.Name))
                };

                await fc.CopyAsync(cancellationToken);
            }
            else
            {
                using (FileStream SourceStream = File.Open(SourceRomFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (FileStream DestinationStream = File.Create(Path.Combine(destinationFolder, SourceRomFile.Name)))
                    {
                        await SourceStream.CopyToAsync(DestinationStream, 81920 /* default */, cancellationToken);
                    }
                }
            }
        }
    }
}
