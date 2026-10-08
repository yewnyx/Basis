using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Basis.ModelPickup.Tests.Validation;
using Basis.ModelPickup.Validation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Basis.ModelPickup.Tests.BasisModelUnityTestSupport;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelImageSanitizerTests
    {
        private const string PngOrJpegOnly = "Only PNG or JPEG images can be embedded";

        private static IEnumerator Run(byte[] image, BasisModelImageFormat format, CancellationToken token, BasisModelImageSanitizeResult[] output)
        {
            Task<BasisModelImageSanitizeResult> task = BasisModelImageSanitizer.Instance.SanitizeAsync(image, format, token);
            yield return WaitFor(task);
            Assert.That(task.IsFaulted, Is.False, "the sanitiser never throws");
            Assert.That(task.IsCanceled, Is.False);
            output[0] = task.Result;
        }

        private static void AssertEightBitRgbaPng(byte[] png, uint width, uint height)
        {
            Assert.That(png, Is.Not.Null);
            Assert.That(png.Length, Is.GreaterThan(33));
            Assert.That(png[0], Is.EqualTo(0x89));
            Assert.That(png[1], Is.EqualTo((byte)'P'));
            // IHDR: width and height (big-endian) at 16 and 20, bit depth at 24, colour type at 25.
            Assert.That(BasisGlbTestPng.ReadBigEndian(png, 16), Is.EqualTo(width));
            Assert.That(BasisGlbTestPng.ReadBigEndian(png, 20), Is.EqualTo(height));
            Assert.That(png[24], Is.EqualTo(8), "8-bit");
            Assert.That(png[25], Is.EqualTo(6), "RGBA");
        }

        [UnityTest]
        public IEnumerator RefusesAGifEvenWhenLabelledPng()
        {
            var output = new BasisModelImageSanitizeResult[1];
            yield return Run(BasisGlbTestPng.Gif(), BasisModelImageFormat.Png, CancellationToken.None, output);
            Assert.That(output[0].Ok, Is.False);
            Assert.That(output[0].Error, Is.EqualTo(PngOrJpegOnly));
            Assert.That(output[0].Png, Is.Null);
        }

        [UnityTest]
        public IEnumerator BadInputComesBackAsAReasonNeverAnException()
        {
            var output = new BasisModelImageSanitizeResult[1];
            byte[] truncatedPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            // A valid header with nothing behind it: passes the header check, fails the decode.
            byte[] headerOnlyJpeg = BasisGlbTestPng.CreateJpegHeader(4, 4);
            byte[][] inputs = { null, new byte[0], truncatedPng, BasisGlbTestPng.Webp(), headerOnlyJpeg };
            for (int i = 0; i < inputs.Length; i++)
            {
                yield return Run(inputs[i], BasisModelImageFormat.Png, CancellationToken.None, output);
                Assert.That(output[0].Ok, Is.False, "input " + i);
                Assert.That(output[0].Error, Is.Not.Null.And.Not.Empty, "input " + i);
                Assert.That(output[0].Png, Is.Null, "input " + i);
            }

            yield return Run(BasisGlbTestPng.Create(4, 4), (BasisModelImageFormat)99, CancellationToken.None, output);
            Assert.That(output[0].Ok, Is.False, "an unknown declared format");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                yield return Run(BasisGlbTestPng.Create(4, 4), BasisModelImageFormat.Png, cancelled.Token, output);
            }
            Assert.That(output[0].Ok, Is.False, "a cancelled request");
        }

        [UnityTest]
        public IEnumerator AnOversizedHeaderIsRefusedBeforeAnythingDecodes()
        {
            var output = new BasisModelImageSanitizeResult[1];
            // Header only: were it decoded, the decode would fail with a different reason.
            yield return Run(BasisGlbTestPng.CreateJpegHeader(5000, 8), BasisModelImageFormat.Jpeg, CancellationToken.None, output);
            Assert.That(output[0].Ok, Is.False);
            StringAssert.StartsWith("Source image is 5,000×8", output[0].Error);
        }

        [UnityTest]
        public IEnumerator ReencodesAPngIntoAnEightBitRgbaPng()
        {
            var output = new BasisModelImageSanitizeResult[1];
            yield return Run(BasisGlbTestPng.Create(6, 3, 2), BasisModelImageFormat.Png, CancellationToken.None, output);
            Assert.That(output[0].Ok, Is.True, output[0].Error);
            AssertEightBitRgbaPng(output[0].Png, 6, 3);
        }

        [UnityTest]
        public IEnumerator ReencodesAJpegIntoAnEightBitRgbaPng()
        {
            var texture = new Texture2D(5, 7, TextureFormat.RGB24, false);
            byte[] jpeg;
            try
            {
                jpeg = texture.EncodeToJPG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            var output = new BasisModelImageSanitizeResult[1];
            yield return Run(jpeg, BasisModelImageFormat.Jpeg, CancellationToken.None, output);
            Assert.That(output[0].Ok, Is.True, output[0].Error);
            AssertEightBitRgbaPng(output[0].Png, 5, 7);
        }

        [UnityTest]
        public IEnumerator AnImageOverTheTextureCapIsScaledDownToIt()
        {
            int cap = BasisModelLimits.Desktop.MaxTextureDimension;
            var output = new BasisModelImageSanitizeResult[1];
            yield return Run(BasisGlbTestPng.Create(cap * 2, 4), BasisModelImageFormat.Png, CancellationToken.None, output);
            Assert.That(output[0].Ok, Is.True, output[0].Error);
            AssertEightBitRgbaPng(output[0].Png, (uint)cap, 2);
        }
    }
}
