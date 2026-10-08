using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGltfMaterialTests
    {
        // TexturedQuad: accessors 0 POSITION, 1 NORMAL, 2 TEXCOORD_0, 3 indices; image 0 (view 4), texture 0, material 0.

        private static BasisGlbTestBuilder WithMaterial(string material)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Materials[0] = material;
            return b;
        }

        [Test]
        public void RejectsTextureSamplerOrSourceOutOfRange()
        {
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"emissiveTexture\":{\"index\":5}}"), BasisGlbErrorKind.Malformed, "emissiveTexture.index is out of range");
            BasisGlbTestBuilder sampler = BasisGlbTestBuilder.TexturedQuad(4, 4);
            sampler.Textures[0] = "{\"sampler\":2,\"source\":0}";
            BasisGlbTestRun.AssertBothFail(sampler, BasisGlbErrorKind.Malformed, "textures[0].sampler is out of range");
            BasisGlbTestBuilder source = BasisGlbTestBuilder.TexturedQuad(4, 4);
            source.Textures[0] = "{\"source\":3}";
            BasisGlbTestRun.AssertBothFail(source, BasisGlbErrorKind.Malformed, "textures[0].source is out of range");
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"emissiveTexture\":{\"texCoord\":0}}"), BasisGlbErrorKind.Malformed, "index is out of range");
        }

        [Test]
        public void RejectsTexCoordAboveOne()
        {
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0,\"texCoord\":2}}}"),
                BasisGlbErrorKind.Unsupported, "baseColorTexture uses TEXCOORD_2; only TEXCOORD_0 and TEXCOORD_1 are supported.");
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0,"
                + "\"extensions\":{\"KHR_texture_transform\":{\"texCoord\":3}}}}}"), BasisGlbErrorKind.Unsupported, "TEXCOORD_3");
        }

        [Test]
        public void ClampsFactorsAndRejectsNonFinite()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(WithMaterial(
                "{\"pbrMetallicRoughness\":{\"baseColorFactor\":[2,-1,0.5,1],\"metallicFactor\":3,\"roughnessFactor\":-2},"
                + "\"emissiveFactor\":[5,0,0],\"occlusionTexture\":{\"index\":0,\"strength\":7},\"alphaMode\":\"MASK\",\"alphaCutoff\":9}"));
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,0,0.5,1],\"roughnessFactor\":0}", json);
            StringAssert.Contains("\"occlusionTexture\":{\"index\":0}", json);
            StringAssert.Contains("\"emissiveFactor\":[1,0,0]", json);
            StringAssert.Contains("\"alphaMode\":\"MASK\",\"alphaCutoff\":1", json);
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"pbrMetallicRoughness\":{\"metallicFactor\":1e39}}"), BasisGlbErrorKind.Malformed, "finite number");
        }

        [TestCase("mask")]
        [TestCase("CUTOUT")]
        public void RejectsUnknownAlphaMode(string mode)
        {
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"alphaMode\":\"" + mode + "\"}"), BasisGlbErrorKind.Malformed, "alphaMode must be OPAQUE, MASK or BLEND");
        }

        [Test]
        public void DropsAlphaCutoffUnlessMask()
        {
            string blend = BasisGlbTestRun.JsonOf(BasisGlbTestRun.Send(WithMaterial("{\"alphaMode\":\"BLEND\",\"alphaCutoff\":0.2}")).CleanGlb);
            StringAssert.Contains("\"materials\":[{\"alphaMode\":\"BLEND\"}]", blend);
            string opaque = BasisGlbTestRun.JsonOf(BasisGlbTestRun.Send(WithMaterial("{\"alphaMode\":\"OPAQUE\",\"alphaCutoff\":0.2,\"doubleSided\":false}")).CleanGlb);
            StringAssert.Contains("\"materials\":[{}]", opaque);
            string mask = BasisGlbTestRun.JsonOf(BasisGlbTestRun.Send(WithMaterial("{\"alphaMode\":\"MASK\",\"alphaCutoff\":0.5}")).CleanGlb);
            StringAssert.Contains("\"materials\":[{\"alphaMode\":\"MASK\"}]", mask);
        }

        [Test]
        public void FoldsTransformTexCoordAndDropsIdentity()
        {
            BasisGlbTestBuilder b = WithMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0,\"texCoord\":0,"
                + "\"extensions\":{\"KHR_texture_transform\":{\"texCoord\":1,\"offset\":[0,0],\"scale\":[1,1],\"rotation\":0}}}}}");
            b.ExtensionsUsed = "[\"KHR_texture_transform\"]";
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2,\"TEXCOORD_1\":2},\"indices\":3,\"material\":0}]}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"baseColorTexture\":{\"index\":0,\"texCoord\":1}", json);
            Assert.That(json, Does.Not.Contain("KHR_texture_transform"));
            Assert.That(result.Stats.UsesTextureTransform, Is.False);
        }

        [Test]
        public void KeepsNonIdentityTransform()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestCorpus.UnlitTransformed());
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.25,0.5],\"rotation\":0.5,\"scale\":[2,2]}}", json);
            StringAssert.Contains("\"extensionsUsed\":[\"KHR_materials_unlit\",\"KHR_texture_transform\"]", json);
            Assert.That(result.Stats.UsesTextureTransform && result.Stats.UsesUnlit, Is.True);
        }

        [Test]
        public void RejectsTextureTransformWithWrongLengths()
        {
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0,"
                + "\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.5]}}}}}"), BasisGlbErrorKind.Malformed, "exactly 2 finite numbers");
        }

        [TestCase("{\"magFilter\":9984}", "magFilter")]
        [TestCase("{\"minFilter\":1}", "minFilter")]
        [TestCase("{\"wrapS\":0}", "wrap mode")]
        public void RejectsInvalidSamplerEnums(string sampler, string message)
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Samplers.Add(sampler);
            b.Textures[0] = "{\"sampler\":0,\"source\":0}";
            BasisGlbTestRun.AssertBothFail(b, BasisGlbErrorKind.Malformed, message);
        }

        [Test]
        public void DefaultSamplerBecomesNoneAndDuplicatesMerge()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Samplers.Add("{\"wrapS\":10497,\"wrapT\":10497}");
            b.Samplers.Add("{\"magFilter\":9728}");
            b.Samplers.Add("{\"magFilter\":9728}");
            b.Textures[0] = "{\"sampler\":0,\"source\":0}";
            b.AddTexture(0, 1);
            b.AddTexture(0, 2);
            b.Materials[0] = "{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":1}},"
                + "\"emissiveTexture\":{\"index\":2}}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"textures\":[{\"source\":0},{\"sampler\":0,\"source\":0}],\"samplers\":[{\"magFilter\":9728}]", json);
            StringAssert.Contains("\"emissiveTexture\":{\"index\":1}", json);
            Assert.That(result.Stats.Textures, Is.EqualTo(2));
            Assert.That(result.Stats.Samplers, Is.EqualTo(1));
            Assert.That(result.Stats.Images, Is.EqualTo(1));
        }

        [Test]
        public void RejectsImageWithBothOrNeitherSource()
        {
            BasisGlbTestBuilder both = BasisGlbTestBuilder.TexturedQuad(4, 4);
            both.Images[0] = "{\"bufferView\":4,\"mimeType\":\"image/png\",\"uri\":\"data:image/png;base64,AAAA\"}";
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Send(both), BasisGlbErrorKind.Malformed, "both a uri and a bufferView");
            BasisGlbTestBuilder neither = BasisGlbTestBuilder.TexturedQuad(4, 4);
            neither.Images[0] = "{\"mimeType\":\"image/png\"}";
            BasisGlbTestRun.AssertBothFail(neither, BasisGlbErrorKind.Malformed, "neither a uri nor a bufferView");
        }

        [Test]
        public void RejectsImageMimeAndSignatureProblems()
        {
            BasisGlbTestBuilder webpMime = BasisGlbTestBuilder.TexturedQuad(4, 4);
            webpMime.Images[0] = "{\"bufferView\":4,\"mimeType\":\"image/webp\"}";
            BasisGlbTestRun.AssertBothFail(webpMime, BasisGlbErrorKind.ImageRejected, "mimeType must be image/png or image/jpeg");

            BasisGlbTestBuilder jpegBytes = BasisGlbTestBuilder.IndexedQuad16();
            int jpegImage = jpegBytes.AddImage(BasisGlbTestPng.CreateJpegHeader(4, 4), "image/png");
            jpegBytes.AddTexture(jpegImage);
            jpegBytes.AddMaterial("{\"emissiveTexture\":{\"index\":0}}");
            jpegBytes.Meshes[0] = jpegBytes.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Send(jpegBytes), BasisGlbErrorKind.ImageRejected, "holds JPEG data but is labelled image/png");

            BasisGlbTestBuilder gif = BasisGlbTestBuilder.IndexedQuad16();
            gif.AddImage(BasisGlbTestPng.Gif(), "image/png");
            gif.AddTexture(0);
            gif.AddMaterial("{\"emissiveTexture\":{\"index\":0}}");
            gif.Meshes[0] = gif.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            BasisGlbTestRun.AssertBothFail(gif, BasisGlbErrorKind.ImageRejected, "images[0] is a GIF; glTF images must be PNG or JPEG.");

            BasisGlbTestBuilder noMime = BasisGlbTestBuilder.TexturedQuad(4, 4);
            noMime.Images[0] = "{\"bufferView\":4}";
            BasisGlbTestRun.AssertBothFail(noMime, BasisGlbErrorKind.Malformed, "must declare a mimeType");
        }

        [Test]
        public void SenderAcceptsJpegAndHandsItToTheSanitizer()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.IndexedQuad16();
            b.AddImage(BasisGlbTestPng.CreateJpegHeader(64, 32), "image/jpeg");
            b.AddTexture(0);
            b.AddMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}");
            b.Meshes[0] = b.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            var sanitizer = new BasisFakeImageSanitizer { Width = 8, Height = 8 };
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b, null, sanitizer);
            BasisGlbTestRun.AssertOk(result);
            CollectionAssert.AreEqual(new[] { BasisModelImageFormat.Jpeg }, sanitizer.Formats);
            StringAssert.Contains("\"mimeType\":\"image/png\"", BasisGlbTestRun.JsonOf(result.CleanGlb));
            Assert.That(result.Stats.MaxTextureDimension, Is.EqualTo(8));
        }

        [Test]
        public void RejectsSourceImageAboveSourceCapsBeforeSanitizing()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b = BasisGlbTestBuilder.IndexedQuad16();
            b.AddImage(BasisFakeImageSanitizer.WithDimensions(BasisGlbTestPng.Create(1, 1), 8192, 16));
            b.AddTexture(0);
            b.AddMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}");
            b.Meshes[0] = b.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(b.BuildGlb(), BasisModelSourceFormat.Glb, BasisModelLimits.Desktop);
            Assert.That(prepared.Ok, Is.False);
            Assert.That(prepared.ErrorKind, Is.EqualTo(BasisGlbErrorKind.OverLimit));
            StringAssert.Contains("images[0] is 8,192×16", prepared.Error);
        }

        [Test]
        public void ReceiverRejectsJpegAndOversizeAnd16BitPng()
        {
            BasisGlbTestBuilder jpeg = BasisGlbTestBuilder.IndexedQuad16();
            jpeg.AddImage(BasisGlbTestPng.CreateJpegHeader(4, 4), "image/jpeg");
            jpeg.AddTexture(0);
            jpeg.AddMaterial("{\"emissiveTexture\":{\"index\":0}}");
            jpeg.Meshes[0] = jpeg.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(jpeg), BasisGlbErrorKind.ImageRejected, "received models carry PNG images only");

            BasisGlbTestBuilder big = BasisGlbTestBuilder.IndexedQuad16();
            big.AddImage(BasisFakeImageSanitizer.WithDimensions(BasisGlbTestPng.Create(1, 1), 4096, 4));
            big.AddTexture(0);
            big.AddMaterial("{\"emissiveTexture\":{\"index\":0}}");
            big.Meshes[0] = big.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(big), BasisGlbErrorKind.OverLimit, "images[0]: Image is 4,096×4");

            BasisGlbTestBuilder deep = BasisGlbTestBuilder.IndexedQuad16();
            deep.AddImage(BasisGlbTestPng.Create(2, 2, 6, 16));
            deep.AddTexture(0);
            deep.AddMaterial("{\"emissiveTexture\":{\"index\":0}}");
            deep.Meshes[0] = deep.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            BasisGlbTestRun.AssertFails(BasisGlbTestRun.Receive(deep), BasisGlbErrorKind.ImageRejected, "16-bit");
        }

        [Test]
        public void DropsTextureWithoutSource()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Textures[0] = "{\"extensions\":{\"KHR_texture_basisu\":{\"source\":0}}}";
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.Textures, Is.EqualTo(0));
            Assert.That(result.Stats.Images, Is.EqualTo(0));
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            StringAssert.Contains("\"materials\":[{}]", json);
            Assert.That(result.Stripped & BasisGlbStripped.CompressionFallbackUsed, Is.EqualTo(BasisGlbStripped.CompressionFallbackUsed));
        }

        [Test]
        public void RejectsNegativeEmissiveStrength()
        {
            BasisGlbTestRun.AssertBothFail(WithMaterial("{\"extensions\":{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":-1}}}"),
                BasisGlbErrorKind.Malformed, "emissiveStrength must be at least 0");
        }
    }
}
