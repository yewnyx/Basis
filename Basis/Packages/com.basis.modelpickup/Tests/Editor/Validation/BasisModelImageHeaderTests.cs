using System;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public sealed class BasisModelImageHeaderTests
    {
        private static readonly BasisModelLimits Limits = BasisModelLimits.Desktop;

        [Test]
        public void PngAndJpegHeadersAreReadFromTheirSignature()
        {
            Assert.That(BasisModelImageHeader.TryCheckSource(BasisGlbTestPng.Create(6, 3), Limits,
                out BasisModelImageFormat format, out int width, out int height, out string error), Is.True, error);
            Assert.That(format, Is.EqualTo(BasisModelImageFormat.Png));
            Assert.That((width, height), Is.EqualTo((6, 3)));

            Assert.That(BasisModelImageHeader.TryCheckSource(BasisGlbTestPng.CreateJpegHeader(640, 480), Limits,
                out format, out width, out height, out error), Is.True, error);
            Assert.That(format, Is.EqualTo(BasisModelImageFormat.Jpeg));
            Assert.That((width, height), Is.EqualTo((640, 480)), "the APP0 segment before the frame header is skipped");
        }

        [Test]
        public void AnythingButPngOrJpegIsRefused()
        {
            byte[][] inputs = { BasisGlbTestPng.Gif(), BasisGlbTestPng.Webp(), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } };
            for (int i = 0; i < inputs.Length; i++)
            {
                Assert.That(BasisModelImageHeader.TryCheckSource(inputs[i], Limits, out _, out _, out _, out string error), Is.False);
                Assert.That(error, Is.EqualTo(BasisModelImageHeader.PngOrJpegOnly), "input " + i);
            }
            Assert.That(BasisModelImageHeader.TryCheckSource(ReadOnlySpan<byte>.Empty, Limits, out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void SourceCapsAreCheckedFromTheHeader()
        {
            int side = Limits.MaxSourceTextureDimension;
            Assert.That(BasisModelImageHeader.TryCheckSource(BasisGlbTestPng.CreateJpegHeader(side, side), Limits,
                out _, out _, out _, out string error), Is.True, error);
            Assert.That(BasisModelImageHeader.TryCheckSource(BasisGlbTestPng.CreateJpegHeader(side + 1, 1), Limits,
                out _, out _, out _, out error), Is.False);
            StringAssert.Contains("width exceeds the limit by 1px", error);

            BasisModelLimits small = Limits;
            small.MaxSourceImageBytes = 16;
            Assert.That(BasisModelImageHeader.TryCheckSource(BasisGlbTestPng.Create(2, 2), small, out _, out _, out _, out error), Is.False);
            StringAssert.StartsWith("Source image is", error);
        }

        [Test]
        public void MalformedJpegSegmentsAreRefused()
        {
            byte[] header = BasisGlbTestPng.CreateJpegHeader(8, 8);

            // Cut inside the frame header's length field.
            byte[] truncated = new byte[24];
            Array.Copy(header, truncated, truncated.Length);
            Assert.That(BasisModelImageHeader.TryReadJpegDimensions(truncated, out _, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Null);

            // Scan data before any frame header.
            byte[] scanFirst = { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9 };
            Assert.That(BasisModelImageHeader.TryReadJpegDimensions(scanFirst, out _, out _, out error), Is.False);
            StringAssert.Contains("before its frame header", error);

            // A zero-sized frame.
            byte[] zero = BasisGlbTestPng.CreateJpegHeader(0, 8);
            Assert.That(BasisModelImageHeader.TryReadJpegDimensions(zero, out _, out _, out error), Is.False);
        }

        [TestCase(2048, 2048, 2048, 2048)]
        [TestCase(4096, 4, 2048, 2)]
        [TestCase(10, 4096, 5, 2048)]
        [TestCase(4096, 1, 2048, 1)]
        public void FitWithinScalesTheLongerSideToTheCap(int width, int height, int expectedWidth, int expectedHeight)
        {
            BasisModelImageHeader.FitWithin(width, height, 2048, out int targetWidth, out int targetHeight);
            Assert.That((targetWidth, targetHeight), Is.EqualTo((expectedWidth, expectedHeight)));
        }
    }
}
