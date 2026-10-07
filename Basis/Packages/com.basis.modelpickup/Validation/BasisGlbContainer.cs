using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Basis.ModelPickup.Validation
{
    public struct BasisGlbChunks
    {
        public int JsonStart, JsonLength;
        public bool HasBin;
        public int BinStart, BinLength;
    }

    public enum BasisDataUriMedia : byte
    {
        None = 0,
        OctetStream = 1,
        GltfBuffer = 2,
        Png = 3,
        Jpeg = 4,
    }

    /// <summary>
    /// GLB header and chunk walk, plus the sender's data: URI grammar and strict base64. All size arithmetic is in
    /// long. The exact-fill rule (header length == data length, chunks fill it exactly) rejects trailing bytes,
    /// duplicate chunks, unknown chunks and a BIN before the JSON in one place; glTFast guards those only with
    /// Asserts that release players strip.
    /// </summary>
    public static class BasisGlbContainer
    {
        public const uint Magic = 0x46546C67;      // "glTF"
        public const uint JsonChunkType = 0x4E4F534A; // "JSON"
        public const uint BinChunkType = 0x004E4942;  // "BIN\0"
        public const int HeaderBytes = 12;
        public const int ChunkHeaderBytes = 8;
        public const int MaxMediaTypeBytes = 64;

        public static bool HasGlbMagic(ReadOnlySpan<byte> head)
        {
            return head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;
        }

        /// <summary>First non-whitespace byte, after at most one UTF-8 BOM, is '{'.</summary>
        public static bool LooksLikeGltfJson(ReadOnlySpan<byte> head)
        {
            int i = HasUtf8Bom(head) ? 3 : 0;
            while (i < head.Length)
            {
                byte c = head[i];
                if (c == (byte)' ' || c == (byte)'\n' || c == (byte)'\r' || c == (byte)'\t')
                {
                    i++;
                    continue;
                }
                return c == (byte)'{';
            }
            return false;
        }

        public static bool HasUtf8Bom(ReadOnlySpan<byte> data)
        {
            return data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        }

        public static bool TryParse(byte[] data, int maxJsonBytes, out BasisGlbChunks chunks, out bool overLimit, out string error)
        {
            chunks = default;
            overLimit = false;
            error = null;
            long length = data.Length;
            if (length < 20)
            {
                error = "GLB is " + BasisGlbErrors.N(length) + " bytes; a GLB needs at least 20.";
                return false;
            }
            var span = new ReadOnlySpan<byte>(data);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
            {
                error = "Not a GLB (bad magic).";
                return false;
            }
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
            if (version != 2)
            {
                error = "GLB version " + version.ToString(CultureInfo.InvariantCulture) + " is not supported; only version 2.";
                return false;
            }
            long declared = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
            if (declared != length)
            {
                error = "GLB header declares " + BasisGlbErrors.N(declared) + " bytes but the data is " + BasisGlbErrors.N(length) + " bytes.";
                return false;
            }
            if (length % 4 != 0)
            {
                error = "GLB length " + BasisGlbErrors.N(length) + " is not a multiple of 4.";
                return false;
            }

            long jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
            uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
            if (jsonType != JsonChunkType)
            {
                error = "GLB chunk 0 has type 0x" + jsonType.ToString("X8", CultureInfo.InvariantCulture) + "; the first chunk must be JSON.";
                return false;
            }
            if (jsonLength <= 0 || jsonLength % 4 != 0)
            {
                error = "GLB JSON chunk length " + BasisGlbErrors.N(jsonLength) + " is not a positive multiple of 4.";
                return false;
            }
            if (20 + jsonLength > length)
            {
                error = "GLB JSON chunk runs past the end of the data.";
                return false;
            }
            // Checked before any UTF-8 or JSON work, so an oversized chunk costs nothing.
            if (jsonLength > maxJsonBytes)
            {
                overLimit = true;
                error = BasisGlbErrors.Bytes("GLB JSON chunk", jsonLength, maxJsonBytes);
                return false;
            }
            chunks.JsonStart = 20;
            chunks.JsonLength = (int)jsonLength;
            if (HasUtf8Bom(span.Slice(20, (int)jsonLength)))
            {
                error = "GLB JSON chunk starts with a byte-order mark.";
                return false;
            }

            long rest = length - (20 + jsonLength);
            if (rest == 0) return true;
            if (rest < ChunkHeaderBytes)
            {
                error = "GLB has data after its last chunk.";
                return false;
            }
            int binHeader = (int)(20 + jsonLength);
            long binLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(binHeader));
            uint binType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(binHeader + 4));
            if (binType != BinChunkType)
            {
                error = "GLB chunk 1 has type 0x" + binType.ToString("X8", CultureInfo.InvariantCulture) + "; only JSON then BIN is allowed.";
                return false;
            }
            if (binLength <= 0 || binLength % 4 != 0)
            {
                error = "GLB BIN chunk length " + BasisGlbErrors.N(binLength) + " is not a positive multiple of 4.";
                return false;
            }
            long end = 28 + jsonLength + binLength;
            if (end > length)
            {
                error = "GLB BIN chunk runs past the end of the data.";
                return false;
            }
            if (end != length)
            {
                error = "GLB has data after its last chunk.";
                return false;
            }
            chunks.HasBin = true;
            chunks.BinStart = binHeader + ChunkHeaderBytes;
            chunks.BinLength = (int)binLength;
            return true;
        }

        /// <summary>True when the bytes start with "data:" (ASCII, case-insensitive).</summary>
        public static bool IsDataUri(byte[] uri, int start, int length)
        {
            return length >= 5 && StartsWithIgnoreCase(uri, start, length, "data:");
        }

        /// <summary>
        /// Parses "data:" mediatype ";base64," payload. No other parameters, media type at most 64 bytes, one of the
        /// allowed types for the role. Unsupported (not malformed) for anything outside that grammar.
        /// </summary>
        public static bool TryParseDataUri(byte[] uri, int start, int length, bool forImage,
            out BasisDataUriMedia media, out int payloadStart, out int payloadLength, out string error)
        {
            media = BasisDataUriMedia.None;
            payloadStart = 0;
            payloadLength = 0;
            error = null;
            int headerStart = start + 5;
            int end = start + length;
            int comma = -1;
            int scanEnd = Math.Min(end, headerStart + MaxMediaTypeBytes + 8 + 1);
            for (int i = headerStart; i < scanEnd; i++)
            {
                if (uri[i] == (byte)',')
                {
                    comma = i;
                    break;
                }
            }
            if (comma < 0)
            {
                error = "uses a data: URI whose header is missing or longer than 64 bytes.";
                return false;
            }
            const string base64Marker = ";base64";
            int headerLength = comma - headerStart;
            if (headerLength < base64Marker.Length
                || !StartsWithIgnoreCase(uri, comma - base64Marker.Length, base64Marker.Length, base64Marker))
            {
                error = "uses a data: URI without base64 encoding, which is not supported.";
                return false;
            }
            int typeLength = headerLength - base64Marker.Length;
            for (int i = headerStart; i < headerStart + typeLength; i++)
            {
                if (uri[i] == (byte)';')
                {
                    error = "uses a data: URI with parameters other than base64, which is not supported.";
                    return false;
                }
            }
            if (forImage)
            {
                if (EqualsIgnoreCase(uri, headerStart, typeLength, "image/png")) media = BasisDataUriMedia.Png;
                else if (EqualsIgnoreCase(uri, headerStart, typeLength, "image/jpeg")) media = BasisDataUriMedia.Jpeg;
            }
            else
            {
                if (EqualsIgnoreCase(uri, headerStart, typeLength, "application/octet-stream")) media = BasisDataUriMedia.OctetStream;
                else if (EqualsIgnoreCase(uri, headerStart, typeLength, "application/gltf-buffer")) media = BasisDataUriMedia.GltfBuffer;
            }
            if (media == BasisDataUriMedia.None)
            {
                error = forImage
                    ? "uses a data: URI whose media type is not image/png or image/jpeg."
                    : "uses a data: URI whose media type is not application/octet-stream or application/gltf-buffer.";
                return false;
            }
            payloadStart = comma + 1;
            payloadLength = end - payloadStart;
            return true;
        }

        /// <summary>
        /// Computes the decoded size from the payload length and trailing '=' alone, so the caller can check its byte
        /// budget before allocating. '=' is allowed only as one or two final characters with length % 4 == 0;
        /// unpadded payloads may end with 2 or 3 extra characters, never 1.
        /// </summary>
        public static bool TryGetBase64DecodedLength(byte[] payload, int start, int length, out int dataChars, out long decodedLength)
        {
            dataChars = 0;
            decodedLength = 0;
            int pad = 0;
            if (length > 0 && payload[start + length - 1] == (byte)'=') pad++;
            if (length > 1 && payload[start + length - 2] == (byte)'=') pad++;
            if (pad > 0 && length % 4 != 0) return false;
            int chars = length - pad;
            int remainder = chars % 4;
            if (remainder == 1) return false;
            if (pad > 0 && remainder == 0) return false;
            if (pad == 1 && remainder != 3) return false;
            if (pad == 2 && remainder != 2) return false;
            dataChars = chars;
            decodedLength = (long)(chars / 4) * 3 + (remainder == 2 ? 1 : remainder == 3 ? 2 : 0);
            return true;
        }

        /// <summary>Decodes <paramref name="dataChars"/> characters of the strict alphabet (no whitespace, no URL-safe variant).</summary>
        public static bool TryDecodeBase64(byte[] payload, int start, int dataChars, byte[] destination, int destinationOffset)
        {
            int o = destinationOffset;
            int i = start;
            int end = start + dataChars;
            while (end - i >= 4)
            {
                int a = Sextet(payload[i]), b = Sextet(payload[i + 1]), c = Sextet(payload[i + 2]), d = Sextet(payload[i + 3]);
                if ((a | b | c | d) < 0) return false;
                int v = (a << 18) | (b << 12) | (c << 6) | d;
                destination[o++] = (byte)(v >> 16);
                destination[o++] = (byte)(v >> 8);
                destination[o++] = (byte)v;
                i += 4;
            }
            int left = end - i;
            if (left == 2)
            {
                int a = Sextet(payload[i]), b = Sextet(payload[i + 1]);
                if ((a | b) < 0) return false;
                destination[o] = (byte)(((a << 18) | (b << 12)) >> 16);
            }
            else if (left == 3)
            {
                int a = Sextet(payload[i]), b = Sextet(payload[i + 1]), c = Sextet(payload[i + 2]);
                if ((a | b | c) < 0) return false;
                int v = (a << 18) | (b << 12) | (c << 6);
                destination[o++] = (byte)(v >> 16);
                destination[o] = (byte)(v >> 8);
            }
            return true;
        }

        private static int Sextet(byte c)
        {
            if (c >= (byte)'A' && c <= (byte)'Z') return c - (byte)'A';
            if (c >= (byte)'a' && c <= (byte)'z') return c - (byte)'a' + 26;
            if (c >= (byte)'0' && c <= (byte)'9') return c - (byte)'0' + 52;
            if (c == (byte)'+') return 62;
            if (c == (byte)'/') return 63;
            return -1;
        }

        private static bool StartsWithIgnoreCase(byte[] data, int start, int length, string ascii)
        {
            if (length < ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
            {
                if (ToLowerAscii(data[start + i]) != ascii[i]) return false;
            }
            return true;
        }

        private static bool EqualsIgnoreCase(byte[] data, int start, int length, string ascii)
        {
            return length == ascii.Length && StartsWithIgnoreCase(data, start, length, ascii);
        }

        private static byte ToLowerAscii(byte c)
        {
            return c >= (byte)'A' && c <= (byte)'Z' ? (byte)(c + 32) : c;
        }
    }
}
