using System;
using System.Collections.Generic;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// The validator's own PNG handling: signature, IHDR, a strict chunk walk with CRC checks, and stripping of every
    /// ancillary chunk except tRNS. This bounds <c>Texture2D.LoadImage</c> (glTFast puts no dimension cap on it) and drops
    /// smuggled payloads (tEXt, iCCP, APNG frames…). There is no JPEG reader here: only the sender's sanitiser decodes JPEG,
    /// after <see cref="BasisModelImageHeader"/> checks its header, and receivers accept PNG only.
    /// </summary>
    public static class BasisGlbPng
    {
        public const int SignatureLength = 8;
        public const int MaxChunks = 8192;

        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly uint[] CrcTable = BuildCrcTable();

        private const uint TypeIhdr = 0x49484452;
        private const uint TypePlte = 0x504C5445;
        private const uint TypeIdat = 0x49444154;
        private const uint TypeIend = 0x49454E44;
        private const uint TypeTrns = 0x74524E53;

        public static bool HasSignature(ReadOnlySpan<byte> data)
        {
            if (data.Length < SignatureLength) return false;
            for (int i = 0; i < SignatureLength; i++)
            {
                if (data[i] != Signature[i]) return false;
            }
            return true;
        }

        public static bool HasJpegSignature(ReadOnlySpan<byte> data)
        {
            return data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        }

        public static bool HasGifSignature(ReadOnlySpan<byte> data)
        {
            return data.Length >= 4 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8';
        }

        public static bool HasWebpSignature(ReadOnlySpan<byte> data)
        {
            return data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';
        }

        /// <summary>IHDR width and height (big-endian at 16 and 20), for the sender's pre-sanitise source check.</summary>
        public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;
            if (data.Length < 24)
            {
                error = "PNG header is too short.";
                return false;
            }
            if (!HasSignature(data))
            {
                error = "Not a PNG (bad signature).";
                return false;
            }
            if (ReadUInt32BigEndian(data, 12) != TypeIhdr)
            {
                error = "PNG IHDR is not the first chunk.";
                return false;
            }
            uint w = ReadUInt32BigEndian(data, 16);
            uint h = ReadUInt32BigEndian(data, 20);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
            {
                error = "PNG has invalid dimensions.";
                return false;
            }
            width = (int)w;
            height = (int)h;
            return true;
        }

        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            return ~UpdateCrc(0xFFFFFFFFu, data);
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        /// <summary>
        /// Validates a final image (sanitiser output on the sender, wire PNG on the receiver) and returns the signature
        /// plus IHDR, PLTE?, tRNS?, IDAT…, IEND verbatim. Idempotent. <paramref name="overLimit"/> tells a cap from a
        /// structural fault. Error text never contains chunk bytes.
        /// </summary>
        public static bool TryCanonicalize(ReadOnlySpan<byte> png, in BasisModelLimits limits, out byte[] canonical,
            out int width, out int height, out bool overLimit, out string error)
        {
            canonical = null;
            width = 0;
            height = 0;
            overLimit = false;
            error = null;
            if (!HasSignature(png))
            {
                error = "Not a PNG (bad signature).";
                return false;
            }
            var kept = new List<int>(16); // pairs: chunk start, chunk total length
            long keptBytes = SignatureLength;
            int pos = SignatureLength;
            int chunks = 0;
            bool sawIhdr = false, sawPlte = false, sawTrns = false, sawIdat = false, idatEnded = false, sawIend = false;
            int colorType = 0;
            int paletteEntries = 0;
            while (pos < png.Length)
            {
                if (++chunks > MaxChunks)
                {
                    error = "PNG has more than " + BasisGlbErrors.N(MaxChunks) + " chunks.";
                    return false;
                }
                if (png.Length - pos < 12)
                {
                    error = "PNG chunk is truncated.";
                    return false;
                }
                uint length = ReadUInt32BigEndian(png, pos);
                if (length > (uint)(png.Length - pos - 12))
                {
                    error = "PNG chunk runs past the end of the data.";
                    return false;
                }
                uint type = ReadUInt32BigEndian(png, pos + 4);
                for (int i = 4; i < 8; i++)
                {
                    byte c = png[pos + i];
                    if (!((c >= (byte)'A' && c <= (byte)'Z') || (c >= (byte)'a' && c <= (byte)'z')))
                    {
                        error = "PNG chunk type is not made of ASCII letters.";
                        return false;
                    }
                }
                int total = (int)length + 12;
                bool critical = (png[pos + 4] & 0x20) == 0;
                if (!sawIhdr && type != TypeIhdr)
                {
                    error = "PNG IHDR is not the first chunk.";
                    return false;
                }
                bool keep = true;
                if (type != TypeIdat && sawIdat) idatEnded = true;
                switch (type)
                {
                    case TypeIhdr:
                        if (sawIhdr)
                        {
                            error = "PNG has more than one IHDR.";
                            return false;
                        }
                        if (length != 13)
                        {
                            error = "PNG IHDR has length " + BasisGlbErrors.N(length) + "; it must be 13.";
                            return false;
                        }
                        if (!CheckCrc(png, pos, (int)length, out error)) return false;
                        if (!TryCheckHeader(png.Slice(pos + 8, 13), limits, out width, out height, out colorType, out overLimit, out error))
                        {
                            return false;
                        }
                        sawIhdr = true;
                        break;
                    case TypePlte:
                        if (sawPlte || sawIdat || sawTrns)
                        {
                            error = "PNG PLTE is repeated or out of order.";
                            return false;
                        }
                        if (colorType == 0 || colorType == 4)
                        {
                            error = "PNG has a PLTE chunk in a greyscale image.";
                            return false;
                        }
                        if (length % 3 != 0 || length < 3 || length > 768)
                        {
                            error = "PNG PLTE length " + BasisGlbErrors.N(length) + " is invalid.";
                            return false;
                        }
                        if (!CheckCrc(png, pos, (int)length, out error)) return false;
                        paletteEntries = (int)length / 3;
                        sawPlte = true;
                        break;
                    case TypeTrns:
                        if (sawTrns || sawIdat || (colorType == 3 && !sawPlte))
                        {
                            error = "PNG tRNS is repeated or out of order.";
                            return false;
                        }
                        if (colorType == 4 || colorType == 6)
                        {
                            error = "PNG has a tRNS chunk in an image with an alpha channel.";
                            return false;
                        }
                        if ((colorType == 0 && length != 2) || (colorType == 2 && length != 6)
                            || (colorType == 3 && (length < 1 || length > paletteEntries)))
                        {
                            error = "PNG tRNS length " + BasisGlbErrors.N(length) + " is invalid.";
                            return false;
                        }
                        if (!CheckCrc(png, pos, (int)length, out error)) return false;
                        sawTrns = true;
                        break;
                    case TypeIdat:
                        if (idatEnded)
                        {
                            error = "PNG IDAT chunks are not consecutive.";
                            return false;
                        }
                        if (!CheckCrc(png, pos, (int)length, out error)) return false;
                        sawIdat = true;
                        break;
                    case TypeIend:
                        if (length != 0)
                        {
                            error = "PNG IEND is not empty.";
                            return false;
                        }
                        if (!CheckCrc(png, pos, 0, out error)) return false;
                        if (pos + total != png.Length)
                        {
                            error = "PNG has data after IEND.";
                            return false;
                        }
                        sawIend = true;
                        break;
                    default:
                        if (critical)
                        {
                            error = "PNG has an unknown critical chunk.";
                            return false;
                        }
                        keep = false; // ancillary: dropped without reading it
                        break;
                }
                if (keep)
                {
                    kept.Add(pos);
                    kept.Add(total);
                    keptBytes += total;
                }
                pos += total;
                if (sawIend) break;
            }
            if (!sawIend)
            {
                error = "PNG has no IEND.";
                return false;
            }
            if (!sawIdat)
            {
                error = "PNG has no IDAT.";
                return false;
            }
            if (colorType == 3 && !sawPlte)
            {
                error = "PNG palette image has no PLTE.";
                return false;
            }
            if (keptBytes > limits.MaxImageBytes)
            {
                overLimit = true;
                error = BasisGlbErrors.Bytes("PNG", keptBytes, limits.MaxImageBytes);
                return false;
            }
            var output = new byte[keptBytes];
            png.Slice(0, SignatureLength).CopyTo(output);
            int o = SignatureLength;
            for (int i = 0; i < kept.Count; i += 2)
            {
                png.Slice(kept[i], kept[i + 1]).CopyTo(new Span<byte>(output, o, kept[i + 1]));
                o += kept[i + 1];
            }
            canonical = output;
            return true;
        }

        private static bool TryCheckHeader(ReadOnlySpan<byte> ihdr, in BasisModelLimits limits, out int width, out int height,
            out int colorType, out bool overLimit, out string error)
        {
            overLimit = false;
            error = null;
            uint w = ReadUInt32BigEndian(ihdr, 0);
            uint h = ReadUInt32BigEndian(ihdr, 4);
            int bitDepth = ihdr[8];
            colorType = ihdr[9];
            width = 0;
            height = 0;
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
            {
                error = "PNG has invalid dimensions.";
                return false;
            }
            width = (int)w;
            height = (int)h;
            if (width > limits.MaxTextureDimension || height > limits.MaxTextureDimension
                || (long)width * height > limits.MaxTexturePixelsPerImage)
            {
                overLimit = true;
                error = BasisGlbErrors.Dimensions("Image", width, height, limits.MaxTextureDimension, limits.MaxTexturePixelsPerImage);
                return false;
            }
            if (bitDepth == 16)
            {
                // Unity loads 16-bit PNGs as RGBA64, which would break the memory estimate.
                error = "PNG is 16-bit; only 8-bit images are supported.";
                return false;
            }
            bool valid;
            switch (colorType)
            {
                case 0:
                case 3:
                    valid = bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8;
                    break;
                case 2:
                case 4:
                case 6:
                    valid = bitDepth == 8;
                    break;
                default:
                    valid = false;
                    break;
            }
            if (!valid)
            {
                error = "PNG has an invalid bit depth and colour type combination.";
                return false;
            }
            if (ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1)
            {
                error = "PNG uses an unknown compression, filter or interlace method.";
                return false;
            }
            return true;
        }

        private static bool CheckCrc(ReadOnlySpan<byte> png, int chunkStart, int dataLength, out string error)
        {
            uint computed = ~UpdateCrc(0xFFFFFFFFu, png.Slice(chunkStart + 4, 4 + dataLength));
            uint stored = ReadUInt32BigEndian(png, chunkStart + 8 + dataLength);
            if (computed != stored)
            {
                error = "PNG chunk CRC does not match.";
                return false;
            }
            error = null;
            return true;
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
