using System;
using System.Collections.Generic;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGlbStatsTests
    {
        [Test]
        public void CorpusStatsMatchHandValues()
        {
            BasisGlbStats triangle = Ok(BasisGlbTestBuilder.Triangle());
            Assert.That(triangle.Nodes, Is.EqualTo(1));
            Assert.That(triangle.Meshes, Is.EqualTo(1));
            Assert.That(triangle.Primitives, Is.EqualTo(1));
            Assert.That(triangle.Accessors, Is.EqualTo(1));
            Assert.That(triangle.Vertices, Is.EqualTo(3));
            Assert.That(triangle.Triangles, Is.EqualTo(1));
            Assert.That(triangle.DrawCalls, Is.EqualTo(1));
            Assert.That(triangle.MeshInstances, Is.EqualTo(1));
            Assert.That(triangle.RenderedTriangles, Is.EqualTo(1));
            Assert.That(triangle.BinBytes, Is.EqualTo(36));
            Assert.That(triangle.HasSkins || triangle.UsesQuantization || triangle.UsesUnlit || triangle.UsesTextureTransform, Is.False);
            AssertBounds(triangle.Bounds, 0, 0, 0, 1, 1, 0);

            BasisGlbStats quad = Ok(BasisGlbTestBuilder.IndexedQuad16());
            Assert.That(quad.Vertices, Is.EqualTo(4));
            Assert.That(quad.Triangles, Is.EqualTo(2));
            Assert.That(quad.Accessors, Is.EqualTo(4));

            BasisGlbStats skinned = Ok(BasisGlbTestBuilder.SkinnedStrip(3));
            Assert.That(skinned.Vertices, Is.EqualTo(8));
            Assert.That(skinned.Triangles, Is.EqualTo(6));
            Assert.That(skinned.Skins, Is.EqualTo(1));
            Assert.That(skinned.Joints, Is.EqualTo(3));
            Assert.That(skinned.Nodes, Is.EqualTo(4));
            AssertBounds(skinned.Bounds, 0, 0, 0, 1, 1.5f, 0);

            BasisGlbStats hierarchy = Ok(BasisGlbTestBuilder.Hierarchy(5));
            Assert.That(hierarchy.Nodes, Is.EqualTo(5));
            AssertBounds(hierarchy.Bounds, 5, 0, 0, 6, 1, 0);

            BasisGlbStats two = Ok(BasisGlbTestCorpus.TwoMaterialsTwoSamplers());
            Assert.That(two.Materials, Is.EqualTo(2));
            Assert.That(two.Primitives, Is.EqualTo(2));
            Assert.That(two.DrawCalls, Is.EqualTo(2));
            Assert.That(two.Textures, Is.EqualTo(2));
            Assert.That(two.Samplers, Is.EqualTo(2));
            Assert.That(two.Images, Is.EqualTo(1));
        }

        [Test]
        public void VerticesMirrorGltfastClustering()
        {
            // Two primitives sharing every attribute accessor share one vertex buffer in glTFast.
            Assert.That(Ok(BasisGlbTestCorpus.TwoMaterialsTwoSamplers()).Vertices, Is.EqualTo(4));

            // Same layout, different POSITION accessors: one cluster, two vertex buffers.
            var b = new BasisGlbTestBuilder();
            int p0 = b.AddFloats("VEC3", BasisGlbTestBuilder.TrianglePositions);
            int p1 = b.AddFloats("VEC3", BasisGlbTestBuilder.TrianglePositions);
            int n0 = b.AddFloats("VEC3", 0, 0, 1, 0, 0, 1, 0, 0, 1);
            b.Meshes.Add("{\"primitives\":[" + BasisGlbTestBuilder.Primitive(p0) + "," + BasisGlbTestBuilder.Primitive(p1) + ","
                + BasisGlbTestBuilder.Primitive(p0, -1, "\"NORMAL\":" + n0) + "," + BasisGlbTestBuilder.Primitive(p0) + "]}");
            b.AddNode("{\"mesh\":0}");
            b.AddNode("{\"mesh\":0}");
            BasisGlbStats stats = Ok(b);
            // Cluster A (no normals): p0 and p1 (the fourth primitive reuses p0) → 6; cluster B (normals): p0+n0 → 3.
            Assert.That(stats.Vertices, Is.EqualTo(9));
            Assert.That(stats.Triangles, Is.EqualTo(4));
            Assert.That(stats.RenderedTriangles, Is.EqualTo(8));
            Assert.That(stats.DrawCalls, Is.EqualTo(8));
            Assert.That(stats.MeshInstances, Is.EqualTo(2));
        }

        [Test]
        public void TextureVariantsFollowSamplerKeys()
        {
            // One 4×4 image through two sampler keys (point/clamp and trilinear/repeat): cloned once, readable.
            BasisGlbStats two = Ok(BasisGlbTestCorpus.TwoMaterialsTwoSamplers());
            Assert.That(two.TexturePixels, Is.EqualTo(32));
            Assert.That(two.MaxTextureDimension, Is.EqualTo(4));

            // NEAREST and NEAREST_MIPMAP_NEAREST map to the same Unity key: one variant, but two sampler indices.
            BasisGlbTestBuilder b = BasisGlbTestCorpus.TwoMaterialsTwoSamplers();
            b.Samplers[0] = "{\"minFilter\":9728}";
            b.Samplers[1] = "{\"minFilter\":9984}";
            BasisGlbStats sameKey = Ok(b);
            Assert.That(sameKey.Samplers, Is.EqualTo(2));
            Assert.That(sameKey.TexturePixels, Is.EqualTo(16));
        }

        [Test]
        public void EstimateMatchesFormula()
        {
            // Triangle: one cluster of 3 vertices, 3 generated indices; (3·40 + 3·4)·2 bytes of mesh data.
            BasisGlbStats triangle = Ok(BasisGlbTestBuilder.Triangle());
            Assert.That(triangle.EstimatedDecodedBytes, Is.EqualTo((3 * 40 + 3 * 4) * 2));
            Assert.That(triangle.EstimatedPeakBytes, Is.EqualTo(triangle.EstimatedDecodedBytes + triangle.CanonicalBytes + (3 * 40 + 3 * 4)));
            Assert.That(triangle.RecalculatedNormalVertices, Is.EqualTo(3), "no NORMAL and the default material is lit");
            Assert.That(triangle.RecalculatedTangentVertices, Is.EqualTo(0));

            // Textured quad 8×4: 4 vertices with one UV set, 6 indices; texture 32 px · 16 / 3, one variant.
            BasisGlbValidationResult textured = BasisGlbTestRun.Send(BasisGlbTestBuilder.TexturedQuad(8, 4));
            BasisGlbTestRun.AssertOk(textured);
            long mesh = (4 * (40 + 8) + 6 * 4) * 2;
            long texture = 32 * 16 / 3;
            Assert.That(textured.Stats.EstimatedDecodedBytes, Is.EqualTo(mesh + texture));
            int png = BasisGlbTestPng.Create(8, 4).Length;
            Assert.That(textured.Stats.EstimatedPeakBytes, Is.EqualTo(mesh + texture + textured.Stats.CanonicalBytes + png + mesh / 2));
            Assert.That(textured.Stats.RecalculatedNormalVertices, Is.EqualTo(0));

            // Skinned: bones add 32 B per vertex, and each skinned instance adds 40 B per vertex of skinning output.
            BasisGlbStats skinned = Ok(BasisGlbTestBuilder.SkinnedStrip(2));
            long skinnedMesh = (6 * (40 + 32) + 12 * 4) * 2;
            Assert.That(skinned.EstimatedDecodedBytes, Is.EqualTo(skinnedMesh + 6 * 40));
        }

        [Test]
        public void BoundsIncludeTransformsAndDequantization()
        {
            // u16 positions up to 1000 under a 0.001 node scale.
            AssertBounds(Ok(BasisGlbTestBuilder.Quantized()).Bounds, 0, 0, 0, 1, 1, 0);

            // Normalised u16 positions: 65535 → 1.0.
            var b = new BasisGlbTestBuilder();
            var data = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0 };
            int position = b.AddAccessor(b.AddView(data, 8), BasisGltfComponent.UnsignedShort, 3, "VEC3", true);
            b.AddNode("{\"mesh\":" + b.AddMesh(position) + ",\"translation\":[0,0,2]}");
            AssertBounds(Ok(b).Bounds, 0, 0, 2, 1, 1, 2);

            // Normalised i8: -128 clamps to -1.
            var s = new BasisGlbTestBuilder();
            var bytes = new byte[] { 0x80, 0x80, 0, 0, 127, 0, 0, 0, 0, 127, 0, 0 };
            int signed = s.AddAccessor(s.AddView(bytes, 4), BasisGltfComponent.Byte, 3, "VEC3", true);
            s.AddNode("{\"mesh\":" + s.AddMesh(signed) + "}");
            AssertBounds(Ok(s).Bounds, -1, -1, 0, 1, 1, 0);
        }

        [Test]
        public void FlatQuadIsAccepted()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.IndexedQuad16());
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Bounds.SizeY, Is.EqualTo(0f));
            Assert.That(BasisGlbClaims.FromStats(result.Stats).TryAdmit(result.CleanGlb.Length, BasisModelLimits.Mobile, out string error), Is.True, error);
        }

        [Test]
        public void RejectsEmptyAndHugeBounds()
        {
            var empty = new BasisGlbTestBuilder();
            int zero = empty.AddFloats("VEC3", 0, 0, 0, 0, 0, 0, 0, 0, 0);
            empty.AddNode("{\"mesh\":" + empty.AddMesh(zero) + "}");
            BasisGlbTestRun.AssertBothFail(empty, BasisGlbErrorKind.OverLimit, "bounds are empty");

            var tiny = new BasisGlbTestBuilder();
            int small = tiny.AddFloats("VEC3", 0, 0, 0, 0.00005f, 0, 0, 0, 0.00005f, 0);
            tiny.AddNode("{\"mesh\":" + tiny.AddMesh(small) + "}");
            BasisGlbTestRun.AssertBothFail(tiny, BasisGlbErrorKind.OverLimit, "bounds are empty");

            var wide = new BasisGlbTestBuilder();
            int big = wide.AddFloats("VEC3", 0, 0, 0, 20000, 0, 0, 0, 1, 0);
            wide.AddNode("{\"mesh\":" + wide.AddMesh(big) + "}");
            BasisGlbTestRun.AssertBothFail(wide, BasisGlbErrorKind.OverLimit, "The model is 20000 m across. The maximum is 10000 m.");

            BasisGlbTestBuilder far = BasisGlbTestBuilder.Triangle();
            far.Nodes[0] = "{\"mesh\":0,\"translation\":[200000,0,0]}";
            BasisGlbTestRun.AssertBothFail(far, BasisGlbErrorKind.OverLimit, "from its origin");
        }

        [Test]
        public void VertexSumNearInt32MaxIsRejected()
        {
            // 513 meshes reuse one 4,194,303-element POSITION accessor; vertices are counted per mesh (no cross-mesh
            // dedupe), so the sum passes 2^31. An int accumulator would wrap negative and slip under the limit.
            const int elements = 4194303;
            const int meshes = 513;
            var limits = BasisModelLimits.Desktop;
            limits.MaxModelBytes = 64L * 1024 * 1024;
            limits.MaxMeshes = 1024;
            limits.MaxPrimitives = 1024;
            limits.MaxMeshInstances = 1024;
            limits.MaxDrawCalls = 1024;
            limits.MaxVertices = int.MaxValue;
            limits.MaxTriangles = int.MaxValue;
            limits.MaxRenderedTriangles = long.MaxValue;
            limits.MaxSkinnedVertexInstances = long.MaxValue;
            var b = new BasisGlbTestBuilder();
            int position = b.AddAccessor(b.AddView(new byte[elements * 3]), BasisGltfComponent.UnsignedByte, elements, "VEC3");
            for (int m = 0; m < meshes; m++)
            {
                b.AddMesh(position);
                b.AddNode("{\"mesh\":" + m + "}");
            }
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b.BuildGlb(), limits);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.OverLimit, "Vertex count is 2,151,677,439. The maximum is 2,147,483,647.");
        }

        private struct LimitCase
        {
            public string Name;
            public Func<BasisGlbTestBuilder> Model;
            public Action<BasisModelLimits[]> Lower;
            public string[] Expected;
        }

        [Test]
        public void EachLimitRejectsWithValueAndLimit()
        {
            var cases = new List<LimitCase>
            {
                Case("nodes", () => BasisGlbTestBuilder.Hierarchy(3), l => l[0].MaxNodes = 2, "more than 2 nodes"),
                Case("depth", () => BasisGlbTestBuilder.Hierarchy(3), l => l[0].MaxNodeDepth = 2, "Node depth is 3", "The maximum is 2"),
                Case("meshes", TwoMeshes, l => l[0].MaxMeshes = 1, "more than 1 meshes"),
                Case("primitives", BasisGlbTestCorpus.TwoMaterialsTwoSamplers, l => l[0].MaxPrimitives = 1, "primitive count is 2", "The maximum is 1"),
                Case("materials", BasisGlbTestCorpus.TwoMaterialsTwoSamplers, l => l[0].MaxMaterials = 1, "material count is 2", "The maximum is 1"),
                Case("textures", BasisGlbTestCorpus.TwoMaterialsTwoSamplers, l => l[0].MaxTextures = 1, "texture count is 2", "The maximum is 1"),
                Case("samplers", BasisGlbTestCorpus.TwoMaterialsTwoSamplers, l => l[0].MaxSamplers = 1, "sampler count is 2", "The maximum is 1"),
                Case("images", TwoImages, l => l[0].MaxImages = 1, "image count is 2", "The maximum is 1"),
                Case("accessors", BasisGlbTestBuilder.IndexedQuad16, l => l[0].MaxAccessors = 3, "accessor count is 4", "The maximum is 3"),
                Case("elements", BasisGlbTestBuilder.IndexedQuad16, l => l[0].MaxAccessorElements = 5, "element count is 6", "The maximum is 5"),
                Case("joints", () => BasisGlbTestBuilder.SkinnedStrip(3), l => l[0].MaxJointsPerSkin = 2, "joint count is 3", "The maximum is 2"),
                Case("instances", TwoMeshes, l => l[0].MaxMeshInstances = 1, "Mesh instance count is 2", "The maximum is 1"),
                Case("draw calls", BasisGlbTestCorpus.TwoMaterialsTwoSamplers, l => l[0].MaxDrawCalls = 1, "Draw call count is 2", "The maximum is 1"),
                Case("vertices", BasisGlbTestBuilder.IndexedQuad16, l => l[0].MaxVertices = 3, "Vertex count is 4", "The maximum is 3"),
                Case("triangles", BasisGlbTestBuilder.IndexedQuad16, l => l[0].MaxTriangles = 1, "Triangle count is 2", "The maximum is 1"),
                Case("rendered", TwoMeshes, l => l[0].MaxRenderedTriangles = 1, "Rendered triangle count is 2", "The maximum is 1"),
                Case("skinned", () => BasisGlbTestBuilder.SkinnedStrip(2), l => l[0].MaxSkinnedVertexInstances = 5, "Skinned vertex instance count is 6", "The maximum is 5"),
                Case("texture pixels", () => BasisGlbTestBuilder.TexturedQuad(8, 4), l => l[0].MaxTotalTexturePixels = 31, "Total texture pixel count is 32", "The maximum is 31"),
                Case("texture size", () => BasisGlbTestBuilder.TexturedQuad(8, 4), l => l[0].MaxTextureDimension = 7, "Image is 8×4", "The maximum is 7×7"),
                Case("decoded", BasisGlbTestBuilder.Triangle, l => l[0].MaxEstimatedDecodedBytes = 263, "Estimated decoded memory is 264 bytes", "The maximum is 263 bytes"),
                Case("model bytes", BasisGlbTestBuilder.Triangle, l => { l[0].MaxModelBytes = 200; l[0].MaxJsonBytes = 200; l[0].MaxImageBytes = 200; }, "The maximum is 200 bytes"),
            };
            foreach (LimitCase c in cases)
            {
                var holder = new[] { BasisModelLimits.Desktop };
                c.Lower(holder);
                Assert.That(holder[0].TryValidate(out string invalid), Is.True, c.Name + ": " + invalid);
                byte[] glb = c.Model().BuildGlb();
                BasisGlbValidationResult result = BasisGlbTestRun.Receive(glb, holder[0]);
                Assert.That(result.Ok, Is.False, c.Name);
                Assert.That(result.ErrorKind, Is.EqualTo(BasisGlbErrorKind.OverLimit), c.Name + ": " + result.Error);
                foreach (string expected in c.Expected) StringAssert.Contains(expected, result.Error, c.Name);
                BasisGlbTestRun.AssertOk(BasisGlbTestRun.Receive(glb));
            }
        }

        private static LimitCase Case(string name, Func<BasisGlbTestBuilder> model, Action<BasisModelLimits[]> lower, params string[] expected)
        {
            return new LimitCase { Name = name, Model = model, Lower = lower, Expected = expected };
        }

        private static BasisGlbTestBuilder TwoMeshes()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.AddNode("{\"mesh\":" + b.AddMesh(0) + ",\"translation\":[2,0,0]}");
            return b;
        }

        private static BasisGlbTestBuilder TwoImages()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            int image = b.AddImage(BasisGlbTestPng.Create(2, 2));
            int texture = b.AddTexture(image);
            b.Materials[0] = "{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}},\"emissiveTexture\":{\"index\":" + texture + "}}";
            return b;
        }

        private static BasisGlbStats Ok(BasisGlbTestBuilder builder)
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(builder);
            BasisGlbTestRun.AssertOk(result);
            BasisGlbValidationResult sent = BasisGlbTestRun.Send(builder);
            BasisGlbTestRun.AssertOk(sent);
            Assert.That(BasisGlbStats.AreEqual(result.Stats, sent.Stats), Is.True, "sender and receiver agree");
            return result.Stats;
        }

        private static void AssertBounds(BasisGlbAabb bounds, float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            Assert.That(bounds.MinX, Is.EqualTo(minX).Within(1e-6f), "MinX");
            Assert.That(bounds.MinY, Is.EqualTo(minY).Within(1e-6f), "MinY");
            Assert.That(bounds.MinZ, Is.EqualTo(minZ).Within(1e-6f), "MinZ");
            Assert.That(bounds.MaxX, Is.EqualTo(maxX).Within(1e-6f), "MaxX");
            Assert.That(bounds.MaxY, Is.EqualTo(maxY).Within(1e-6f), "MaxY");
            Assert.That(bounds.MaxZ, Is.EqualTo(maxZ).Within(1e-6f), "MaxZ");
        }
    }
}
