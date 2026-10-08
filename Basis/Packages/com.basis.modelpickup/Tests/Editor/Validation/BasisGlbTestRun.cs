using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    /// <summary>Synchronous drivers for the sender and receiver paths, plus assertion helpers.</summary>
    internal static class BasisGlbTestRun
    {
        internal static BasisGlbValidationResult Send(byte[] source, BasisModelSourceFormat format = BasisModelSourceFormat.Glb,
            BasisModelLimits? limits = null, IBasisModelImageSanitizer sanitizer = null, CancellationToken cancellation = default)
        {
            BasisModelLimits l = limits ?? BasisModelLimits.Desktop;
            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(source, format, l, cancellation);
            if (!prepared.Ok) return new BasisGlbValidationResult { Ok = false, Error = prepared.Error, ErrorKind = prepared.ErrorKind };
            IBasisModelImageSanitizer s = sanitizer ?? new BasisFakeImageSanitizer();
            for (int i = 0; i < prepared.Model.ImageCount; i++)
            {
                BasisModelImageSanitizeResult r = s.SanitizeAsync(prepared.Model.CopyImageSource(i), prepared.Model.GetImageFormat(i), cancellation)
                    .GetAwaiter().GetResult();
                if (!r.Ok) return new BasisGlbValidationResult { Ok = false, Error = r.Error, ErrorKind = BasisGlbErrorKind.ImageRejected };
                prepared.Model.SetSanitizedImage(i, r.Png);
            }
            return BasisGlbValidator.Finish(prepared.Model, cancellation);
        }

        internal static BasisGlbValidationResult Send(BasisGlbTestBuilder builder, BasisModelLimits? limits = null, IBasisModelImageSanitizer sanitizer = null)
        {
            return Send(builder.BuildGlb(), BasisModelSourceFormat.Glb, limits, sanitizer);
        }

        internal static BasisGlbValidationResult Receive(byte[] glb, BasisModelLimits? limits = null)
        {
            return BasisGlbValidator.ValidateReceived(glb, limits ?? BasisModelLimits.Desktop);
        }

        internal static BasisGlbValidationResult Receive(BasisGlbTestBuilder builder, BasisModelLimits? limits = null)
        {
            return Receive(builder.BuildGlb(), limits);
        }

        internal static void AssertOk(in BasisGlbValidationResult result)
        {
            Assert.That(result.Ok, Is.True, "Expected success but got " + result.ErrorKind + ": " + result.Error);
            Assert.That(result.CleanGlb, Is.Not.Null);
        }

        internal static void AssertFails(in BasisGlbValidationResult result, BasisGlbErrorKind kind, string contains = null)
        {
            Assert.That(result.Ok, Is.False, "Expected " + kind + " but validation succeeded.");
            Assert.That(result.ErrorKind, Is.EqualTo(kind), result.Error);
            Assert.That(result.CleanGlb, Is.Null);
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
            if (contains != null) StringAssert.Contains(contains, result.Error);
        }

        /// <summary>Both paths reject: the sender with descriptive text, the receiver as a GLB.</summary>
        internal static void AssertBothFail(BasisGlbTestBuilder builder, BasisGlbErrorKind kind, string contains = null)
        {
            byte[] glb = builder.BuildGlb();
            AssertFails(Send(glb), kind, contains);
            AssertFails(Receive(glb), kind, contains);
        }

        internal static string JsonOf(byte[] glb)
        {
            int length = BitConverter.ToInt32(glb, 12);
            return Encoding.UTF8.GetString(glb, 20, length).TrimEnd(' ');
        }

        internal static byte[] BinOf(byte[] glb)
        {
            int jsonLength = BitConverter.ToInt32(glb, 12);
            int binHeader = 20 + jsonLength;
            if (binHeader >= glb.Length) return new byte[0];
            int binLength = BitConverter.ToInt32(glb, binHeader);
            var bin = new byte[binLength];
            Buffer.BlockCopy(glb, binHeader + 8, bin, 0, binLength);
            return bin;
        }

        /// <summary>Replaces the JSON chunk of a GLB (re-padding it), keeping the BIN chunk.</summary>
        internal static byte[] WithJson(byte[] glb, string json)
        {
            return BasisGlbTestBuilder.Assemble(Encoding.UTF8.GetBytes(json), BinOf(glb), new BasisGlbTestContainer());
        }
    }

    /// <summary>Valid inputs covering every feature the canonical form can carry.</summary>
    internal static class BasisGlbTestCorpus
    {
        internal struct Entry
        {
            public string Name;
            public byte[] Bytes;
            public BasisModelSourceFormat Format;

            public override string ToString()
            {
                return Name;
            }
        }

        internal static List<Entry> All()
        {
            var entries = new List<Entry>();
            Add(entries, "triangle", BasisGlbTestBuilder.Triangle());
            Add(entries, "indexed quad", BasisGlbTestBuilder.IndexedQuad16());
            Add(entries, "textured quad", BasisGlbTestBuilder.TexturedQuad(8, 4));
            Add(entries, "skinned strip", BasisGlbTestBuilder.SkinnedStrip(3));
            Add(entries, "quantized", BasisGlbTestBuilder.Quantized());
            Add(entries, "hierarchy", BasisGlbTestBuilder.Hierarchy(5));
            Add(entries, "animated morphed", BasisGlbTestBuilder.AnimatedMorphed());
            Add(entries, "unlit transformed", UnlitTransformed());
            Add(entries, "two materials two samplers", TwoMaterialsTwoSamplers());
            entries.Add(new Entry { Name = "triangle (.gltf)", Bytes = BasisGlbTestBuilder.Triangle().BuildGltfWithDataUris(), Format = BasisModelSourceFormat.GltfJson });
            entries.Add(new Entry { Name = "textured quad (.gltf)", Bytes = BasisGlbTestBuilder.TexturedQuad(4, 4).BuildGltfWithDataUris(), Format = BasisModelSourceFormat.GltfJson });
            entries.Add(new Entry { Name = "skinned strip (.gltf)", Bytes = BasisGlbTestBuilder.SkinnedStrip(2).BuildGltfWithDataUris(true), Format = BasisModelSourceFormat.GltfJson });
            return entries;
        }

        private static void Add(List<Entry> entries, string name, BasisGlbTestBuilder builder)
        {
            entries.Add(new Entry { Name = name, Bytes = builder.BuildGlb(), Format = BasisModelSourceFormat.Glb });
        }

        internal static BasisGlbTestBuilder UnlitTransformed()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Materials[0] = "{\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.5,0.25,1,1],\"baseColorTexture\":{\"index\":0,"
                + "\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.25,0.5],\"rotation\":0.5,\"scale\":[2,2]}}}},"
                + "\"alphaMode\":\"MASK\",\"alphaCutoff\":0.25,\"doubleSided\":true,\"extensions\":{\"KHR_materials_unlit\":{}}}";
            b.ExtensionsUsed = "[\"KHR_materials_unlit\",\"KHR_texture_transform\"]";
            return b;
        }

        /// <summary>One image used through two samplers (glTFast clones it), two materials, emissive and normal maps.</summary>
        internal static BasisGlbTestBuilder TwoMaterialsTwoSamplers()
        {
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f);
            int normal = b.AddFloats("VEC3", 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f);
            int tangent = b.AddFloats("VEC4", 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f);
            int uv = b.AddFloats("VEC2", 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f);
            int first = b.AddIndices8(0, 1, 2);
            int second = b.AddIndices8(0, 2, 3);
            int image = b.AddImage(BasisGlbTestPng.Create(4, 4));
            b.Samplers.Add("{\"magFilter\":9728,\"minFilter\":9728,\"wrapS\":33071,\"wrapT\":33071}");
            b.Samplers.Add("{\"magFilter\":9729,\"minFilter\":9987}");
            int nearest = b.AddTexture(image, 0);
            int trilinear = b.AddTexture(image, 1);
            int a = b.AddMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":" + nearest + "},\"metallicFactor\":0,\"roughnessFactor\":0.5},"
                + "\"normalTexture\":{\"index\":" + trilinear + ",\"scale\":0.75}}");
            int m = b.AddMaterial("{\"emissiveTexture\":{\"index\":" + trilinear + ",\"texCoord\":0},\"emissiveFactor\":[1,0.5,0],"
                + "\"occlusionTexture\":{\"index\":" + nearest + ",\"strength\":0.5},\"alphaMode\":\"BLEND\"}");
            string attributes = "\"NORMAL\":" + normal + ",\"TANGENT\":" + tangent + ",\"TEXCOORD_0\":" + uv;
            b.Meshes.Add("{\"primitives\":[" + BasisGlbTestBuilder.Primitive(position, first, attributes, a) + ","
                + BasisGlbTestBuilder.Primitive(position, second, attributes, m) + "]}");
            b.AddNode("{\"mesh\":0,\"rotation\":[0,0.70710677,0,0.70710677],\"translation\":[1,2,3]}");
            return b;
        }
    }
}
