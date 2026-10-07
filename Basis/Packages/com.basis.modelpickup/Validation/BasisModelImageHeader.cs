using System;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// The sender's checks on one embedded source image before anything decodes it: the signature says PNG or
    /// JPEG (a GIF, WebP or anything else is refused, whatever its glTF mime type claims), the bytes and the
    /// header dimensions are within the source caps, and the size it will be re-encoded at. Engine-free, so
    /// the dotnet harness tests it; <c>BasisModelImageSanitizer</c> does the decode and re-encode.
    /// </summary>
    public static class BasisModelImageHeader
    {
        public const string PngOrJpegOnly = "Only PNG or JPEG images can be embedded";

        /// <summary>
        /// Checks <paramref name="data"/> against <paramref name="limits"/>' source caps (bytes, side and pixel
        /// count) from its signature and header alone. False with a reason for anything else.
        /// </summary>
        public static bool TryCheckSource(ReadOnlySpan<byte> data, in BasisModelLimits limits,
            out BasisModelImageFormat format, out int width, out int height, out string error)
        {
            format = default;
            width = 0;
            height = 0;
            if (data.Length == 0)
            {
                error = "The image is empty.";
                return false;
            }
            if (data.Length > limits.MaxSourceImageBytes)
            {
                error = BasisGlbErrors.Bytes("Source image", data.Length, limits.MaxSourceImageBytes);
                return false;
            }

            bool read;
            if (BasisGlbPng.HasSignature(data))
            {
                format = BasisModelImageFormat.Png;
                read = BasisGlbPng.TryReadDimensions(data, out width, out height, out error);
            }
            else if (BasisGlbPng.HasJpegSignature(data))
            {
                format = BasisModelImageFormat.Jpeg;
                read = TryReadJpegDimensions(data, out width, out height, out error);
            }
            else
            {
                error = PngOrJpegOnly;
                return false;
            }
            if (!read)
                return false;

            if (width > limits.MaxSourceTextureDimension || height > limits.MaxSourceTextureDimension
                || (long)width * height > limits.MaxSourceTexturePixels)
            {
                error = BasisGlbErrors.Dimensions("Source image", width, height, limits.MaxSourceTextureDimension,
                    limits.MaxSourceTexturePixels);
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// The size an image is re-encoded at: unchanged when both sides fit <paramref name="maxDimension"/>,
        /// otherwise scaled down uniformly until the longer side does, each side at least one pixel.
        /// </summary>
        public static void FitWithin(int width, int height, int maxDimension, out int targetWidth, out int targetHeight)
        {
            if (width <= maxDimension && height <= maxDimension)
            {
                targetWidth = width;
                targetHeight = height;
                return;
            }
            double scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
            targetWidth = Math.Max(1, Math.Min(maxDimension, (int)Math.Round(width * scale)));
            targetHeight = Math.Max(1, Math.Min(maxDimension, (int)Math.Round(height * scale)));
        }

        /// <summary>
        /// Width and height from the first start-of-frame segment, walking the segments before it. Fails on a
        /// truncated or malformed segment, and when scan data starts before any frame header.
        /// </summary>
        public static bool TryReadJpegDimensions(ReadOnlySpan<byte> data, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            {
                error = "Not a JPEG (bad signature).";
                return false;
            }

            int length = data.Length;
            int offset = 2;
            while (offset < length)
            {
                while (offset < length && data[offset] != 0xFF)
                    offset++;
                while (offset < length && data[offset] == 0xFF)
                    offset++;
                if (offset >= length)
                    break;

                byte marker = data[offset++];
                if (marker == 0x00 || marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    continue;
                if (marker == 0xD9)
                    break;
                if (marker == 0xDA)
                {
                    error = "JPEG scan data starts before its frame header.";
                    return false;
                }

                if (offset + 2 > length)
                {
                    error = "JPEG segment is truncated.";
                    return false;
                }
                int segmentLength = (data[offset] << 8) | data[offset + 1];
                if (segmentLength < 2 || segmentLength > length - offset)
                {
                    error = "JPEG segment length is invalid.";
                    return false;
                }

                if (IsStartOfFrame(marker))
                {
                    if (segmentLength < 7)
                    {
                        error = "JPEG frame header is truncated.";
                        return false;
                    }
                    height = (data[offset + 3] << 8) | data[offset + 4];
                    width = (data[offset + 5] << 8) | data[offset + 6];
                    if (width <= 0 || height <= 0)
                    {
                        error = "JPEG has invalid dimensions.";
                        return false;
                    }
                    error = null;
                    return true;
                }
                offset += segmentLength;
            }

            error = "JPEG frame header not found.";
            return false;
        }

        // SOF0..SOF15, less DHT (C4), JPG (C8) and DAC (CC), which share the range.
        private static bool IsStartOfFrame(byte marker)
        {
            return marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
        }
    }
}
