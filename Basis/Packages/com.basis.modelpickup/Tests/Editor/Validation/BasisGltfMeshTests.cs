using System;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfMeshTests
    {
        private static BasisGlbTestBuilder TriangleWith(string attributeName, int componentType, string type, bool normalized, int count = 3)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            int components = type == "VEC2" ? 2 : type == "VEC3" ? 3 : 4;
            int size = componentType == 5126 || componentType == 5125 ? 4 : componentType == 5122 || componentType == 5123 ? 2 : 1;
            int element = (components * size + 3) & ~3;
            int view = b.AddView(new byte[element * count], element == components * size ? (int?)null : element);
            int accessor = b.AddAccessor(view, componentType, count, type, normalized);
            if (attributeName == "WEIGHTS_0" || attributeName == "JOINTS_0")
            {
                throw new ArgumentException("use the skin tests for joints and weights");
            }
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"" + attributeName + "\":" + accessor + "}}]}";
            return b;
        }

        [Test]
        public void RejectsPrimitiveWithoutPosition()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{}}]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "has no POSITION");
        }

        [TestCase("NORMAL", 5126, "VEC2", false)]
        [TestCase("NORMAL", 5121, "VEC3", true)]
        [TestCase("NORMAL", 5122, "VEC3", false)]
        [TestCase("TANGENT", 5126, "VEC3", false)]
        [TestCase("TANGENT", 5121, "VEC4", true)]
        [TestCase("TEXCOORD_0", 5126, "VEC3", false)]
        [TestCase("TEXCOORD_0", 5125, "VEC2", false)]
        [TestCase("COLOR_0", 5121, "VEC4", false)]
        [TestCase("COLOR_0", 5120, "VEC4", true)]
        [TestCase("COLOR_0", 5126, "VEC2", false)]
        public void RejectsAttributeOutsideTable(string attribute, int componentType, string type, bool normalized)
        {
            BasisGlbTestRun.AssertBothFail(TriangleWith(attribute, componentType, type, normalized), BasisGlbErrorKind.Unsupported, attribute);
        }

        [TestCase("NORMAL", 5120, "VEC3", true)]
        [TestCase("NORMAL", 5122, "VEC3", true)]
        [TestCase("TANGENT", 5122, "VEC4", true)]
        [TestCase("TEXCOORD_0", 5121, "VEC2", true)]
        [TestCase("TEXCOORD_0", 5123, "VEC2", false)]
        [TestCase("TEXCOORD_0", 5120, "VEC2", false)]
        [TestCase("COLOR_0", 5123, "VEC3", true)]
        [TestCase("COLOR_0", 5126, "VEC4", false)]
        public void AcceptsAttributesInTable(string attribute, int componentType, string type, bool normalized)
        {
            BasisGlbTestBuilder b = TriangleWith(attribute, componentType, type, normalized);
            if (attribute == "NORMAL" || attribute == "TANGENT")
            {
                // Zero data is invalid only for nothing here: normals and tangents are not range-checked, only finite.
            }
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Send(b));
        }

        [Test]
        public void AcceptsQuantizedAttributesAndDeclaresExtension()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.Quantized());
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.UsesQuantization, Is.True);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"extensionsUsed\":[\"KHR_mesh_quantization\"]", json);
            StringAssert.Contains("\"extensionsRequired\":[\"KHR_mesh_quantization\"]", json);
            BasisGlbValidationResult plain = BasisGlbTestRun.Send(BasisGlbTestBuilder.Triangle());
            Assert.That(BasisGlbTestRun.JsonOf(plain.CleanGlb), Does.Not.Contain("extensions"));
        }

        [TestCase("NORMAL", 5126, "VEC3")]
        [TestCase("TEXCOORD_0", 5126, "VEC2")]
        [TestCase("COLOR_0", 5126, "VEC4")]
        public void RejectsAttributeCountDifferentFromPosition(string attribute, int componentType, string type)
        {
            BasisGlbTestRun.AssertBothFail(TriangleWith(attribute, componentType, type, false, 4), BasisGlbErrorKind.Malformed,
                attribute + " has 4 elements but POSITION has 3.");
        }

        [Test]
        public void RejectsJointsCountDifferentFromPosition()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.Accessors[1] = "{\"bufferView\":1,\"componentType\":5121,\"count\":5,\"type\":\"VEC4\"}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "JOINTS_0 has 5 elements but POSITION has 6.");
        }

        [TestCase(8)]
        [TestCase(16)]
        [TestCase(32)]
        public void RejectsIndexAtOrPastVertexCount(int bits)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            int indices = bits == 8 ? b.AddIndices8(0, 1, 3) : bits == 16 ? b.AddIndices16(0, 1, 3) : b.AddIndices32(0, 1, 3);
            b.Meshes[0] = "{\"primitives\":[" + BasisGlbTestBuilder.Primitive(0, indices) + "]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "meshes[0].primitives[0] index 3 at position 2 is not below the vertex count 3.");
        }

        [Test]
        public void RejectsIndexCountNotMultipleOfThree()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            int indices = b.AddIndices16(0, 1, 2, 0);
            b.Meshes[0] = "{\"primitives\":[" + BasisGlbTestBuilder.Primitive(0, indices) + "]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "has 4 indices; triangles need a multiple of 3");
        }

        [Test]
        public void RejectsNonIndexedCountNotMultipleOfThree()
        {
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0);
            b.AddNode("{\"mesh\":" + b.AddMesh(position) + "}");
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "has 4 vertices and no indices");
        }

        [Test]
        public void StripsNonTrianglePrimitivesAndEmptyMeshes()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":1},{\"attributes\":{\"POSITION\":0}}]}";
            b.Meshes.Add("{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":0}]}");
            b.AddNode("{\"mesh\":1}");
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.NonTrianglePrimitives, Is.EqualTo(BasisGlbStripped.NonTrianglePrimitives));
            Assert.That(result.Stats.Primitives, Is.EqualTo(1));
            Assert.That(result.Stats.Meshes, Is.EqualTo(1));
            Assert.That(result.Stats.MeshInstances, Is.EqualTo(1));
            Assert.That(result.Stats.Nodes, Is.EqualTo(2));
            StringAssert.Contains("\"nodes\":[{\"mesh\":0},{}]", BasisGlbTestRun.JsonOf(result.CleanGlb));
        }

        [Test]
        public void RejectsInvalidPrimitiveMode()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":7}]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "mode must be 0 to 6");
        }

        [Test]
        public void RejectsModelWithNoTriangles()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":0}]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "no triangles to display");
            BasisGlbTestBuilder noMesh = BasisGlbTestBuilder.Triangle();
            noMesh.Nodes[0] = "{}";
            BasisGlbTestRun.AssertBothFail(noMesh, BasisGlbErrorKind.Malformed, "no triangles to display");
        }

        [Test]
        public void StripsMorphTargetsAndWeights()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.AnimatedMorphed());
            BasisGlbTestRun.AssertOk(result);
            BasisGlbStripped expected = BasisGlbStripped.MorphTargets | BasisGlbStripped.Animations | BasisGlbStripped.Cameras | BasisGlbStripped.Extras;
            Assert.That(result.Stripped & expected, Is.EqualTo(expected));
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("targets"));
            Assert.That(json, Does.Not.Contain("weights"));
            Assert.That(result.Stats.Accessors, Is.EqualTo(1));
        }

        [Test]
        public void StripsTexcoordAboveOneAndCustomAttributes()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.IndexedQuad16();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2,\"TEXCOORD_2\":2,\"_CUSTOM\":2,\"COLOR_1\":1},\"indices\":3}]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            BasisGlbStripped expected = BasisGlbStripped.ExtraUvSets | BasisGlbStripped.ExtraVertexStreams;
            Assert.That(result.Stripped & expected, Is.EqualTo(expected));
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("TEXCOORD_2"));
            Assert.That(json, Does.Not.Contain("_CUSTOM"));
            Assert.That(json, Does.Not.Contain("COLOR_1"));
        }

        [Test]
        public void DropsTexcoordOneWithoutTexcoordZero()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.IndexedQuad16();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_1\":2},\"indices\":3}]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.ExtraUvSets, Is.EqualTo(BasisGlbStripped.ExtraUvSets));
            Assert.That(BasisGlbTestRun.JsonOf(result.CleanGlb), Does.Not.Contain("TEXCOORD"));
        }

        [TestCase("POSITION", float.NaN)]
        [TestCase("POSITION", float.PositiveInfinity)]
        [TestCase("NORMAL", float.NegativeInfinity)]
        [TestCase("TANGENT", float.NaN)]
        [TestCase("TEXCOORD_0", float.PositiveInfinity)]
        [TestCase("COLOR_0", float.NaN)]
        public void RejectsNonFiniteVertexData(string attribute, float bad)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            if (attribute == "POSITION")
            {
                b.AddFloats("VEC3", 0, 0, 0, bad, 0, 0, 0, 1, 0);
                b.Accessors[1] = b.Accessors[1].Substring(0, b.Accessors[1].IndexOf(",\"min\"", StringComparison.Ordinal)) + "}";
                b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":1}}]}";
            }
            else
            {
                string type = attribute == "TEXCOORD_0" ? "VEC2" : attribute == "NORMAL" ? "VEC3" : "VEC4";
                int components = type == "VEC2" ? 2 : type == "VEC3" ? 3 : 4;
                var values = new float[components * 3];
                values[components] = bad;
                int accessor = b.AddFloats(type, values);
                if (type == "VEC3") b.Accessors[accessor] = b.Accessors[accessor].Substring(0, b.Accessors[accessor].IndexOf(",\"min\"", StringComparison.Ordinal)) + "}";
                b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"" + attribute + "\":" + accessor + "}}]}";
            }
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "non-finite");
        }

        [Test]
        public void RewritesPositionMinMaxFromData()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[-100,-100,-100],\"max\":[100,100,100]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            StringAssert.Contains("\"min\":[0,0,0],\"max\":[1,1,0]", BasisGlbTestRun.JsonOf(result.CleanGlb));
        }

        [Test]
        public void RejectsMaterialIndexOutOfRange()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"material\":0}]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "material is out of range");
        }

        [Test]
        public void RejectsAccessorUsedForIndicesAndVertices()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.IndexedQuad16();
            b.Meshes.Add("{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":0}]}");
            b.AddNode("{\"mesh\":1}");
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Unsupported, "indices");
        }

        [Test]
        public void RejectsNormalMappedMeshWithoutNormals()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Materials[0] = "{\"normalTexture\":{\"index\":0}}";
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0}]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Unsupported, "has no NORMAL but its material uses a normal texture");
        }
    }
}
