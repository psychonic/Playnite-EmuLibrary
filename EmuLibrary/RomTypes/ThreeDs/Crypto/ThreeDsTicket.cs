using System;
using System.Security.Cryptography;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // A parsed 3DS ticket (cetk / .tik). The ticket data follows the same signature block as the tmd. Layout
    // after the signature block: +0x7F titleKey(0x10, encrypted), +0x90 ticketId(u64), +0x9C titleId(u64),
    // +0xB1 commonKeyIndex(u8). The titlekey is AES-128-CBC encrypted under the common key selected by the
    // index, IV = titleId(8 BE) zero-padded to 16.
    internal sealed class ThreeDsTicket
    {
        private const int TitleKeyOffset = 0x7F;
        private const int TicketIdOffset = 0x90;
        private const int TitleIdOffset = 0x9C;
        private const int CommonKeyIndexOffset = 0xB1;

        public ulong TitleId { get; private set; }
        public ulong TicketId { get; private set; }
        public byte CommonKeyIndex { get; private set; }
        public byte[] EncryptedTitleKey { get; private set; }

        public static ThreeDsTicket Parse(byte[] ticket)
        {
            if (ticket == null)
                throw new ArgumentNullException(nameof(ticket));

            int data = ThreeDsTitleMetadata.SignatureBlockSize(ticket);
            if (ticket.Length < data + 0x100)
                throw new ArgumentException("3DS ticket is too small.", nameof(ticket));

            var encKey = new byte[16];
            Buffer.BlockCopy(ticket, data + TitleKeyOffset, encKey, 0, 16);

            return new ThreeDsTicket
            {
                TitleId = Be.U64(ticket, data + TitleIdOffset),
                TicketId = Be.U64(ticket, data + TicketIdOffset),
                CommonKeyIndex = ticket[data + CommonKeyIndexOffset],
                EncryptedTitleKey = encKey,
            };
        }

        // Decrypts the titlekey using the common key the ticket selects. IV = titleId (8 bytes, big-endian)
        // followed by 8 zero bytes.
        public byte[] DecryptTitleKey(ThreeDsKeys keys) =>
            DecryptTitleKey(keys.GetTitleKeyDecryptionKey(CommonKeyIndex));

        public byte[] DecryptTitleKey(byte[] commonKey)
        {
            var iv = new byte[16];
            WriteBe64(iv, 0, TitleId);

            using (var aes = Aes.Create())
            {
                aes.Key = commonKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                {
                    var titleKey = new byte[16];
                    dec.TransformBlock(EncryptedTitleKey, 0, 16, titleKey, 0);
                    return titleKey;
                }
            }
        }

        private static void WriteBe64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buffer[offset + i] = (byte)(value >> (56 - i * 8));
        }
    }
}
