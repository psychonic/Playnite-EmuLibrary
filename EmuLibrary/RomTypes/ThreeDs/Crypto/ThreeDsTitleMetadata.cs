using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // The content category encoded in a 3DS title id's high word (bits 32..47): base game, update/patch, DLC,
    // or demo. Base/update/DLC of one title share the rest of the id.
    internal enum ThreeDsContentKind
    {
        Application,
        Update,
        Dlc,
        Demo,
        Other,
    }

    // One content chunk record from a 3DS tmd. All fields big-endian on disk.
    internal sealed class ThreeDsTmdContent
    {
        public uint Id;       // content id; the SD store names the file "<id:08x>.app"
        public ushort Index;  // ordinal within the title (the CIA content order / titlekey IV)
        public ushort Type;   // bit 0x0001 encrypted, 0x4000 optional (DLC), 0x8000 shared
        public ulong Size;

        public bool IsEncrypted => (Type & 0x0001) != 0;
        public bool IsOptional => (Type & 0x4000) != 0;
    }

    // Parsed 3DS tmd. Unlike the Wii U tmd (fixed RSA-2048 layout) the 3DS signature type varies, so the
    // header offset is computed from the leading signature type. Layout after the signature block:
    //   +0x4C title_id(u64), +0x9C title_version(u16), +0x9E content_count(u16),
    //   +0xC4 content-info records (64 * 0x24), +0x9C4 content chunk records (count * 0x30).
    internal sealed class ThreeDsTitleMetadata
    {
        private const int TitleIdOffset = 0x4C;
        private const int TitleVersionOffset = 0x9C;
        private const int ContentCountOffset = 0x9E;
        private const int ContentInfoOffset = 0xC4;
        private const int ContentChunkOffset = 0xC4 + 0x900;
        private const int ContentChunkSize = 0x30;

        public ulong TitleId { get; private set; }
        public ushort TitleVersion { get; private set; }
        public IReadOnlyList<ThreeDsTmdContent> Contents { get; private set; }

        // Group key: the title id with the content-category bits of the high word cleared (base/update/DLC of
        // one title collapse to this). High word 0x0004000E (update) / 0x0004008C (DLC) -> 0x00040000.
        public ulong BaseTitleId => TitleId & 0xFFFF0000FFFFFFFFUL;

        public ThreeDsContentKind Kind
        {
            get
            {
                switch ((ushort)((TitleId >> 32) & 0xFFFF))
                {
                    case 0x000E: return ThreeDsContentKind.Update;
                    case 0x008C: return ThreeDsContentKind.Dlc;
                    case 0x0002: return ThreeDsContentKind.Demo;
                    case 0x0000: return ThreeDsContentKind.Application;
                    default: return ThreeDsContentKind.Other;
                }
            }
        }

        public ulong TotalContentSize
        {
            get
            {
                ulong total = 0;
                foreach (var c in Contents)
                    total += c.Size;
                return total;
            }
        }

        public static ThreeDsTitleMetadata Parse(byte[] tmd)
        {
            if (tmd == null)
                throw new ArgumentNullException(nameof(tmd));

            int sig = SignatureBlockSize(tmd);
            int header = sig;

            if (tmd.Length < header + ContentChunkOffset)
                throw new ArgumentException("3DS tmd is too small.", nameof(tmd));

            ushort contentCount = Be.U16(tmd, header + ContentCountOffset);
            int chunksAt = header + ContentChunkOffset;
            if (tmd.Length < chunksAt + contentCount * ContentChunkSize)
                throw new ArgumentException($"3DS tmd too small for {contentCount} contents.", nameof(tmd));

            var contents = new List<ThreeDsTmdContent>(contentCount);
            for (int i = 0; i < contentCount; i++)
            {
                int o = chunksAt + i * ContentChunkSize;
                contents.Add(new ThreeDsTmdContent
                {
                    Id = Be.U32(tmd, o + 0x00),
                    Index = Be.U16(tmd, o + 0x04),
                    Type = Be.U16(tmd, o + 0x06),
                    Size = Be.U64(tmd, o + 0x08),
                });
            }

            return new ThreeDsTitleMetadata
            {
                TitleId = Be.U64(tmd, header + TitleIdOffset),
                TitleVersion = Be.U16(tmd, header + TitleVersionOffset),
                Contents = contents,
            };
        }

        // The signed-blob signature block size (signature + padding to 0x40) from the leading u32 type, plus
        // the 4 type bytes themselves. Shared shape used by both tmd and ticket.
        public static int SignatureBlockSize(byte[] data)
        {
            if (data == null || data.Length < 4)
                throw new ArgumentException("Signed blob is too small to hold a signature type.");

            uint type = Be.U32(data, 0);
            switch (type)
            {
                case 0x00010000: // RSA-4096 SHA-1
                case 0x00010003: // RSA-4096 SHA-256
                    return 4 + 0x200 + 0x3C;
                case 0x00010001: // RSA-2048 SHA-1
                case 0x00010004: // RSA-2048 SHA-256
                    return 4 + 0x100 + 0x3C;
                case 0x00010002: // ECDSA SHA-1
                case 0x00010005: // ECDSA SHA-256
                    return 4 + 0x3C + 0x40;
                default:
                    throw new NotSupportedException($"Unknown 3DS signature type 0x{type:X8}.");
            }
        }

        public static ThreeDsContentKind KindFromTitleId(ulong titleId)
        {
            switch ((ushort)((titleId >> 32) & 0xFFFF))
            {
                case 0x000E: return ThreeDsContentKind.Update;
                case 0x008C: return ThreeDsContentKind.Dlc;
                case 0x0002: return ThreeDsContentKind.Demo;
                case 0x0000: return ThreeDsContentKind.Application;
                default: return ThreeDsContentKind.Other;
            }
        }

        public static ulong MaskBaseTitleId(ulong titleId) => titleId & 0xFFFF0000FFFFFFFFUL;
    }
}
