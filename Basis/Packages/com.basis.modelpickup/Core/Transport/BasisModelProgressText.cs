using System;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Text for the transfer progress labels. Kept apart from the gizmo instance so the wording and the
    /// rebuild key can be tested without the engine. Every piece is formatted into a stack buffer, so a label
    /// costs one string when its text changes and nothing when it does not.
    /// </summary>
    public static class BasisModelProgressText
    {
        /// <summary>
        /// Room for "tx 100%  " and any finite rate; a value that still does not fit (nothing a transfer reports)
        /// falls back to the allocating formatter rather than being cut short.
        /// </summary>
        private const int MaxChars = 96;

        private const uint FnvOffset = 2166136261u;
        private const uint FnvPrime = 16777619u;

        /// <summary>
        /// Changes when, and only when, the label would read differently: a hash of the very text
        /// <see cref="BuildText"/> would produce, formatted without allocating. A rate wobbling inside its last
        /// shown digit no longer rebuilds an identical label, and one crossing it always does.
        /// </summary>
        public static int TextKey(int percent, float bytesPerSecond, bool outbound)
        {
            Span<char> buffer = stackalloc char[MaxChars];
            if (!TryWriteText(buffer, percent, bytesPerSecond, outbound, out int written))
                return Hash(BuildText(percent, bytesPerSecond, outbound).AsSpan());
            return Hash(buffer.Slice(0, written));
        }

        /// <summary>"42%  128.0 KB/s", or "tx 42%  ..." for an outbound transfer.</summary>
        public static string BuildText(int percent, float bytesPerSecond, bool outbound)
        {
            Span<char> buffer = stackalloc char[MaxChars];
            if (TryWriteText(buffer, percent, bytesPerSecond, outbound, out int written))
                return new string(buffer.Slice(0, written));
            return (outbound ? "tx " : string.Empty) + percent.ToString() + "%  " + FormatRate(bytesPerSecond);
        }

        /// <summary>Formats with the current culture, as the image pickup's label does.</summary>
        public static string FormatRate(float bytesPerSecond)
        {
            Span<char> buffer = stackalloc char[MaxChars];
            if (TryWriteRate(buffer, bytesPerSecond, out int written))
                return new string(buffer.Slice(0, written));
            ToUnit(bytesPerSecond, out float value, out string suffix, out bool whole);
            return (whole ? BasisModelMath.RoundToInt(value).ToString() : value.ToString("0.0")) + suffix;
        }

        /// <summary>The label into <paramref name="destination"/>; false, with nothing promised, when it does not fit.</summary>
        public static bool TryWriteText(Span<char> destination, int percent, float bytesPerSecond, bool outbound, out int written)
        {
            written = 0;
            if (outbound && !TryAppend(destination, ref written, "tx "))
                return false;
            if (!percent.TryFormat(destination.Slice(written), out int percentChars))
                return false;
            written += percentChars;
            if (!TryAppend(destination, ref written, "%  "))
                return false;
            if (!TryWriteRate(destination.Slice(written), bytesPerSecond, out int rateChars))
                return false;
            written += rateChars;
            return true;
        }

        /// <summary>
        /// "512 B/s", "1.5 KB/s", "3.0 MB/s": exactly what <c>ToString("0.0")</c> (current culture) or a rounded
        /// integer plus the unit would give, written without allocating.
        /// </summary>
        public static bool TryWriteRate(Span<char> destination, float bytesPerSecond, out int written)
        {
            written = 0;
            ToUnit(bytesPerSecond, out float value, out string suffix, out bool whole);
            bool formatted = whole
                ? BasisModelMath.RoundToInt(value).TryFormat(destination, out written)
                : value.TryFormat(destination, out written, "0.0");
            if (!formatted)
                return false;
            return TryAppend(destination, ref written, suffix);
        }

        private static void ToUnit(float bytesPerSecond, out float value, out string suffix, out bool whole)
        {
            if (bytesPerSecond < 0f)
                bytesPerSecond = 0f;
            if (bytesPerSecond >= 1024f * 1024f)
            {
                value = bytesPerSecond / (1024f * 1024f);
                suffix = " MB/s";
                whole = false;
                return;
            }
            if (bytesPerSecond >= 1024f)
            {
                value = bytesPerSecond / 1024f;
                suffix = " KB/s";
                whole = false;
                return;
            }
            value = bytesPerSecond;
            suffix = " B/s";
            whole = true;
        }

        private static bool TryAppend(Span<char> destination, ref int written, string text)
        {
            if (written + text.Length > destination.Length)
                return false;
            text.AsSpan().CopyTo(destination.Slice(written));
            written += text.Length;
            return true;
        }

        /// <summary>FNV-1a over the characters.</summary>
        private static int Hash(ReadOnlySpan<char> text)
        {
            uint hash = FnvOffset;
            int length = text.Length;
            for (int i = 0; i < length; i++)
            {
                hash ^= text[i];
                hash *= FnvPrime;
            }
            return (int)hash;
        }
    }
}
