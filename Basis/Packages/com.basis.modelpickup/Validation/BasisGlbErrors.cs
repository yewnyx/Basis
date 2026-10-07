using System;
using System.Globalization;
using System.Text;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Invariant-culture message helpers. Messages carry indices and numbers only; the one exception is
    /// sender-mode descriptive text (an extension name or a URI), which goes through <see cref="ForDisplay"/>.
    /// Receiver-mode messages never contain bytes from the input.
    /// </summary>
    public static class BasisGlbErrors
    {
        private const double MiB = 1024d * 1024d;

        public static string N(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        public static string Index(string collection, int index)
        {
            return collection + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        public static string Index(string collection, int index, string member)
        {
            return collection + "[" + index.ToString(CultureInfo.InvariantCulture) + "]." + member;
        }

        public static string Primitive(int mesh, int primitive)
        {
            return "meshes[" + mesh.ToString(CultureInfo.InvariantCulture) + "].primitives["
                + primitive.ToString(CultureInfo.InvariantCulture) + "]";
        }

        /// <summary>"Vertex count is 612,000. The maximum is 500,000. It exceeds the limit by 112,000."</summary>
        public static string Limit(string label, long actual, long maximum)
        {
            return label + " is " + N(actual) + ". The maximum is " + N(maximum)
                + ". It exceeds the limit by " + N(actual - maximum) + ".";
        }

        /// <summary>Same shape as the image pickup's byte-limit message.</summary>
        public static string Bytes(string label, long actual, long maximum)
        {
            return label + " is " + FormatBytes(actual) + ". The maximum is " + FormatBytes(maximum)
                + ". It exceeds the limit by " + FormatBytes(actual - maximum) + ".";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= MiB)
            {
                return (bytes / MiB).ToString("0.##", CultureInfo.InvariantCulture) + " MiB (" + N(bytes) + " bytes)";
            }
            return N(bytes) + " bytes";
        }

        /// <summary>Same shape as the image pickup's dimension-limit message.</summary>
        public static string Dimensions(string label, int width, int height, int maximumDimension, long maximumPixels)
        {
            long pixels = (long)width * height;
            var exceeded = new StringBuilder();
            if (width > maximumDimension)
            {
                exceeded.Append("width exceeds the limit by ").Append(N(width - maximumDimension)).Append("px");
            }
            if (height > maximumDimension)
            {
                if (exceeded.Length > 0) exceeded.Append("; ");
                exceeded.Append("height exceeds the limit by ").Append(N(height - maximumDimension)).Append("px");
            }
            if (pixels > maximumPixels)
            {
                if (exceeded.Length > 0) exceeded.Append("; ");
                exceeded.Append("pixel count exceeds the limit by ").Append(N(pixels - maximumPixels));
            }
            return label + " is " + N(width) + "×" + N(height) + " (" + N(pixels) + " pixels). The maximum is "
                + N(maximumDimension) + "×" + N(maximumDimension) + " and " + N(maximumPixels)
                + " total pixels. Exceeded: " + exceeded + ".";
        }

        /// <summary>"accessors[7] reads past the end of bufferViews[2]: …"</summary>
        public static string At(string path, string message)
        {
            return path + " " + message;
        }

        public static string Meters(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture) + " m";
        }

        /// <summary>
        /// Sender-mode only: strips control and format characters (bidi overrides, zero-width) and caps the
        /// length so a file name or extension name cannot spoof the local user's dialog. The UI escapes it too.
        /// </summary>
        public static string ForDisplay(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new StringBuilder(Math.Min(value.Length, maxChars + 1));
            for (int i = 0; i < value.Length && builder.Length < maxChars; i++)
            {
                char c = value[i];
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format
                    || category == UnicodeCategory.Surrogate || category == UnicodeCategory.OtherNotAssigned)
                {
                    continue;
                }
                builder.Append(c);
            }
            if (builder.Length == maxChars && value.Length > maxChars)
            {
                builder.Append('…');
            }
            return builder.ToString();
        }
    }
}
