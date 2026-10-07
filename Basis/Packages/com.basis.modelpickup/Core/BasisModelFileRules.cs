using System;
using System.IO;
using System.Security;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Local file handling for dropped models: a read that refuses oversized files before allocating, and generated
    /// save names. Which paths are models at all is the validator's call
    /// (<see cref="BasisGlbValidator.HasSupportedModelExtension"/>, <see cref="BasisGlbValidator.TryDetectFormat"/>).
    /// Runs on worker threads; touches nothing but the file.
    /// </summary>
    public static class BasisModelFileRules
    {
        public const string SaveFilePrefix = "Model_";
        public const string SaveFileExtension = ".glb";

        /// <summary>Largest byte array the runtime will allocate (Array.MaxLength); any limit above it is capped here.</summary>
        public const long MaxArrayBytes = 0x7FFFFFC7;

        private const int ReadBufferBytes = 81920;
        private const string ChangedWhileReading = "The file changed while it was being read.";

        /// <summary>
        /// Reads a whole file of at most <paramref name="maxBytes"/>. The size is checked against the limit before any
        /// buffer exists, and a file that grows or shrinks during the read is refused rather than truncated.
        /// </summary>
        public static bool TryReadBounded(string path, long maxBytes, out byte[] data, out string error)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            {
                error = "The file path is not valid.";
                return false;
            }
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferBytes,
                    FileOptions.SequentialScan))
                {
                    return TryReadBounded(stream, stream.Length, maxBytes, out data, out error);
                }
            }
            catch (FileNotFoundException)
            {
                error = "The file no longer exists.";
            }
            catch (DirectoryNotFoundException)
            {
                error = "The file no longer exists.";
            }
            catch (UnauthorizedAccessException)
            {
                error = "The file could not be opened: access was denied.";
            }
            catch (IOException e)
            {
                error = "The file could not be read: " + e.Message;
            }
            catch (ArgumentException)
            {
                error = "The file path is not valid.";
            }
            catch (NotSupportedException)
            {
                error = "The file path is not valid.";
            }
            catch (SecurityException)
            {
                error = "The file could not be opened: access was denied.";
            }
            data = null;
            return false;
        }

        /// <summary>
        /// Reads exactly <paramref name="declaredLength"/> bytes from <paramref name="stream"/>, then requires the end of
        /// the stream. The length is checked against <paramref name="maxBytes"/> before allocating. Never throws for I/O.
        /// </summary>
        public static bool TryReadBounded(Stream stream, long declaredLength, long maxBytes, out byte[] data, out string error)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            data = null;
            if (declaredLength < 0)
            {
                error = "The file size could not be determined.";
                return false;
            }
            if (declaredLength == 0)
            {
                error = "The file is empty.";
                return false;
            }
            long limit = Math.Min(Math.Max(0L, maxBytes), MaxArrayBytes);
            if (declaredLength > limit)
            {
                error = BasisGlbErrors.Bytes("File", declaredLength, limit);
                return false;
            }

            byte[] buffer = new byte[declaredLength];
            try
            {
                int filled = 0;
                while (filled < buffer.Length)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0)
                    {
                        error = ChangedWhileReading;
                        return false;
                    }
                    filled += read;
                }
                if (stream.ReadByte() >= 0)
                {
                    error = ChangedWhileReading;
                    return false;
                }
            }
            catch (IOException e)
            {
                error = "The file could not be read: " + e.Message;
                return false;
            }
            catch (ObjectDisposedException)
            {
                error = "The file could not be read.";
                return false;
            }
            catch (NotSupportedException)
            {
                error = "The file could not be read.";
                return false;
            }
            data = buffer;
            error = null;
            return true;
        }

        /// <summary>A fresh name for a saved model. Nothing from the source file or the sender reaches the file system.</summary>
        public static string GenerateSaveFileName(Guid fileId)
        {
            return SaveFilePrefix + fileId.ToString("N") + SaveFileExtension;
        }

        /// <summary>"12.31 MiB (12,912,345 bytes)", or "512 bytes" below a mebibyte; invariant culture, as the validator's messages.</summary>
        public static string DescribeBytes(long bytes)
        {
            return BasisGlbErrors.FormatBytes(bytes);
        }
    }
}
