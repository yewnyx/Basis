using System;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGlbContainerTests
    {
        private static BasisGlbValidationResult Receive(BasisGlbTestContainer container)
        {
            return BasisGlbTestRun.Receive(BasisGlbTestBuilder.Triangle().BuildGlb(container));
        }

        [Test]
        public void MinimalTriangleIsAccepted()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(BasisGlbTestBuilder.Triangle());
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Triangles, Is.EqualTo(1));
            Assert.That(result.Stats.Vertices, Is.EqualTo(3));
        }

        [TestCase(0)]
        [TestCase(11)]
        [TestCase(19)]
        public void RejectsTooShortData(int length)
        {
            byte[] data = new byte[length];
            if (length >= 4) BitConverter.GetBytes(0x46546C67u).CopyTo(data, 0);
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(data);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.Malformed, length == 0 ? "No data" : "needs at least 20");
        }

        [Test]
        public void RejectsBadMagic()
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { Magic = 0x12345678 }), BasisGlbErrorKind.Malformed, "bad magic");
        }

        [TestCase(1u)]
        [TestCase(3u)]
        public void RejectsBadVersion(uint version)
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { Version = version }), BasisGlbErrorKind.Malformed,
                "GLB version " + version + " is not supported");
        }

        [Test]
        public void RejectsHeaderLengthMismatch()
        {
            byte[] good = BasisGlbTestBuilder.Triangle().BuildGlb();
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { LengthOverride = good.Length - 4 }), BasisGlbErrorKind.Malformed, "declares");
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { LengthOverride = good.Length + 4 }), BasisGlbErrorKind.Malformed, "declares");
            // Trailing bytes the header counts are rejected by the exact-fill rule; ones it does not count by the length check.
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { TrailingBytes = 4 }), BasisGlbErrorKind.Malformed, "after its last chunk");
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { TrailingBytes = 4, LengthOverride = good.Length }),
                BasisGlbErrorKind.Malformed, "declares");
        }

        [Test]
        public void RejectsUnalignedChunkLengths()
        {
            byte[] good = BasisGlbTestBuilder.Triangle().BuildGlb();
            int jsonLength = BitConverter.ToInt32(good, 12);
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { JsonLengthOverride = jsonLength - 1 }), BasisGlbErrorKind.Malformed, "multiple of 4");
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { BinLengthOverride = 35 }), BasisGlbErrorKind.Malformed, "multiple of 4");
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { TrailingBytes = 2, LengthOverride = good.Length + 2 }), BasisGlbErrorKind.Malformed, "multiple of 4");
        }

        [TestCase(0xFFFFFFFFL)]
        [TestCase(0x7FFFFFFCL)]
        public void RejectsChunkLengthOverflow(long length)
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { JsonLengthOverride = length }), BasisGlbErrorKind.Malformed);
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { BinLengthOverride = length }), BasisGlbErrorKind.Malformed);
        }

        [Test]
        public void RejectsBinBeforeJson()
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { SwapChunks = true }), BasisGlbErrorKind.Malformed, "the first chunk must be JSON");
        }

        [Test]
        public void RejectsDuplicateJsonOrBinChunk()
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { DuplicateJson = true }), BasisGlbErrorKind.Malformed, "only JSON then BIN");
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { DuplicateBin = true }), BasisGlbErrorKind.Malformed, "after its last chunk");
        }

        [Test]
        public void RejectsUnknownChunkType()
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { ExtraChunk = true }), BasisGlbErrorKind.Malformed, "after its last chunk");
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { BinType = 0x12345678 }), BasisGlbErrorKind.Malformed, "0x12345678");
        }

        [Test]
        public void RejectsOversizeJsonChunkBeforeParsing()
        {
            // 5 MiB of invalid JSON: rejected on the chunk length alone, so the error is the size, not a syntax error.
            var json = new byte[5 * 1024 * 1024];
            for (int i = 0; i < json.Length; i++) json[i] = (byte)'!';
            byte[] glb = BasisGlbTestBuilder.Assemble(json, new byte[0], new BasisGlbTestContainer());
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(glb);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.OverLimit, "GLB JSON chunk is 5 MiB");
            StringAssert.Contains("The maximum is 4 MiB", result.Error);
        }

        [Test]
        public void RejectsBomInGlbJson()
        {
            BasisGlbTestRun.AssertFails(Receive(new BasisGlbTestContainer { Utf8Bom = true }), BasisGlbErrorKind.Malformed, "byte-order mark");
        }

        [Test]
        public void RejectsBinShorterThanBufferOrMoreThanThreeLonger()
        {
            BasisGlbTestBuilder shorter = BasisGlbTestBuilder.Triangle();
            shorter.BufferJson = "{\"byteLength\":40}";
            BasisGlbTestRun.AssertBothFail(shorter, BasisGlbErrorKind.Malformed, "BIN chunk is 36 bytes but buffers[0] declares 40");
            BasisGlbTestBuilder longer = BasisGlbTestBuilder.Triangle();
            longer.BufferJson = "{\"byteLength\":32}";
            BasisGlbTestRun.AssertBothFail(longer, BasisGlbErrorKind.Malformed, "declares 32");
            BasisGlbTestBuilder withinPadding = BasisGlbTestBuilder.Triangle();
            withinPadding.BufferJson = "{\"byteLength\":33}";
            // byteLength 33 ≤ BIN 36 ≤ 33 + 3 is fine, but the accessor then reads past buffers[0]'s declared end.
            BasisGlbTestRun.AssertBothFail(withinPadding, BasisGlbErrorKind.Malformed, "past the end of buffers[0]");
        }

        [Test]
        public void RejectsBinWhenBufferZeroHasUri()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BufferJson = "{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64,AAAA\"}";
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Send(b), BasisGlbErrorKind.Malformed, "BIN chunk but buffers[0] has a uri");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(b), BasisGlbErrorKind.Unsupported, "buffers[0] has a uri");
        }

        [Test]
        public void RejectsGlbBufferWithoutBinChunk()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            byte[] glb = b.BuildGlb(new BasisGlbTestContainer { OmitBin = true });
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(glb), BasisGlbErrorKind.Malformed, "no BIN chunk");
        }

        [TestCase(".glb", true, BasisModelSourceFormat.Glb)]
        [TestCase("model.GLB", true, BasisModelSourceFormat.Glb)]
        [TestCase("dir/model.gltf", false, BasisModelSourceFormat.GltfJson)]
        [TestCase("a.glb.exe", true, BasisModelSourceFormat.Unknown)]
        [TestCase("", true, BasisModelSourceFormat.Unknown)]
        [TestCase("a\0.glb", true, BasisModelSourceFormat.Unknown)]
        [TestCase(null, true, BasisModelSourceFormat.Glb)]
        [TestCase(null, false, BasisModelSourceFormat.GltfJson)]
        public void DetectsFormatFromExtensionAndSignature(string path, bool glbHead, BasisModelSourceFormat expected)
        {
            byte[] head = glbHead ? BasisGlbTestBuilder.Triangle().BuildGlb() : Encoding.UTF8.GetBytes(" \n{\"asset\":{}}");
            bool ok = BasisGlbValidator.TryDetectFormat(path, head, out BasisModelSourceFormat format, out string error);
            Assert.That(ok, Is.EqualTo(expected != BasisModelSourceFormat.Unknown), error);
            Assert.That(format, Is.EqualTo(expected));
            if (!ok) Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void DetectsGltfAfterOneBomAndRejectsUnknownData()
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' };
            Assert.That(BasisGlbValidator.TryDetectFormat(null, bom, out BasisModelSourceFormat format, out _), Is.True);
            Assert.That(format, Is.EqualTo(BasisModelSourceFormat.GltfJson));
            Assert.That(BasisGlbValidator.TryDetectFormat(null, Encoding.ASCII.GetBytes("PK\x03\x04"), out _, out string error), Is.False);
            StringAssert.Contains("only GLB and glTF JSON", error);
            Assert.That(BasisGlbValidator.HasSupportedModelExtension("x.GlTf"), Is.True);
            Assert.That(BasisGlbValidator.HasSupportedModelExtension("x.png"), Is.False);
            Assert.That(BasisGlbValidator.HasSupportedModelExtension(null), Is.False);
            Assert.That(BasisGlbValidator.HasSupportedModelExtension("x\0.glb"), Is.False);
        }

        [Test]
        public void RejectsExtensionSignatureMismatch()
        {
            byte[] glb = BasisGlbTestBuilder.Triangle().BuildGlb();
            byte[] json = BasisGlbTestBuilder.Triangle().BuildGltfWithDataUris();
            Assert.That(BasisGlbValidator.TryDetectFormat("a.glb", json, out _, out string glbError), Is.False);
            Assert.That(glbError, Is.EqualTo("The file extension says .glb but the data is not a GLB."));
            Assert.That(BasisGlbValidator.TryDetectFormat("a.gltf", glb, out _, out string gltfError), Is.False);
            Assert.That(gltfError, Is.EqualTo("The file extension says .gltf but the data is not glTF JSON."));
        }
    }
}
