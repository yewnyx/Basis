using System;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfStructureTests
    {
        [TestCase(null, BasisGlbErrorKind.Malformed, "no asset")]
        [TestCase("{\"version\":\"1.0\"}", BasisGlbErrorKind.Unsupported, "asset.version")]
        [TestCase("{\"version\":\"3.0\"}", BasisGlbErrorKind.Unsupported, "asset.version")]
        [TestCase("{\"version\":\"2.0\",\"minVersion\":\"2.1\"}", BasisGlbErrorKind.Unsupported, "minVersion")]
        [TestCase("{}", BasisGlbErrorKind.Malformed, "asset.version is missing")]
        [TestCase("{\"version\":2}", BasisGlbErrorKind.Malformed, "must be a string")]
        public void RejectsMissingAssetOrWrongVersion(string asset, BasisGlbErrorKind kind, string message)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Asset = asset;
            if (asset == null)
            {
                string json = b.BuildJson().Replace("\"asset\":,", string.Empty);
                BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(BasisGlbTestRun.WithJson(b.BuildGlb(), json)), kind, message);
                return;
            }
            BasisGlbTestRun.AssertBothFail(b, kind, message);
        }

        [Test]
        public void AcceptsMinorVersionAndMinVersion20()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Asset = "{\"version\":\"2.1\",\"minVersion\":\"2.0\"}";
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Receive(b));
        }

        [Test]
        public void RejectsBufferViewOutsideBuffer()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BufferViews[0] = "{\"buffer\":0,\"byteOffset\":4,\"byteLength\":36}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "past the end of buffers[0]");
            b.BufferViews[0] = "{\"buffer\":0,\"byteOffset\":2147483647,\"byteLength\":36}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "past the end of buffers[0]");
            b.BufferViews[0] = "{\"buffer\":0,\"byteOffset\":4294967295,\"byteLength\":4294967295}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "past the end of buffers[0]");
            b.BufferViews[0] = "{\"buffer\":1,\"byteLength\":36}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "bufferViews[0].buffer is out of range");
            b.BufferViews[0] = "{\"buffer\":0,\"byteLength\":0}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "byteLength must be at least 1");
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(6)]
        [TestCase(256)]
        public void RejectsInvalidByteStride(int stride)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BufferViews[0] = "{\"buffer\":0,\"byteLength\":36,\"byteStride\":" + stride + "}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "byteStride must be a multiple of 4 from 4 to 252");
        }

        [Test]
        public void RejectsStrideShorterThanTheElement()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BufferViews[0] = "{\"buffer\":0,\"byteLength\":36,\"byteStride\":8}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "elements are 12 bytes");
        }

        [TestCase("\"count\":2147483647")]
        [TestCase("\"count\":3,\"byteOffset\":2147483647")]
        [TestCase("\"count\":4")]
        public void RejectsAccessorPastViewEnd(string members)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"type\":\"VEC3\"," + members + "}";
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.ErrorKind == BasisGlbErrorKind.Malformed || result.ErrorKind == BasisGlbErrorKind.OverLimit, Is.True, result.Error);
        }

        [Test]
        public void RejectsStrideTimesCountOverflow()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.BufferViews[0] = "{\"buffer\":0,\"byteLength\":36,\"byteStride\":252}";
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"type\":\"VEC3\",\"count\":4194303}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "reads past the end of bufferViews[0]");
        }

        [Test]
        public void RejectsAccessorWithoutBufferView()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"componentType\":5126,\"type\":\"VEC3\",\"count\":3}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "accessors[0] has no bufferView");
        }

        [Test]
        public void RejectsRetainedSparseButAcceptsSparseOnlyInStrippedAnimation()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = b.Accessors[0].TrimEnd('}') + ",\"sparse\":{\"count\":1,\"indices\":{\"bufferView\":0,\"componentType\":5125},\"values\":{\"bufferView\":0}}}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Unsupported, "sparse storage");

            BasisGlbTestBuilder animated = BasisGlbTestBuilder.Triangle();
            int times = animated.AddAccessor(0, BasisGltfComponent.Float, 1, "SCALAR", false, 0,
                "\"sparse\":{\"count\":1,\"indices\":{\"bufferView\":0,\"componentType\":5125},\"values\":{\"bufferView\":0}}");
            animated.ExtraRootMembers = "\"animations\":[{\"channels\":[],\"samplers\":[{\"input\":" + times + ",\"output\":" + times + "}]}]";
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(animated);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.Animations, Is.EqualTo(BasisGlbStripped.Animations));
        }

        [TestCase(5124, "VEC3")]
        [TestCase(5126, "vec3")]
        [TestCase(5126, "MAT5")]
        [TestCase(1, "VEC3")]
        public void RejectsUnknownComponentTypeOrType(int componentType, string type)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":" + componentType + ",\"count\":3,\"type\":\"" + type + "\"}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "accessors[0]");
        }

        [Test]
        public void RejectsNormalizedFloat()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"normalized\":true}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "normalized");
        }

        [TestCase("http://x/a.bin")]
        [TestCase("file:///C:/a.bin")]
        [TestCase("a.bin")]
        [TestCase("\\\\\\\\srv\\\\s\\\\a.bin")]
        [TestCase("../a.bin")]
        public void RejectsExternalUris(string uri)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            string json = b.BuildJson().Replace("\"buffers\":[{\"byteLength\":36}]", "\"buffers\":[{\"byteLength\":36,\"uri\":\"" + uri + "\"}]");
            byte[] gltf = System.Text.Encoding.UTF8.GetBytes(json);
            BasisGlbValidationResult result = BasisGlbTestRun.Send(gltf, BasisModelSourceFormat.GltfJson);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.Unsupported, "references the external file");
            StringAssert.Contains("Only .glb files and .gltf files with embedded data: URIs are supported.", result.Error);
        }

        [Test]
        public void ExternalUriTextIsCappedAndStrippedOfControls()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            string uri = "\\u202Eevil" + new string('x', 200);
            string json = b.BuildJson().Replace("\"buffers\":[{\"byteLength\":36}]", "\"buffers\":[{\"byteLength\":36,\"uri\":\"" + uri + "\"}]");
            BasisGlbValidationResult result = BasisGlbTestRun.Send(System.Text.Encoding.UTF8.GetBytes(json), BasisModelSourceFormat.GltfJson);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.Unsupported);
            // Ordinal check: a culture-aware Contains treats format characters as ignorable and always matches them.
            Assert.That(result.Error.IndexOf('\u202E'), Is.EqualTo(-1));
            Assert.That(result.Error, Does.Not.Contain(new string('x', 65)));
        }

        [Test]
        public void MergesMultipleDataUriBuffersIntoOneBin()
        {
            var b = new BasisGlbTestBuilder();
            byte[] positions = new byte[36];
            for (int i = 0; i < 9; i++) BitConverter.GetBytes(BasisGlbTestBuilder.TrianglePositions[i]).CopyTo(positions, i * 4);
            byte[] indices = { 0, 1, 2, 0 };
            string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],"
                + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5121,\"count\":3,\"type\":\"SCALAR\"}],"
                + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36},{\"buffer\":1,\"byteLength\":3}],"
                + "\"buffers\":[{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(positions) + "\"},"
                + "{\"byteLength\":3,\"uri\":\"data:application/gltf-buffer;base64," + Convert.ToBase64String(indices) + "\"}]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(System.Text.Encoding.UTF8.GetBytes(json), BasisModelSourceFormat.GltfJson);
            BasisGlbTestRun.AssertOk(result);
            string canonical = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"buffers\":[{\"byteLength\":40}]", canonical);
            Assert.That(canonical, Does.Not.Contain("\"buffer\":1"));
        }

        [Test]
        public void RejectsIndexViewWithByteStride()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.IndexedQuad16();
            b.BufferViews[3] = b.BufferViews[3].TrimEnd('}') + ",\"byteStride\":4}";
            b.Accessors[3] = "{\"bufferView\":3,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "holds indices and must not have a byteStride");
        }

        [Test]
        public void RejectsWrongJsonTypesWithTheirPath()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"count\":\"3\",\"type\":\"VEC3\"}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "accessors[0].count must be a non-negative integer.");
            b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":null}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "nodes[0].mesh must be a non-negative integer.");
            b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"translation\":[1,2]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "nodes[0].translation must be exactly 3 finite numbers.");
        }
    }
}
