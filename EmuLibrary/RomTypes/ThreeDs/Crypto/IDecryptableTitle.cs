using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // The common shape of a 3DS title package the SD installer can consume, regardless of how the content is
    // physically laid out at the source. A CIA bundles tmd + ticket + concatenated content in one file; a CDN
    // folder keeps the tmd, an external .tik, and one file per content named by its hex content id. Both expose
    // the same primitives: the parsed tmd/ticket, the raw tmd/ticket bytes (written verbatim into the SD store),
    // the set of contents actually present, and a way to stream one content with its titlekey CBC layer stripped.
    internal interface IDecryptableTitle : IDisposable
    {
        ThreeDsTitleMetadata Tmd { get; }
        ThreeDsTicket Ticket { get; }

        // Raw section bytes, written to the SD store verbatim (00000000.tmd / the NAND ticket).
        byte[] RawTmd { get; }
        byte[] RawTicket { get; }

        // The tmd contents backed by data actually present at the source (a CIA's content-index bitmask, or the
        // content files that exist in a CDN folder).
        IEnumerable<ThreeDsTmdContent> PresentContents { get; }

        // Streams one content out with its titlekey AES-128-CBC layer removed (still an NCCH whose internal
        // crypto NcchDecryptor must then strip). IV = the content index as a 16-byte big-endian value.
        void DecryptContentTo(ThreeDsTmdContent content, byte[] titleKey, Stream dest, CancellationToken ct);

        // Reads just [offsetInContent, +length) of one content with the titlekey CBC layer removed, random
        // access (cheap even deep into a large content). Used to read a title's SMDH without decrypting it whole.
        // The returned array may be shorter than length if it reaches the content end.
        byte[] ReadDecryptedRange(ThreeDsTmdContent content, byte[] titleKey, long offsetInContent, int length);
    }
}
