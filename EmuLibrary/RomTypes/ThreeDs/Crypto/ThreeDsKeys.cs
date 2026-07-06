using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // The universal retail 3DS key material needed to decrypt titlekeys and NCCH content. EmuLibrary ships NO
    // keys; everything here is read from the user's OWN Azahar so we require nothing the user doesn't already
    // need to run it. Source order:
    //   1. an on-disk aes_keys.txt (sysdata, a mapping override, or beside the source)
    //   2. else extract Azahar's built-in keyblob from its binaries (ENABLE_BUILTIN_KEYBLOB is on by default)
    //      and zero-key-AES-CBC-decrypt it exactly as Azahar's GetKeysStream() does
    //   3. else: refuse (the same state in which Azahar itself couldn't run the title)
    internal sealed class ThreeDsKeys
    {
        // The bundled keyblob is AES-128-CBC(key=0, IV=0) of an aes_keys.txt whose first 16-byte plaintext
        // block is the ASCII "# Default keys f". With a zero IV the first ciphertext block is therefore a
        // fixed, key-free constant — a stable signature to locate the blob inside azahar.exe/core. It carries
        // no key material (it is the encryption of a comment string).
        private static readonly byte[] KeyblobSignature =
            { 0x4E, 0x81, 0xE9, 0x54, 0xCC, 0xDE, 0xFD, 0x56, 0x7D, 0xD2, 0x72, 0xE6, 0xD9, 0xCD, 0x8E, 0x11 };

        private const string KeyblobPlaintextPrefix = "# Default keys file bundled with Azahar";

        // Raw 16-byte key parts parsed from the Citra-format key file. Names match the file's tokens.
        private readonly Dictionary<string, byte[]> _parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // program-id -> 16-byte seed, from seeddb.bin (only needed for seed-crypto titles).
        private readonly Dictionary<ulong, byte[]> _seeds = new Dictionary<ulong, byte[]>();

        private byte[] _generatorConstant;

        private ThreeDsKeys() { }

        public bool HasGeneratorConstant => _generatorConstant != null;

        // The AES key that decrypts a ticket's titlekey for the given common-key index (0..5): the normal key
        // of slot 0x3D scrambled from slot0x3DKeyX + commonN (its KeyY). A directly-provided normal key
        // (slot0x3DKeyN) wins if present.
        public byte[] GetTitleKeyDecryptionKey(int commonKeyIndex)
        {
            if (_parts.TryGetValue("slot0x3DKeyN", out var normal))
                return normal;

            var keyX = Require("slot0x3DKeyX");
            var keyY = Require("common" + commonKeyIndex);
            return ThreeDsKeyScrambler.Derive(keyX, keyY, RequireGeneratorConstant());
        }

        // The NCCH KeyX for a crypto-method byte from the NCCH flags: 0x00 -> 0x2C, 0x01 -> 0x25,
        // 0x0A -> 0x18, 0x0B -> 0x1B.
        public byte[] GetNcchKeyX(byte cryptoMethod)
        {
            switch (cryptoMethod)
            {
                case 0x00: return Require("slot0x2CKeyX");
                case 0x01: return Require("slot0x25KeyX");
                case 0x0A: return Require("slot0x18KeyX");
                case 0x0B: return Require("slot0x1BKeyX");
                default: throw new NotSupportedException($"Unknown NCCH crypto method 0x{cryptoMethod:X2}.");
            }
        }

        public byte[] GeneratorConstant => RequireGeneratorConstant();

        public bool TryGetSeed(ulong programId, out byte[] seed) => _seeds.TryGetValue(programId, out seed);

        private byte[] Require(string token)
        {
            if (_parts.TryGetValue(token, out var v))
                return v;
            throw new Exception($"3DS key \"{token}\" is missing from the loaded Azahar keys.");
        }

        private byte[] RequireGeneratorConstant()
        {
            if (_generatorConstant == null)
                throw new Exception("3DS key \"generatorConstant\" is missing from the loaded Azahar keys.");
            return _generatorConstant;
        }

        // Resolves a mapping's keys, requiring NOTHING the user doesn't already need to run Azahar. Azahar's
        // OWN built-in keyblob is the PRIMARY (authoritative) source — a stock Azahar bundles the full set
        // (ENABLE_BUILTIN_KEYBLOB is on by default), so no user-supplied key file is needed and, when present,
        // the keyblob wins. An on-disk aes_keys.txt is only a FALLBACK: it fills anything the keyblob lacks
        // (e.g. keys for newer titles) and is the sole source on a source build compiled WITHOUT the keyblob.
        // Throws with a clear, actionable message only if neither yields keys.
        public static ThreeDsKeys Load(IEnumerable<string> keyFileCandidates, string emulatorBaseDir, ILogger logger)
        {
            var keys = new ThreeDsKeys();
            bool any = false;

            // 1) Primary: Azahar's built-in keyblob.
            if (!string.IsNullOrEmpty(emulatorBaseDir) && Directory.Exists(emulatorBaseDir))
            {
                try
                {
                    var text = TryExtractBuiltinKeyblob(emulatorBaseDir, logger);
                    if (text != null)
                    {
                        keys.ParseKeyText(text, fillOnly: true);
                        logger?.Info("[3DS] Loaded AES keys from Azahar's built-in keyblob.");
                        any = true;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, "[3DS] Failed to read Azahar's built-in keyblob.");
                }
            }

            // 2) Fallback: on-disk aes_keys.txt fills only what the keyblob did not provide.
            foreach (var path in keyFileCandidates ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    keys.ParseKeyText(File.ReadAllText(path), fillOnly: true);
                    keys.LoadSeedDbBeside(path);
                    logger?.Info($"[3DS] Merged fallback AES keys from \"{path}\".");
                    any = true;
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"[3DS] Failed to parse AES key file \"{path}\".");
                }
            }

            if (!any)
                throw new Exception(
                    "No 3DS AES keys could be found. Most Azahar builds bundle their keys, so simply point the " +
                    "mapping's emulator at your Azahar install. If you use a build compiled without the bundled " +
                    "keys, place an \"aes_keys.txt\" in Azahar's sysdata folder (or beside the source). " +
                    "Loose .3ds/.cci/.cxi/.3dsx files install without keys.");

            return keys;
        }

        private void ParseKeyText(string text, bool fillOnly = false)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0)
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                var name = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (value.Length != 32 || !TryHexToBytes(value, out var bytes))
                    continue;

                // Citra/Azahar's aes_keys.txt spells the key-scrambler constant "generator"; accept the longer
                // form too for hand-written files.
                if (name.Equals("generator", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("generatorConstant", StringComparison.OrdinalIgnoreCase))
                {
                    if (!fillOnly || _generatorConstant == null)
                        _generatorConstant = bytes;
                }
                else if (!fillOnly || !_parts.ContainsKey(name))
                {
                    _parts[name] = bytes;
                }
            }
        }

        private void LoadSeedDbBeside(string keyFilePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(keyFilePath);
                if (dir == null)
                    return;
                var seedDb = Path.Combine(dir, "seeddb.bin");
                if (File.Exists(seedDb))
                    ParseSeedDb(File.ReadAllBytes(seedDb));
            }
            catch { /* seeds are optional; only seed-crypto titles need them */ }
        }

        // seeddb.bin: 16-byte header (u32 count, 12 bytes padding) then `count` entries of 0x20 bytes:
        // u64 titleId (LE), 0x10 seed, 0x8 reserved.
        private void ParseSeedDb(byte[] data)
        {
            if (data.Length < 16)
                return;
            uint count = Le.U32(data, 0);
            for (uint i = 0; i < count; i++)
            {
                int o = 16 + (int)i * 0x20;
                if (o + 0x20 > data.Length)
                    break;
                ulong titleId = Le.U64(data, o);
                var seed = new byte[16];
                Buffer.BlockCopy(data, o + 8, seed, 0, 16);
                _seeds[titleId] = seed;
            }
        }

        private static string TryExtractBuiltinKeyblob(string emulatorBaseDir, ILogger logger)
        {
            IEnumerable<string> binaries;
            try
            {
                binaries = Directory.EnumerateFiles(emulatorBaseDir, "*.exe", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(emulatorBaseDir, "*.dll", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[3DS] Could not enumerate Azahar binaries under \"{emulatorBaseDir}\".");
                return null;
            }

            foreach (var bin in binaries)
            {
                try
                {
                    var bytes = File.ReadAllBytes(bin);
                    int at = IndexOf(bytes, KeyblobSignature);
                    if (at < 0)
                        continue;

                    var text = DecryptKeyblob(bytes, at);
                    if (text != null && text.StartsWith(KeyblobPlaintextPrefix, StringComparison.Ordinal))
                    {
                        logger?.Info($"[3DS] Found Azahar built-in keyblob in \"{Path.GetFileName(bin)}\".");
                        return text;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"[3DS] Failed while scanning \"{bin}\" for the keyblob.");
                }
            }
            return null;
        }

        // AES-128-CBC(key=0, IV=0, no padding) decrypt from the blob offset forward, returning the plaintext as
        // ASCII. The blob is a few KB; decrypt a generous window bounded by the file.
        private static string DecryptKeyblob(byte[] data, int offset)
        {
            int avail = data.Length - offset;
            int len = Math.Min(avail, 0x4000);
            len -= len % 16;
            if (len <= 0)
                return null;

            using (var aes = Aes.Create())
            {
                aes.Key = new byte[16];
                aes.IV = new byte[16];
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                {
                    var plain = new byte[len];
                    dec.TransformBlock(data, offset, len, plain, 0);
                    return Encoding.ASCII.GetString(plain);
                }
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return i;
            }
            return -1;
        }

        public static bool TryHexToBytes(string hex, out byte[] bytes)
        {
            bytes = null;
            if (hex.Length % 2 != 0)
                return false;
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                    return false;
            }
            bytes = result;
            return true;
        }

        // Test/diagnostic hook: build a key set directly from aes_keys.txt text.
        public static ThreeDsKeys FromKeyText(string text)
        {
            var keys = new ThreeDsKeys();
            keys.ParseKeyText(text);
            return keys;
        }
    }
}
