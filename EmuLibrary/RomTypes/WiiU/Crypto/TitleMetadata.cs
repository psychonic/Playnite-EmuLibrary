using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // A single content (.app) entry from the TMD. All fields big-endian on disk; decoded here.
    internal sealed class TmdContent
    {
        public uint Id;       // .app file id (filename is "%08x.app")
        public ushort Index;
        public ushort Type;   // bit 0x02 => hash-tree protected content
        public ulong Size;

        public bool IsHashed => (Type & 0x02) != 0;
    }

    // Parsed Wii U TMD (title.tmd). The signature/cert layout is fixed for Wii U (RSA-2048 SHA-256), so all
    // field offsets are constant. Layout per VitaSmith/cdecrypt's TitleMetaData:
    //   0x140 Issuer[0x40], 0x180 Version(u8), 0x18C TitleID(u64), 0x1DC TitleVersion(u16),
    //   0x1DE ContentCount(u16), 0x204 ContentInfo[64] (0x24 each), 0xB04 Content[] (0x30 each).
    internal sealed class TitleMetadata
    {
        private const int IssuerOffset = 0x140;
        private const int VersionOffset = 0x180;
        private const int TitleIdOffset = 0x18C;
        private const int TitleVersionOffset = 0x1DC;
        private const int ContentCountOffset = 0x1DE;
        private const int ContentsOffset = 0xB04;
        private const int ContentSize = 0x30;

        // The two valid TMD issuers and the common key each selects.
        private const string RetailIssuer = "Root-CA00000003-CP0000000b";
        private const string DevIssuer = "Root-CA00000004-CP00000010";

        public string Issuer { get; private set; }
        public byte Version { get; private set; }
        public ulong TitleId { get; private set; }
        public ushort TitleVersion { get; private set; }
        public bool IsDev { get; private set; }
        public IReadOnlyList<TmdContent> Contents { get; private set; }

        // The base title id with the content-type bits of the high word masked out (game/update/DLC of one
        // title share this). High word: 0x00050000 game, 0x0005000E update, 0x0005000C DLC.
        public ulong BaseTitleId => TitleId & 0xFFFFFF00FFFFFFFFUL;

        public WiiUContentKind Kind
        {
            get
            {
                switch ((byte)((TitleId >> 32) & 0xFF))
                {
                    case 0x0E: return WiiUContentKind.Update;
                    case 0x0C: return WiiUContentKind.Dlc;
                    default: return WiiUContentKind.Game;
                }
            }
        }

        public static TitleMetadata Parse(byte[] tmd)
        {
            if (tmd == null || tmd.Length < ContentsOffset)
                throw new ArgumentException("TMD is too small.", nameof(tmd));

            var version = tmd[VersionOffset];
            if (version != 1)
                throw new NotSupportedException($"Unsupported TMD version: {version}.");

            var issuer = ReadCString(tmd, IssuerOffset, 0x40);
            bool isDev;
            if (issuer == RetailIssuer)
                isDev = false;
            else if (issuer == DevIssuer)
                isDev = true;
            else
                throw new NotSupportedException($"Unknown TMD issuer: \"{issuer}\".");

            ushort contentCount = Be.U16(tmd, ContentCountOffset);
            int needed = ContentsOffset + contentCount * ContentSize;
            if (tmd.Length < needed)
                throw new ArgumentException($"TMD too small for {contentCount} contents.", nameof(tmd));

            var contents = new List<TmdContent>(contentCount);
            for (int i = 0; i < contentCount; i++)
            {
                int o = ContentsOffset + i * ContentSize;
                contents.Add(new TmdContent
                {
                    Id = Be.U32(tmd, o + 0),
                    Index = Be.U16(tmd, o + 4),
                    Type = Be.U16(tmd, o + 6),
                    Size = Be.U64(tmd, o + 8),
                });
            }

            return new TitleMetadata
            {
                Issuer = issuer,
                Version = version,
                TitleId = Be.U64(tmd, TitleIdOffset),
                TitleVersion = Be.U16(tmd, TitleVersionOffset),
                IsDev = isDev,
                Contents = contents,
            };
        }

        private static string ReadCString(byte[] buf, int offset, int maxLen)
        {
            int end = offset;
            int limit = Math.Min(buf.Length, offset + maxLen);
            while (end < limit && buf[end] != 0)
                end++;
            return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset);
        }
    }

    // Big-endian readers (Wii U formats are big-endian throughout).
    internal static class Be
    {
        public static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        public static uint U32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        public static ulong U64(byte[] b, int o) =>
            ((ulong)U32(b, o) << 32) | U32(b, o + 4);
    }
}
