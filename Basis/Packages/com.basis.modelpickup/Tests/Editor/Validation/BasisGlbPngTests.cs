using System.Collections.Generic;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGlbPngTests
    {
        private static readonly BasisModelLimits Limits = BasisModelLimits.Desktop;

        private static bool Canonicalize(byte[] png, out byte[] canonical, out string error, out bool overLimit)
        {
            return BasisGlbPng.TryCanonicalize(png, Limits, out canonical, out _, out _, out overLimit, out error);
        }

        private static byte[] Text(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        [Test]
        public void Crc32MatchesTheReferenceValue()
        {
            Assert.That(BasisGlbPng.Crc32(Text("IEND")), Is.EqualTo(0xAE426082u));
            Assert.That(BasisGlbPng.Crc32(Text("123456789")), Is.EqualTo(0xCBF43926u));
        }

        [Test]
        public void StripsAncillaryKeepsCriticalAndTrns()
        {
            byte[] png = BasisGlbTestPng.Create(3, 2, 2, 8, new BasisGlbTestPng.Chunk[]
            {
                new BasisGlbTestPng.Chunk("tEXt", Text("Comment\0ZZQmarker")),
                new BasisGlbTestPng.Chunk("tRNS", new byte[] { 0, 1, 0, 2, 0, 3 }),
                new BasisGlbTestPng.Chunk("iCCP", Text("profile\0\0junk")),
            }, new[] { new BasisGlbTestPng.Chunk("zTXt", Text("late\0\0ZZQmarker")) });
            Assert.That(Canonicalize(png, out byte[] canonical, out string error, out _), Is.True, error);
            List<BasisGlbTestPng.Chunk> chunks = BasisGlbTestPng.Split(canonical);
            var types = new List<string>();
            foreach (BasisGlbTestPng.Chunk chunk in chunks) types.Add(chunk.Type);
            CollectionAssert.AreEqual(new[] { "IHDR", "tRNS", "IDAT", "IEND" }, types);
            Assert.That(Encoding.ASCII.GetString(canonical).Contains("ZZQ"), Is.False);
            Assert.That(canonical.Length, Is.LessThan(png.Length));
        }

        [Test]
        public void StripIsIdempotent()
        {
            byte[] png = BasisGlbTestPng.Create(4, 4, 6, 8, new BasisGlbTestPng.Chunk("tIME", new byte[7]));
            Assert.That(Canonicalize(png, out byte[] once, out _, out _), Is.True);
            Assert.That(Canonicalize(once, out byte[] twice, out _, out _), Is.True);
            CollectionAssert.AreEqual(once, twice);
            byte[] plain = BasisGlbTestPng.Create(4, 4);
            Assert.That(Canonicalize(plain, out byte[] same, out _, out _), Is.True);
            CollectionAssert.AreEqual(plain, same);
        }

        [TestCase("signature")]
        [TestCase("crc")]
        [TestCase("ihdr-not-first")]
        [TestCase("ihdr-12")]
        [TestCase("missing-iend")]
        [TestCase("data-after-iend")]
        [TestCase("split-idat")]
        [TestCase("no-idat")]
        [TestCase("truncated")]
        [TestCase("bad-type")]
        public void RejectsStructuralErrors(string problem)
        {
            byte[] good = BasisGlbTestPng.Create(2, 2);
            List<BasisGlbTestPng.Chunk> chunks = BasisGlbTestPng.Split(good);
            byte[] bad;
            switch (problem)
            {
                case "signature":
                    bad = (byte[])good.Clone();
                    bad[1] = (byte)'X';
                    break;
                case "crc":
                    bad = (byte[])good.Clone();
                    bad[29] ^= 0xFF; // IHDR CRC
                    break;
                case "ihdr-not-first":
                    chunks.Insert(0, new BasisGlbTestPng.Chunk("tEXt", Text("a\0b")));
                    bad = BasisGlbTestPng.Join(chunks);
                    break;
                case "ihdr-12":
                    chunks[0] = new BasisGlbTestPng.Chunk("IHDR", new byte[12]);
                    bad = BasisGlbTestPng.Join(chunks);
                    break;
                case "missing-iend":
                    chunks.RemoveAt(chunks.Count - 1);
                    bad = BasisGlbTestPng.Join(chunks);
                    break;
                case "data-after-iend":
                    bad = new byte[good.Length + 4];
                    good.CopyTo(bad, 0);
                    break;
                case "split-idat":
                    chunks.Insert(chunks.Count - 1, new BasisGlbTestPng.Chunk("tEXt", Text("a\0b")));
                    chunks.Insert(chunks.Count - 1, new BasisGlbTestPng.Chunk("IDAT", new byte[2]));
                    bad = BasisGlbTestPng.Join(chunks);
                    break;
                case "no-idat":
                    chunks.RemoveAt(1);
                    bad = BasisGlbTestPng.Join(chunks);
                    break;
                case "truncated":
                    bad = new byte[good.Length - 6];
                    System.Buffer.BlockCopy(good, 0, bad, 0, bad.Length);
                    break;
                default:
                    bad = (byte[])good.Clone();
                    bad[12 + 4 + 13 + 4 + 4 + 1] = (byte)'1'; // IDAT type byte → "I1AT"
                    break;
            }
            Assert.That(Canonicalize(bad, out byte[] canonical, out string error, out bool overLimit), Is.False, problem);
            Assert.That(canonical, Is.Null);
            Assert.That(overLimit, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void RejectsUnknownCriticalChunk()
        {
            byte[] png = BasisGlbTestPng.Create(2, 2, 6, 8, new BasisGlbTestPng.Chunk("ZZQB", new byte[4]));
            Assert.That(Canonicalize(png, out _, out string error, out _), Is.False);
            Assert.That(error, Is.EqualTo("PNG has an unknown critical chunk."));
            Assert.That(Canonicalize(BasisGlbTestPng.Create(2, 2, 6, 8, new BasisGlbTestPng.Chunk("zzqB", new byte[4])), out _, out _, out _), Is.True);
        }

        [TestCase(2, 4)]
        [TestCase(6, 16)]
        [TestCase(2, 16)]
        [TestCase(0, 16)]
        [TestCase(4, 4)]
        [TestCase(5, 8)]
        [TestCase(3, 16)]
        public void RejectsInvalidDepthColorCombos(int colorType, int bitDepth)
        {
            byte[] png = BasisGlbTestPng.Create(2, 2, colorType, bitDepth);
            Assert.That(Canonicalize(png, out _, out string error, out _), Is.False);
            Assert.That(error, Is.Not.Null);
        }

        [TestCase(0, 1)]
        [TestCase(0, 8)]
        [TestCase(2, 8)]
        [TestCase(3, 2)]
        [TestCase(3, 8)]
        [TestCase(4, 8)]
        [TestCase(6, 8)]
        public void AcceptsValidDepthColorCombos(int colorType, int bitDepth)
        {
            Assert.That(Canonicalize(BasisGlbTestPng.Create(3, 3, colorType, bitDepth), out _, out string error, out _), Is.True, error);
        }

        [Test]
        public void EnforcesPaletteAndTransparencyRules()
        {
            Assert.That(Canonicalize(BasisGlbTestPng.Create(2, 2, 0, 8, new BasisGlbTestPng.Chunk("PLTE", new byte[3])), out _, out string grey, out _), Is.False);
            StringAssert.Contains("greyscale", grey);
            Assert.That(Canonicalize(BasisGlbTestPng.Create(2, 2, 3, 8, new BasisGlbTestPng.Chunk("tRNS", new byte[3])), out _, out string trns, out _), Is.False,
                "the test palette has 2 entries");
            StringAssert.Contains("tRNS", trns);
            Assert.That(Canonicalize(BasisGlbTestPng.Create(2, 2, 3, 8, new BasisGlbTestPng.Chunk("tRNS", new byte[2])), out _, out _, out _), Is.True);
            Assert.That(Canonicalize(BasisGlbTestPng.Create(2, 2, 6, 8, new BasisGlbTestPng.Chunk("tRNS", new byte[2])), out _, out string alpha, out _), Is.False);
            StringAssert.Contains("alpha channel", alpha);
            List<BasisGlbTestPng.Chunk> noPalette = BasisGlbTestPng.Split(BasisGlbTestPng.Create(2, 2, 3, 8));
            noPalette.RemoveAt(1);
            Assert.That(Canonicalize(BasisGlbTestPng.Join(noPalette), out _, out string missing, out _), Is.False);
            StringAssert.Contains("no PLTE", missing);
        }

        [Test]
        public void RejectsDimensionsAboveCapsWithoutDecoding()
        {
            byte[] huge = BasisFakeImageSanitizer.WithDimensions(BasisGlbTestPng.Create(1, 1), 2049, 1);
            Assert.That(Canonicalize(huge, out _, out string error, out bool overLimit), Is.False);
            Assert.That(overLimit, Is.True);
            StringAssert.Contains("Image is 2,049×1", error);
            byte[] edge = BasisFakeImageSanitizer.WithDimensions(BasisGlbTestPng.Create(1, 1), 2048, 2048);
            Assert.That(Canonicalize(edge, out _, out _, out _), Is.True, "IHDR alone is checked; IDAT is never inflated");
        }

        [Test]
        public void RejectsOutputAboveImageBytes()
        {
            Assert.That(Canonicalize(BasisFakeImageSanitizer.OversizePng(), out _, out string error, out bool overLimit), Is.False);
            Assert.That(overLimit, Is.True);
            StringAssert.Contains("The maximum is 8 MiB", error);
        }

        [Test]
        public void ReadsSourceDimensionsFromIhdr()
        {
            Assert.That(BasisGlbPng.TryReadDimensions(BasisGlbTestPng.Create(7, 5), out int w, out int h, out _), Is.True);
            Assert.That(w, Is.EqualTo(7));
            Assert.That(h, Is.EqualTo(5));
            Assert.That(BasisGlbPng.TryReadDimensions(BasisGlbTestPng.CreateJpegHeader(7, 5), out _, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Null);
            Assert.That(BasisGlbPng.TryReadDimensions(new byte[10], out _, out _, out _), Is.False);
        }

        [Test]
        public void RecognisesSignatures()
        {
            Assert.That(BasisGlbPng.HasSignature(BasisGlbTestPng.Create(1, 1)), Is.True);
            Assert.That(BasisGlbPng.HasJpegSignature(BasisGlbTestPng.CreateJpegHeader(1, 1)), Is.True);
            Assert.That(BasisGlbPng.HasGifSignature(BasisGlbTestPng.Gif()), Is.True);
            Assert.That(BasisGlbPng.HasWebpSignature(BasisGlbTestPng.Webp()), Is.True);
            Assert.That(BasisGlbPng.HasSignature(BasisGlbTestPng.Gif()), Is.False);
        }
    }
}
