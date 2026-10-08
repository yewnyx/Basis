using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfExtensionTests
    {
        [TestCase("KHR_draco_mesh_compression")]
        [TestCase("EXT_meshopt_compression")]
        [TestCase("KHR_texture_basisu")]
        [TestCase("EXT_mesh_gpu_instancing")]
        [TestCase("KHR_lights_punctual")]
        [TestCase("KHR_materials_variants")]
        [TestCase("EXT_unknown")]
        public void RejectsRequiredOutsideAllowList(string extension)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.ExtensionsUsed = "[\"" + extension + "\"]";
            b.ExtensionsRequired = "[\"" + extension + "\"]";
            byte[] glb = b.BuildGlb();
            BasisGlbValidationResult sent = BasisGlbTestRun.Send(glb);
            BasisGlbTestRun.AssertFails(sent, BasisGlbErrorKind.Unsupported, "The model requires the glTF extension \"" + extension + "\", which Basis does not support.");
            BasisGlbValidationResult received = BasisGlbTestRun.Receive(glb);
            BasisGlbTestRun.AssertFails(received, BasisGlbErrorKind.Unsupported, "extensionsRequired[0]");
            Assert.That(received.Error, Does.Not.Contain(extension));
        }

        [Test]
        public void RejectsRequiredMissingFromUsed()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.ExtensionsRequired = "[\"KHR_materials_unlit\"]";
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Send(b), BasisGlbErrorKind.Malformed,
                "extensionsRequired lists \"KHR_materials_unlit\" which is missing from extensionsUsed.");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(b), BasisGlbErrorKind.Malformed, "extensionsRequired[0] is missing from extensionsUsed.");
        }

        [Test]
        public void RejectsBadExtensionLists()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            b.ExtensionsUsed = "[\"A\",\"A\"]";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "repeats an earlier entry");
            b.ExtensionsUsed = "[\"" + new string('E', 65) + "\"]";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.OverLimit, "longer than 64 bytes");
            b.ExtensionsUsed = "[1]";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, "must be a string");
        }

        [Test]
        public void StripsOptionalExtensionsWithFlags()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.ExtensionsUsed = "[\"KHR_lights_punctual\",\"EXT_mesh_gpu_instancing\",\"KHR_materials_variants\",\"KHR_draco_mesh_compression\","
                + "\"KHR_materials_clearcoat\",\"EXT_whatever\",\"KHR_texture_basisu\"]";
            b.ExtraRootMembers = "\"extensions\":{\"KHR_lights_punctual\":{\"lights\":[{\"type\":\"point\"}]},\"KHR_materials_variants\":{\"variants\":[]}}";
            b.Nodes[0] = "{\"mesh\":0,\"extensions\":{\"KHR_lights_punctual\":{\"light\":0},\"EXT_mesh_gpu_instancing\":{\"attributes\":{}}}}";
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0,"
                + "\"extensions\":{\"KHR_draco_mesh_compression\":{\"bufferView\":0,\"attributes\":{}}}}]}";
            b.Materials[0] = "{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}},\"extensions\":{\"KHR_materials_clearcoat\":{\"clearcoatFactor\":1}}}";
            b.Textures[0] = "{\"source\":0,\"extensions\":{\"KHR_texture_basisu\":{\"source\":0},\"EXT_whatever\":{}}}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            BasisGlbStripped expected = BasisGlbStripped.Lights | BasisGlbStripped.Instancing | BasisGlbStripped.MaterialVariants
                | BasisGlbStripped.CompressionFallbackUsed | BasisGlbStripped.MaterialExtensions | BasisGlbStripped.OtherExtensions;
            Assert.That(result.Stripped & expected, Is.EqualTo(expected));
            Assert.That(BasisGlbTestRun.JsonOf(result.CleanGlb), Does.Not.Contain("extensions"));
        }

        [Test]
        public void KeepsUnlitTransformQuantization()
        {
            BasisGlbTestBuilder b = BasisGlbTestCorpus.UnlitTransformed();
            b.ExtensionsRequired = "[\"KHR_materials_unlit\"]";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"extensions\":{\"KHR_materials_unlit\":{}}", json);
            Assert.That(json, Does.Not.Contain("extensionsRequired"), "only quantization is ever required in the canonical form");
            BasisGlbTestRun.AssertOk(BasisGlbTestRun.Send(BasisGlbTestBuilder.Quantized()));
        }

        [Test]
        public void StripsEmissiveStrengthAfterValidating()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.ExtensionsUsed = "[\"KHR_materials_emissive_strength\"]";
            b.ExtensionsRequired = "[\"KHR_materials_emissive_strength\"]";
            b.Materials[0] = "{\"emissiveFactor\":[1,1,1],\"extensions\":{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":4}}}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.MaterialExtensions, Is.EqualTo(BasisGlbStripped.MaterialExtensions));
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("emissive_strength"));
            StringAssert.Contains("\"emissiveFactor\":[1,1,1]", json);
        }

        [Test]
        public void StripsLightsCamerasInstancing()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.AnimatedMorphed();
            b.Nodes[0] = "{\"mesh\":0,\"camera\":0,\"extensions\":{\"KHR_lights_punctual\":{\"light\":0},\"EXT_mesh_gpu_instancing\":{\"attributes\":{\"TRANSLATION\":0}}}}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            foreach (string key in new[] { "camera", "KHR_lights_punctual", "EXT_mesh_gpu_instancing", "TRANSLATION", "animations" })
            {
                Assert.That(json, Does.Not.Contain(key));
            }
            BasisGlbStripped expected = BasisGlbStripped.Cameras | BasisGlbStripped.Lights | BasisGlbStripped.Instancing;
            Assert.That(result.Stripped & expected, Is.EqualTo(expected));
        }

        [Test]
        public void UndeclaredUnlitIsStillHonoured()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Materials[0] = "{\"extensions\":{\"KHR_materials_unlit\":{\"ignored\":true}}}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.UsesUnlit, Is.True);
            StringAssert.Contains("\"extensionsUsed\":[\"KHR_materials_unlit\"]", BasisGlbTestRun.JsonOf(result.CleanGlb));
        }
    }
}
