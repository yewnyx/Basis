using System;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfDataUriTests
    {
        private static byte[] TriangleBin()
        {
            return BasisGlbTestBuilder.Triangle().BinBytes();
        }

        private static byte[] GltfWithBufferUri(string uri, long byteLength = 36)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BufferJson = "{\"byteLength\":" + byteLength + ",\"uri\":\"" + uri + "\"}";
            return Encoding.UTF8.GetBytes(b.BuildJson());
        }

        private static BasisGlbValidationResult SendGltf(byte[] gltf)
        {
            return BasisGlbTestRun.Send(gltf, BasisModelSourceFormat.GltfJson);
        }

        [Test]
        public void GltfConvertsToSameCanonicalBytesAsGlb()
        {
            foreach (BasisGlbTestBuilder builder in new[] { BasisGlbTestBuilder.Triangle(), BasisGlbTestBuilder.TexturedQuad(4, 4), BasisGlbTestBuilder.SkinnedStrip(2) })
            {
                BasisGlbValidationResult fromGlb = BasisGlbTestRun.Send(builder.BuildGlb());
                BasisGlbValidationResult fromGltf = SendGltf(builder.BuildGltfWithDataUris());
                BasisGlbTestRun.AssertOk(fromGlb);
                BasisGlbTestRun.AssertOk(fromGltf);
                CollectionAssert.AreEqual(fromGlb.CleanGlb, fromGltf.CleanGlb);
            }
        }

        [Test]
        public void RejectsNonBase64DataUris()
        {
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream,%00%01")), BasisGlbErrorKind.Unsupported, "without base64");
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream;charset=x;base64,AAAA")),
                BasisGlbErrorKind.Unsupported, "parameters other than base64");
        }

        [TestCase('*')]
        [TestCase('-')]
        [TestCase('_')]
        [TestCase('=')]
        [TestCase(' ')]
        public void RejectsMalformedBase64(char bad)
        {
            // A payload of the right length (48 characters for 36 bytes) with one character outside the strict alphabet.
            char[] payload = Convert.ToBase64String(TriangleBin()).ToCharArray();
            payload[10] = bad;
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + new string(payload))),
                BasisGlbErrorKind.Malformed, "malformed base64");
        }

        [Test]
        public void RejectsBase64WithOneLeftoverCharacter()
        {
            string payload = Convert.ToBase64String(TriangleBin()) + "A";
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + payload)),
                BasisGlbErrorKind.Malformed, "malformed base64");
        }

        [Test]
        public void AcceptsUnpaddedBase64()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            byte[] bin = b.BinBytes();
            var longer = new byte[38];
            Buffer.BlockCopy(bin, 0, longer, 0, bin.Length);
            string payload = Convert.ToBase64String(longer).TrimEnd('=');
            BasisGlbTestRun.AssertOk(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + payload, 38)));
        }

        [Test]
        public void RejectsDecodedLengthMismatch()
        {
            string payload = Convert.ToBase64String(TriangleBin());
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + payload, 40)),
                BasisGlbErrorKind.Malformed, "decodes to 36 bytes but declares 40");
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + payload, 32)),
                BasisGlbErrorKind.Malformed, "decodes to 36 bytes but declares 32");
            BasisGlbTestRun.AssertOk(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + payload, 36)));
        }

        [Test]
        public void RejectsOversizeUrisBeforeAllocating()
        {
            // A small file declaring a huge buffer fails on length math alone.
            string payload = Convert.ToBase64String(TriangleBin());
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:application/octet-stream;base64," + payload, 4294967295L)),
                BasisGlbErrorKind.Malformed, "decodes to 36 bytes but declares 4,294,967,295");

            // An embedded image larger than the source-image cap is refused before it is decoded.
            var limits = BasisModelLimits.Desktop;
            limits.MaxSourceImageBytes = 16;
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            string json = Encoding.UTF8.GetString(b.BuildGltfWithDataUris());
            int imageStart = json.IndexOf("\"images\":[", StringComparison.Ordinal);
            json = json.Substring(0, imageStart) + "\"images\":[{\"uri\":\"data:image/png;base64," + Convert.ToBase64String(new byte[64]) + "\"}]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(Encoding.UTF8.GetBytes(json), BasisModelSourceFormat.GltfJson, limits);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.OverLimit, "images[0] is 64 bytes. The maximum is 16 bytes.");
        }

        [Test]
        public void RejectsTooManyDataUris()
        {
            var limits = BasisModelLimits.Desktop;
            limits.MaxDataUris = 1;
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Send(b.BuildGltfWithDataUris(), BasisModelSourceFormat.GltfJson, limits));
            string json = Encoding.UTF8.GetString(b.BuildGltfWithDataUris());
            int imageStart = json.IndexOf("\"images\":[", StringComparison.Ordinal);
            json = json.Substring(0, imageStart) + "\"images\":[{\"uri\":\"data:image/png;base64," + Convert.ToBase64String(BasisGlbTestPng.Create(4, 4)) + "\"}]}";
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Send(Encoding.UTF8.GetBytes(json), BasisModelSourceFormat.GltfJson, limits),
                BasisGlbErrorKind.OverLimit, "data: URI count is 2. The maximum is 1.");
        }

        [Test]
        public void AcceptsEscapedSlashAndImageDataUris()
        {
            string payload = Convert.ToBase64String(TriangleBin()).Replace("/", "\\/");
            BasisGlbTestRun.AssertOk(SendGltf(GltfWithBufferUri("data:application\\/octet-stream;base64," + payload)));

            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            string json = Encoding.UTF8.GetString(b.BuildGltfWithDataUris());
            int imageStart = json.IndexOf("\"images\":[", StringComparison.Ordinal);
            string png = Convert.ToBase64String(BasisGlbTestPng.Create(4, 4));
            json = json.Substring(0, imageStart) + "\"images\":[{\"uri\":\"data:image\\/png;base64," + png + "\"}]}";
            BasisGlbValidationResult result = SendGltf(Encoding.UTF8.GetBytes(json));
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Images, Is.EqualTo(1));
            Assert.That(result.Stripped & BasisGlbStripped.UnreferencedContent, Is.EqualTo(BasisGlbStripped.UnreferencedContent), "the old image view is unused");
        }

        [Test]
        public void RejectsUnsupportedMediaTypes()
        {
            string payload = Convert.ToBase64String(TriangleBin());
            BasisGlbTestRun.AssertFails(SendGltf(GltfWithBufferUri("data:text/plain;base64," + payload)), BasisGlbErrorKind.Unsupported, "media type");
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            string json = Encoding.UTF8.GetString(b.BuildGltfWithDataUris());
            int imageStart = json.IndexOf("\"images\":[", StringComparison.Ordinal);
            json = json.Substring(0, imageStart) + "\"images\":[{\"uri\":\"data:image/gif;base64,R0lGODlh\"}]}";
            BasisGlbTestRun.AssertFails(SendGltf(Encoding.UTF8.GetBytes(json)), BasisGlbErrorKind.Unsupported, "not image/png or image/jpeg");
            json = json.Replace("data:image/gif;base64,R0lGODlh", "data:image/png;base64," + Convert.ToBase64String(BasisGlbTestPng.Create(2, 2)))
                .Replace("\"images\":[{\"uri\"", "\"images\":[{\"mimeType\":\"image/jpeg\",\"uri\"");
            BasisGlbTestRun.AssertFails(SendGltf(Encoding.UTF8.GetBytes(json)), BasisGlbErrorKind.Malformed, "mimeType does not match its data: URI");
        }

        [Test]
        public void SkipsLeadingBomInGltfOnly()
        {
            BasisGlbTestRun.AssertOk(SendGltf(BasisGlbTestBuilder.Triangle().BuildGltfWithDataUris(true)));
            byte[] doubleBom = BasisGlbTestBuilder.Triangle().BuildGltfWithDataUris(true);
            var two = new byte[doubleBom.Length + 3];
            two[0] = 0xEF;
            two[1] = 0xBB;
            two[2] = 0xBF;
            Buffer.BlockCopy(doubleBom, 0, two, 3, doubleBom.Length);
            BasisGlbTestRun.AssertFails(SendGltf(two), BasisGlbErrorKind.Malformed);
        }

        [Test]
        public void ReceiverRejectsDataUris()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BuildGlb();
            byte[] glb = BasisGlbTestRun.WithJson(b.BuildGlb(), Encoding.UTF8.GetString(GltfWithBufferUri("data:application/octet-stream;base64,ZZQ")));
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(glb);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.Unsupported, "buffers[0] has a uri.");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(BasisGlbTestBuilder.Triangle().BuildGltfWithDataUris()), BasisGlbErrorKind.Malformed, "bad magic");
        }
    }
}
