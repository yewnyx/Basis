using System;
using System.Collections.Generic;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfSkinTests
    {
        // SkinnedStrip(n) layout: accessors 0 POSITION, 1 JOINTS_0 (u8), 2 WEIGHTS_0 (float), 3 indices, 4 IBM;
        // nodes 0 = skinned mesh node, 1..n = joint chain; skins[0] uses joints 1..n.

        private static BasisGlbTestBuilder WithWeights(int joints, float[] weights)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(joints);
            int accessor = b.AddFloats("VEC4", weights);
            b.Meshes[0] = b.Meshes[0].Replace("\"WEIGHTS_0\":2", "\"WEIGHTS_0\":" + accessor);
            return b;
        }

        private static float[] UniformWeights(int vertices, float first, float second = 0f)
        {
            var values = new float[vertices * 4];
            for (int v = 0; v < vertices; v++)
            {
                values[v * 4] = first;
                values[v * 4 + 1] = second;
            }
            return values;
        }

        [Test]
        public void AcceptsSkinnedStrip()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.SkinnedStrip(3));
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.HasSkins, Is.True);
            Assert.That(result.Stats.Skins, Is.EqualTo(1));
            Assert.That(result.Stats.Joints, Is.EqualTo(3));
            Assert.That(result.Stats.SkinnedMeshInstances, Is.EqualTo(1));
            Assert.That(result.Stats.SkinnedVertexInstances, Is.EqualTo(8));
            StringAssert.Contains("\"skins\":[{\"inverseBindMatrices\":4,\"skeleton\":1,\"joints\":[1,2,3]}]", BasisGlbTestRun.JsonOf(result.CleanGlb));
        }

        [Test]
        public void RejectsJointOutOfRangeOrDuplicate()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.Skins[0] = "{\"joints\":[1,9]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "skins[0].joints[1] is out of range");
            b.Skins[0] = "{\"joints\":[1,1]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "lists nodes[1] twice");
            b.Skins[0] = "{\"joints\":[]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "joints is empty");
        }

        [Test]
        public void RejectsJointOutsideDefaultScene()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.AddNode("{}", false);
            b.Skins[0] = "{\"joints\":[1,3]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "is not part of the displayed scene");
        }

        [Test]
        public void RejectsJointValueAtOrPastJointCountEvenWithZeroWeight()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            var joints = new byte[6 * 4];
            joints[3] = 2; // vertex 0, fourth influence (weight 0) points at joint 2 of a 2-joint skin
            int accessor = b.AddAccessor(b.AddView(joints), BasisGltfComponent.UnsignedByte, 6, "VEC4");
            b.Meshes[0] = b.Meshes[0].Replace("\"JOINTS_0\":1", "\"JOINTS_0\":" + accessor);
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "uses joint 2, but skins[0] has 2 joints");
        }

        [TestCase(-0.5f, 1.5f, "vertex 0 has a negative or non-finite weight")]
        [TestCase(float.NaN, 1f, "accessors[5] has a non-finite value at element 0")]
        [TestCase(0f, 0f, "vertex 0 sum to 0; skin weights must sum to 1 (±0.01).")]
        [TestCase(0.9f, 0f, "vertex 0 sum to 0.9;")]
        [TestCase(1.2f, 0f, "vertex 0 sum to 1.2;")]
        public void RejectsBadWeights(float first, float second, string message)
        {
            BasisGlbTestBuilder b = WithWeights(2, UniformWeights(6, first, second));
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, message);
        }

        [TestCase(0.995f)]
        [TestCase(1.005f)]
        public void AcceptsWeightSumWithinTolerance(float sum)
        {
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Receive(WithWeights(2, UniformWeights(6, sum))));
        }

        [Test]
        public void AcceptsNormalisedByteWeights()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            var weights = new byte[6 * 4];
            for (int v = 0; v < 6; v++)
            {
                weights[v * 4] = 128;
                weights[v * 4 + 1] = 127;
            }
            int accessor = b.AddAccessor(b.AddView(weights), BasisGltfComponent.UnsignedByte, 6, "VEC4", true);
            b.Meshes[0] = b.Meshes[0].Replace("\"WEIGHTS_0\":2", "\"WEIGHTS_0\":" + accessor);
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Receive(b));
        }

        [TestCase("VEC4")]
        [TestCase("short")]
        [TestCase("count")]
        public void RejectsBadInverseBindMatrices(string problem)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            if (problem == "VEC4") b.Accessors[4] = "{\"bufferView\":4,\"componentType\":5126,\"count\":8,\"type\":\"VEC4\"}";
            else if (problem == "short") b.Accessors[4] = "{\"bufferView\":4,\"componentType\":5122,\"count\":2,\"type\":\"MAT4\"}";
            else b.Accessors[4] = "{\"bufferView\":4,\"componentType\":5126,\"count\":1,\"type\":\"MAT4\"}";
            BasisGlbTestRun.AssertBothFail(b, problem == "count" ? BasisGlbErrorKind.Malformed : BasisGlbErrorKind.Unsupported, "inverseBindMatrices");
        }

        [Test]
        public void TrimsInverseBindMatricesToJointCount()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            var ibm = new List<float>();
            for (int j = 0; j < 3; j++) ibm.AddRange(new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -0.5f * j, 0f, 1f });
            int accessor = b.AddFloats("MAT4", ibm.ToArray());
            b.Skins[0] = b.Skins[0].Replace("\"inverseBindMatrices\":4", "\"inverseBindMatrices\":" + accessor);
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            StringAssert.Contains("\"componentType\":5126,\"count\":2,\"type\":\"MAT4\"", BasisGlbTestRun.JsonOf(result.CleanGlb));
        }

        [Test]
        public void DropsInvalidSkeletonKeepsValid()
        {
            BasisGlbTestBuilder valid = BasisGlbTestBuilder.SkinnedStrip(2);
            StringAssert.Contains("\"skeleton\":1", BasisGlbTestRun.JsonOf(BasisGlbTestRun.Send(valid).CleanGlb));
            BasisGlbTestBuilder notAncestor = BasisGlbTestBuilder.SkinnedStrip(2);
            notAncestor.Skins[0] = notAncestor.Skins[0].Replace("\"skeleton\":1", "\"skeleton\":2");
            BasisGlbValidationResult result = BasisGlbTestRun.Send(notAncestor);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(BasisGlbTestRun.JsonOf(result.CleanGlb), Does.Not.Contain("skeleton"));
            BasisGlbTestBuilder outOfRange = BasisGlbTestBuilder.SkinnedStrip(2);
            outOfRange.Skins[0] = outOfRange.Skins[0].Replace("\"skeleton\":1", "\"skeleton\":40");
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Send(outOfRange));
        }

        [Test]
        public void RejectsMeshSkinnedByTwoSkins()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.Skins.Add("{\"joints\":[2,1]}");
            b.AddNode("{\"mesh\":0,\"skin\":1}");
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Unsupported, "is skinned by both skins[0] and skins[1]");
        }

        [Test]
        public void StripsJointsWeightsWithoutSkinnedInstance()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.Nodes[0] = "{\"mesh\":0}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.ExtraVertexStreams, Is.EqualTo(BasisGlbStripped.ExtraVertexStreams));
            Assert.That(result.Stats.HasSkins, Is.False);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("JOINTS_0"));
            Assert.That(json, Does.Not.Contain("skins"));
        }

        [Test]
        public void DropsSkinOfNodeWhoseMeshHasNoJoints()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"skin\":0}";
            b.AddNode("{}");
            b.Skins.Add("{\"joints\":[1]}");
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.UnusedSkins, Is.EqualTo(BasisGlbStripped.UnusedSkins));
            Assert.That(BasisGlbTestRun.JsonOf(result.CleanGlb), Does.Not.Contain("skin"));
        }

        [Test]
        public void SkinnedBoundsCoverEveryInfluenceTransform()
        {
            // Two joints; joint 1 is posed 2 m along +X (bind pose was at y = 0.5). Every vertex is half on joint 0 and
            // half on joint 1, so the hull spans both transforms: x in [0, 1] ∪ [2, 3], y from the bind and posed rows.
            BasisGlbTestBuilder b = WithWeights(2, UniformWeights(6, 0.5f, 0.5f));
            var joints = new byte[6 * 4];
            for (int v = 0; v < 6; v++)
            {
                joints[v * 4] = 0;
                joints[v * 4 + 1] = 1;
            }
            int jointsAccessor = b.AddAccessor(b.AddView(joints), BasisGltfComponent.UnsignedByte, 6, "VEC4");
            b.Meshes[0] = b.Meshes[0].Replace("\"JOINTS_0\":1", "\"JOINTS_0\":" + jointsAccessor);
            b.Nodes[2] = "{\"translation\":[2,0.5,0]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertOk(result);
            // Vertices: x ∈ {0,1}, y ∈ {0, 0.5, 1}. Joint 0: identity. Joint 1: world T(2,0.5,0) · IBM T(0,-0.5,0) = T(2,0,0).
            BasisGlbAabb bounds = result.Stats.Bounds;
            Assert.That(bounds.MinX, Is.EqualTo(0f));
            Assert.That(bounds.MaxX, Is.EqualTo(3f));
            Assert.That(bounds.MinY, Is.EqualTo(0f));
            Assert.That(bounds.MaxY, Is.EqualTo(1f));
            Assert.That(bounds.MinZ, Is.EqualTo(0f));
            Assert.That(bounds.MaxZ, Is.EqualTo(0f));
        }

        [Test]
        public void SkinnedMeshIgnoresItsOwnNodeTransform()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.Nodes[0] = "{\"mesh\":0,\"skin\":0,\"translation\":[100,0,0]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Bounds.MaxX, Is.EqualTo(1f));
        }

        [Test]
        public void RejectsTooManyJoints()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.SkinnedStrip(2);
            var joints = new System.Text.StringBuilder();
            for (int i = 0; i < 257; i++)
            {
                b.AddNode("{}", true);
                joints.Append(i > 0 ? "," : "").Append(b.Nodes.Count - 1);
            }
            b.Skins[0] = "{\"joints\":[" + joints + "]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.OverLimit, "joint count is 257. The maximum is 256.");
        }
    }
}
