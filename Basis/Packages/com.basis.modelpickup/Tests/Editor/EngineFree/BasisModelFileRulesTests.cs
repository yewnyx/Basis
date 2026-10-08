using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelFileRulesTests
    {
        private const long MiB = 1024L * 1024L;

        private string _directory;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "BasisModelFileRulesTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
                // Best effort; the OS temp cleaner gets the rest.
            }
        }

        private string WriteFile(string name, int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = (byte)(i * 31 + 7);
            string path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Test]
        public void ABoundedReadReturnsTheExactBytes()
        {
            string path = WriteFile("model.glb", 1000);
            Assert.That(BasisModelFileRules.TryReadBounded(path, 1000, out byte[] data, out string error), Is.True, error);
            Assert.That(error, Is.Null);
            CollectionAssert.AreEqual(File.ReadAllBytes(path), data);
        }

        [Test]
        public void AFileOverTheLimitIsRefusedAndNamesTheLimit()
        {
            string path = WriteFile("big.glb", 1001);
            Assert.That(BasisModelFileRules.TryReadBounded(path, 1000, out byte[] data, out string error), Is.False);
            Assert.That(data, Is.Null);
            Assert.That(error, Is.EqualTo("File is 1,001 bytes. The maximum is 1,000 bytes. It exceeds the limit by 1 bytes."));
        }

        [Test]
        public void AnOversizedStreamIsRefusedBeforeAnythingIsRead()
        {
            var stream = new ProbeStream(10L * 1024L * MiB);
            Assert.That(BasisModelFileRules.TryReadBounded(stream, stream.Length, 64 * MiB, out byte[] data, out string error), Is.False);
            Assert.That(data, Is.Null);
            Assert.That(stream.Reads, Is.EqualTo(0));
            StringAssert.Contains("The maximum is 64 MiB (67,108,864 bytes)", error);
        }

        [Test]
        public void ALimitAboveTheArrayMaximumIsCappedBeforeAllocating()
        {
            var stream = new ProbeStream(BasisModelFileRules.MaxArrayBytes + 1);
            Assert.That(BasisModelFileRules.TryReadBounded(stream, stream.Length, long.MaxValue, out _, out string error), Is.False);
            Assert.That(stream.Reads, Is.EqualTo(0));
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void EmptyFilesAreRefused()
        {
            string path = WriteFile("empty.glb", 0);
            Assert.That(BasisModelFileRules.TryReadBounded(path, 1000, out byte[] data, out string error), Is.False);
            Assert.That(data, Is.Null);
            Assert.That(error, Is.EqualTo("The file is empty."));
        }

        [Test]
        public void MissingFilesAreRefused()
        {
            string missingFile = Path.Combine(_directory, "missing.glb");
            Assert.That(BasisModelFileRules.TryReadBounded(missingFile, 1000, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("The file no longer exists."));

            string missingDirectory = Path.Combine(_directory, "gone", "model.glb");
            Assert.That(BasisModelFileRules.TryReadBounded(missingDirectory, 1000, out _, out error), Is.False);
            Assert.That(error, Is.EqualTo("The file no longer exists."));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("a\0.glb")]
        public void InvalidPathsAreRefused(string path)
        {
            Assert.That(BasisModelFileRules.TryReadBounded(path, 1000, out byte[] data, out string error), Is.False);
            Assert.That(data, Is.Null);
            Assert.That(error, Is.EqualTo("The file path is not valid."));
        }

        [Test]
        public void AFileThatGrewDuringTheReadIsRefused()
        {
            using (var stream = new MemoryStream(new byte[20]))
            {
                Assert.That(BasisModelFileRules.TryReadBounded(stream, 10, 1000, out byte[] data, out string error), Is.False);
                Assert.That(data, Is.Null);
                Assert.That(error, Is.EqualTo("The file changed while it was being read."));
            }
        }

        [Test]
        public void AFileThatShrankDuringTheReadIsRefused()
        {
            using (var stream = new MemoryStream(new byte[5]))
            {
                Assert.That(BasisModelFileRules.TryReadBounded(stream, 10, 1000, out byte[] data, out string error), Is.False);
                Assert.That(data, Is.Null);
                Assert.That(error, Is.EqualTo("The file changed while it was being read."));
            }
        }

        [Test]
        public void ShortReadsAreAssembled()
        {
            var stream = new ProbeStream(10) { MaxBytesPerRead = 3 };
            Assert.That(BasisModelFileRules.TryReadBounded(stream, 10, 1000, out byte[] data, out string error), Is.True, error);
            Assert.That(data.Length, Is.EqualTo(10));
            Assert.That(stream.Reads, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void AReadFailureIsReportedNotThrown()
        {
            var stream = new ProbeStream(10) { FailReads = true };
            Assert.That(BasisModelFileRules.TryReadBounded(stream, 10, 1000, out byte[] data, out string error), Is.False);
            Assert.That(data, Is.Null);
            StringAssert.StartsWith("The file could not be read", error);
        }

        [Test]
        public void ANegativeLengthIsRefused()
        {
            using (var stream = new MemoryStream(new byte[5]))
            {
                Assert.That(BasisModelFileRules.TryReadBounded(stream, -1, 1000, out _, out string error), Is.False);
                Assert.That(error, Is.EqualTo("The file size could not be determined."));
            }
        }

        [Test]
        public void ANullStreamIsAProgrammingError()
        {
            Assert.Throws<ArgumentNullException>(() => BasisModelFileRules.TryReadBounded((Stream)null, 1, 1, out _, out _));
        }

        [Test]
        public void SaveFileNamesAreGenerated()
        {
            var shape = new Regex("^Model_[0-9a-f]{32}\\.glb$");
            string first = BasisModelFileRules.GenerateSaveFileName(Guid.NewGuid());
            string second = BasisModelFileRules.GenerateSaveFileName(Guid.NewGuid());
            Assert.That(shape.IsMatch(first), Is.True, first);
            Assert.That(shape.IsMatch(second), Is.True, second);
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(BasisModelFileRules.GenerateSaveFileName(Guid.Empty), Is.EqualTo("Model_" + new string('0', 32) + ".glb"));
        }

        [Test]
        public void ByteDescriptionsIgnoreTheCurrentCulture()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.That(BasisModelFileRules.DescribeBytes(12912345), Is.EqualTo("12.31 MiB (12,912,345 bytes)"));
                Assert.That(BasisModelFileRules.DescribeBytes(512), Is.EqualTo("512 bytes"));
                Assert.That(BasisModelFileRules.DescribeBytes(32 * MiB), Is.EqualTo("32 MiB (33,554,432 bytes)"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        /// <summary>A stream that reports any length without backing storage, and counts or fails its reads.</summary>
        private sealed class ProbeStream : Stream
        {
            private readonly long _length;
            private long _position;

            public int Reads;
            public int MaxBytesPerRead = int.MaxValue;
            public bool FailReads;

            public ProbeStream(long length)
            {
                _length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Reads++;
                if (FailReads)
                    throw new IOException("simulated device error");
                int read = (int)Math.Min(Math.Min(count, MaxBytesPerRead), _length - _position);
                if (read <= 0)
                    return 0;
                for (int i = 0; i < read; i++)
                    buffer[offset + i] = (byte)(_position + i);
                _position += read;
                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
