using System;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfNodeGraphTests
    {
        [Test]
        public void RejectsChildOutOfRange()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"children\":[5]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "nodes[0].children[0] is out of range");
        }

        [Test]
        public void RejectsDuplicateChild()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"children\":[1,1]}";
            b.AddNode("{}", false);
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "lists nodes[1] twice");
        }

        [Test]
        public void RejectsNodeWithTwoParents()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"children\":[2]}";
            b.AddNode("{\"children\":[2]}");
            b.AddNode("{}", false);
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "nodes[2] is a child of both nodes[0] and nodes[1].");
        }

        [Test]
        public void RejectsSceneRootWithParentAndDuplicateRoots()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"children\":[1]}";
            b.AddNode("{}");
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "scene roots must have no parent");
            BasisGlbTestBuilder twice = BasisGlbTestBuilder.Triangle();
            twice.SceneRoots.Add(0);
            BasisGlbTestRun.AssertBothFail(twice, BasisGlbErrorKind.Malformed, "lists nodes[0] twice");
            BasisGlbTestBuilder outside = BasisGlbTestBuilder.Triangle();
            outside.SceneRoots.Add(9);
            BasisGlbTestRun.AssertBothFail(outside, BasisGlbErrorKind.Malformed, "scenes[0].nodes[1] is out of range");
        }

        [Test]
        public void DropsUnreachableCycles()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.AddNode("{\"children\":[2],\"mesh\":0}", false);
            b.AddNode("{\"children\":[1]}", false);
            b.AddNode("{\"children\":[3]}", false); // self-loop via 3 → 3
            b.Nodes[3] = "{\"children\":[3]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Nodes, Is.EqualTo(1));
            Assert.That(result.Stripped & BasisGlbStripped.UnreferencedContent, Is.EqualTo(BasisGlbStripped.UnreferencedContent));
        }

        [Test]
        public void RejectsDepthAboveLimit()
        {
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Send(BasisGlbTestBuilder.Hierarchy(128)));
            BasisGlbValidationResult deep = BasisGlbTestRun.Receive(BasisGlbTestBuilder.Hierarchy(129));
            BasisGlbTestRun.AssertFails(deep, BasisGlbErrorKind.OverLimit, "Node depth is 129. The maximum is 128.");
        }

        [Test]
        public void DeepChainDoesNotRecurse()
        {
            var limits = BasisModelLimits.Desktop;
            limits.MaxNodes = 65535;
            limits.MaxNodeDepth = 70000;
            limits.MaxJsonTokens = 2000000;
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Hierarchy(1);
            b.Nodes.Clear();
            b.SceneRoots.Clear();
            const int depth = 60000;
            for (int i = 0; i < depth; i++)
            {
                b.AddNode(i == depth - 1 ? "{\"mesh\":0}" : "{\"children\":[" + (i + 1) + "]}", i == 0);
            }
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b.BuildGlb(), limits);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Nodes, Is.EqualTo(depth));
        }

        [Test]
        public void RejectsMissingOrOutOfRangeScene()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.SceneIndex = "3";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "scene 3 is out of range");
            BasisGlbTestBuilder none = BasisGlbTestBuilder.Triangle();
            none.SceneIndex = null;
            none.Scenes = new System.Collections.Generic.List<string>();
            BasisGlbTestRun.AssertBothFail(none, BasisGlbErrorKind.Malformed, "no scene to display");
            BasisGlbTestBuilder empty = BasisGlbTestBuilder.Triangle();
            empty.Scenes = new System.Collections.Generic.List<string> { "{}" };
            BasisGlbTestRun.AssertBothFail(empty, BasisGlbErrorKind.Malformed, "no scene to display");
        }

        [Test]
        public void UsesSceneZeroWhenSceneAbsent()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.SceneIndex = null;
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertOk(result);
            StringAssert.Contains("\"scene\":0", BasisGlbTestRun.JsonOf(result.CleanGlb));
        }

        [Test]
        public void DropsOtherScenesAndOutsideNodes()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.AddNode("{\"mesh\":0,\"translation\":[5,0,0]}", false);
            b.SceneIndex = "1";
            b.Scenes = new System.Collections.Generic.List<string> { "{\"nodes\":[0]}", "{\"nodes\":[1]}" };
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.ExtraScenes, Is.EqualTo(BasisGlbStripped.ExtraScenes));
            Assert.That(result.Stats.Nodes, Is.EqualTo(1));
            Assert.That(result.Stats.Bounds.MinX, Is.EqualTo(5f));
        }

        [Test]
        public void RejectsMatrixWithTrs()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],\"scale\":[1,1,1]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "both a matrix and translation");
        }

        [Test]
        public void RejectsNonFiniteTransforms()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"translation\":[1e39,0,0]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "translation must be exactly 3 finite numbers");
        }

        [Test]
        public void RejectsZeroQuaternion()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"rotation\":[0,0,0,0]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "zero quaternion");
        }

        [TestCase("[1,0,0,0,0,1,0,0,0,0,1,0.5,0,0,0,1]")]
        [TestCase("[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,2]")]
        [TestCase("[0,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]")]
        public void RejectsDegenerateOrProjectiveMatrix(string matrix)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"matrix\":" + matrix + "}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "matrix is not a valid transform");
        }

        [Test]
        public void ConvertsMatrixToEquivalentTrs()
        {
            // T(1,2,3) · R(90° about Y) · S(2,3,-4): a negative determinant, so rotation and scale both flip.
            float c = 0f, s = 1f;
            float[] m =
            {
                c * 2, 0, -s * 2, 0,
                0, 3, 0, 0,
                s * -4, 0, c * -4, 0,
                1, 2, 3, 1,
            };
            var json = new StringBuilder("[");
            for (int i = 0; i < 16; i++) json.Append(i > 0 ? "," : "").Append(BasisGlbTestBuilder.F(m[i]));
            json.Append(']');
            BasisGlbTestBuilder viaMatrix = BasisGlbTestBuilder.Triangle();
            viaMatrix.Nodes[0] = "{\"mesh\":0,\"matrix\":" + json + "}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(viaMatrix);
            BasisGlbTestRun.AssertOk(result);
            string canonical = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(canonical, Does.Not.Contain("matrix"));
            StringAssert.Contains("\"translation\":[1,2,3]", canonical);

            // World-space vertices of the triangle under the matrix: (0,0,0)→(1,2,3), (1,0,0)→(1,2,1), (0,1,0)→(1,5,3).
            BasisGlbAabb bounds = result.Stats.Bounds;
            Assert.That(bounds.MinX, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(bounds.MaxX, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(bounds.MinY, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(bounds.MaxY, Is.EqualTo(5f).Within(1e-5f));
            Assert.That(bounds.MinZ, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(bounds.MaxZ, Is.EqualTo(3f).Within(1e-5f));
        }

        [Test]
        public void NormalizesRotationOnlyOutsideTolerance()
        {
            BasisGlbTestBuilder exact = BasisGlbTestBuilder.Triangle();
            exact.Nodes[0] = "{\"mesh\":0,\"rotation\":[0,0.70710677,0,0.70710677]}";
            StringAssert.Contains("\"rotation\":[0,0.707106769,0,0.707106769]", BasisGlbTestRun.JsonOf(BasisGlbTestRun.Send(exact).CleanGlb));

            BasisGlbTestBuilder scaled = BasisGlbTestBuilder.Triangle();
            scaled.Nodes[0] = "{\"mesh\":0,\"rotation\":[0,0,0,2]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(scaled);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(BasisGlbTestRun.JsonOf(result.CleanGlb), Does.Not.Contain("rotation"), "renormalised to identity and omitted");
        }

        [Test]
        public void RejectsWorldMatrixAboveLimit()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Hierarchy(3);
            for (int i = 0; i < 3; i++) b.Nodes[i] = b.Nodes[i].Replace("\"translation\":[1,0,0]", "\"scale\":[1000000,1,1]");
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.OverLimit, "world transform");
        }

        [Test]
        public void ScaleProductOverflowIsRejectedNotPropagated()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Hierarchy(128);
            for (int i = 0; i < 128; i++) b.Nodes[i] = b.Nodes[i].Replace("\"translation\":[1,0,0]", "\"scale\":[1000000,1000000,1000000]");
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.OverLimit, "world transform");
        }

        [Test]
        public void RejectsTranslationAndScaleAboveLimits()
        {
            BasisGlbTestBuilder far = BasisGlbTestBuilder.Triangle();
            far.Nodes[0] = "{\"mesh\":0,\"translation\":[20000000,0,0]}";
            BasisGlbTestRun.AssertBothFail(far, BasisGlbErrorKind.OverLimit, "translation exceeds");
            BasisGlbTestBuilder big = BasisGlbTestBuilder.Triangle();
            big.Nodes[0] = "{\"mesh\":0,\"scale\":[1,-2000000,1]}";
            BasisGlbTestRun.AssertBothFail(big, BasisGlbErrorKind.OverLimit, "scale exceeds");
        }

        [Test]
        public void NodeOrderIsDepthFirstPreOrder()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            // Source order: 0 = mesh leaf, 1 = root (children 3, 0), 2 = second root, 3 = middle (child 4), 4 = leaf.
            b.Nodes.Clear();
            b.SceneRoots.Clear();
            b.AddNode("{\"mesh\":0}", false);
            b.AddNode("{\"children\":[3,0]}");
            b.AddNode("{\"translation\":[0,1,0]}");
            b.AddNode("{\"children\":[4]}", false);
            b.AddNode("{\"translation\":[0,0,1]}", false);
            BasisGlbValidationResult result = BasisGlbTestRun.Receive(b);
            BasisGlbTestRun.AssertOk(result);
            // Pre-order: 1, 3, 4, 0, 2 → canonical 0..4.
            StringAssert.Contains("\"scenes\":[{\"nodes\":[0,4]}],\"nodes\":[{\"children\":[1,3]},{\"children\":[2]},{\"translation\":[0,0,1]},{\"mesh\":0},{\"translation\":[0,1,0]}]",
                BasisGlbTestRun.JsonOf(result.CleanGlb));
        }

        [Test]
        public void RejectsSkinWithoutMeshAndOutOfRangeReferences()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":3}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "nodes[0].mesh is out of range");
            b = BasisGlbTestBuilder.SkinnedStrip(2);
            b.Nodes[1] = "{\"skin\":0,\"children\":[2]}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "has a skin but no mesh");
        }
    }
}
