using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Deterministic ASCII JSON (no whitespace, fixed key order, defaults omitted) and the GLB bytes. The
    /// output carries no names, generator or copyright, and never emits uri, extras, animations, cameras, matrix,
    /// weights, targets, sparse, mode, minVersion, accessor byteOffset, or any extension other than unlit,
    /// texture transform and mesh quantization. Every field glTFast reads is therefore either validated or absent.
    /// </summary>
    public static class BasisGltfCanonicalWriter
    {
        private const string ExtUnlit = "KHR_materials_unlit";
        private const string ExtQuantization = "KHR_mesh_quantization";
        private const string ExtTextureTransform = "KHR_texture_transform";

        /// <summary>Builds the canonical JSON. Its length is capped at MaxJsonBytes while writing.</summary>
        public static bool TryBuildJson(BasisGltfWork work, out byte[] json, out int length)
        {
            BasisGltfPlan plan = work.Plan;
            var w = new JsonBuilder(work.Limits.MaxJsonBytes);
            w.Raw("{\"asset\":{\"version\":\"2.0\"}");
            if (plan.UsesUnlit || plan.UsesQuantization || plan.UsesTextureTransform)
            {
                // Sorted ordinal: KHR_materials_unlit < KHR_mesh_quantization < KHR_texture_transform.
                w.Raw(",\"extensionsUsed\":[");
                bool first = true;
                if (plan.UsesUnlit) w.ListString(ref first, ExtUnlit);
                if (plan.UsesQuantization) w.ListString(ref first, ExtQuantization);
                if (plan.UsesTextureTransform) w.ListString(ref first, ExtTextureTransform);
                w.Raw("]");
                if (plan.UsesQuantization) w.Raw(",\"extensionsRequired\":[\"KHR_mesh_quantization\"]");
            }
            w.Raw(",\"scene\":0,\"scenes\":[{\"nodes\":");
            w.IntArray(plan.SceneRoots);
            w.Raw("}]");

            w.Raw(",\"nodes\":[");
            for (int n = 0; n < plan.NodeCount; n++)
            {
                if (n > 0) w.Raw(",");
                WriteNode(w, plan, n);
            }
            w.Raw("]");

            w.Raw(",\"meshes\":[");
            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                if (m > 0) w.Raw(",");
                WriteMesh(w, plan.Meshes[m]);
            }
            w.Raw("]");

            if (plan.Skins.Count > 0)
            {
                w.Raw(",\"skins\":[");
                for (int s = 0; s < plan.Skins.Count; s++)
                {
                    if (s > 0) w.Raw(",");
                    BasisGltfCanonSkin skin = plan.Skins[s];
                    w.Raw("{");
                    bool first = true;
                    if (skin.InverseBindMatrices >= 0) w.MemberInt(ref first, "inverseBindMatrices", skin.InverseBindMatrices);
                    if (skin.Skeleton >= 0) w.MemberInt(ref first, "skeleton", skin.Skeleton);
                    w.Member(ref first, "joints");
                    w.IntArray(skin.Joints);
                    w.Raw("}");
                }
                w.Raw("]");
            }

            if (plan.Materials.Count > 0)
            {
                w.Raw(",\"materials\":[");
                for (int m = 0; m < plan.Materials.Count; m++)
                {
                    if (m > 0) w.Raw(",");
                    WriteMaterial(w, plan.Materials[m]);
                }
                w.Raw("]");
            }

            if (plan.Textures.Count > 0)
            {
                w.Raw(",\"textures\":[");
                for (int t = 0; t < plan.Textures.Count; t++)
                {
                    if (t > 0) w.Raw(",");
                    BasisGltfCanonTexture texture = plan.Textures[t];
                    w.Raw("{");
                    bool first = true;
                    if (texture.Sampler >= 0) w.MemberInt(ref first, "sampler", texture.Sampler);
                    w.MemberInt(ref first, "source", texture.Image);
                    w.Raw("}");
                }
                w.Raw("]");
            }

            if (plan.Samplers.Count > 0)
            {
                w.Raw(",\"samplers\":[");
                for (int s = 0; s < plan.Samplers.Count; s++)
                {
                    if (s > 0) w.Raw(",");
                    BasisGltfCanonSampler sampler = plan.Samplers[s];
                    w.Raw("{");
                    bool first = true;
                    if (sampler.Mag != 0) w.MemberInt(ref first, "magFilter", sampler.Mag);
                    if (sampler.Min != 0) w.MemberInt(ref first, "minFilter", sampler.Min);
                    if (sampler.WrapS != 10497) w.MemberInt(ref first, "wrapS", sampler.WrapS);
                    if (sampler.WrapT != 10497) w.MemberInt(ref first, "wrapT", sampler.WrapT);
                    w.Raw("}");
                }
                w.Raw("]");
            }

            int accessorCount = plan.Accessors.Count;
            if (plan.Images.Count > 0)
            {
                w.Raw(",\"images\":[");
                for (int i = 0; i < plan.Images.Count; i++)
                {
                    if (i > 0) w.Raw(",");
                    w.Raw("{\"bufferView\":");
                    w.Int(accessorCount + i);
                    w.Raw(",\"mimeType\":\"image/png\"}");
                }
                w.Raw("]");
            }

            w.Raw(",\"accessors\":[");
            for (int a = 0; a < accessorCount; a++)
            {
                if (a > 0) w.Raw(",");
                BasisGltfCanonAccessor accessor = plan.Accessors[a];
                w.Raw("{\"bufferView\":");
                w.Int(a);
                w.Raw(",\"componentType\":");
                w.Int(accessor.ComponentType);
                if (accessor.Normalized) w.Raw(",\"normalized\":true");
                w.Raw(",\"count\":");
                w.Int(accessor.Count);
                w.Raw(",\"type\":\"");
                w.Raw(BasisGltfType.Name(accessor.Type));
                w.Raw("\"");
                if (accessor.IsPosition)
                {
                    w.Raw(",\"min\":");
                    w.FloatArray(accessor.Min, 3);
                    w.Raw(",\"max\":");
                    w.FloatArray(accessor.Max, 3);
                }
                w.Raw("}");
            }
            w.Raw("]");

            w.Raw(",\"bufferViews\":[");
            for (int a = 0; a < accessorCount; a++)
            {
                if (a > 0) w.Raw(",");
                BasisGltfCanonAccessor accessor = plan.Accessors[a];
                WriteView(w, accessor.ViewOffset, accessor.ViewLength);
                // glTFast reads padded views tightly packed unless byteStride says otherwise.
                if (accessor.Role == BasisGltfAccessorRole.Vertex && accessor.DestStride != accessor.ElementSize)
                {
                    w.Raw(",\"byteStride\":");
                    w.Int(accessor.DestStride);
                }
                if (accessor.Role == BasisGltfAccessorRole.Vertex) w.Raw(",\"target\":34962");
                else if (accessor.Role == BasisGltfAccessorRole.Index) w.Raw(",\"target\":34963");
                w.Raw("}");
            }
            for (int i = 0; i < plan.Images.Count; i++)
            {
                if (accessorCount + i > 0) w.Raw(",");
                WriteView(w, plan.Images[i].ViewOffset, plan.Images[i].Final.Length);
                w.Raw("}");
            }
            w.Raw("]");

            w.Raw(",\"buffers\":[{\"byteLength\":");
            w.Int(plan.BinLength);
            w.Raw("}]}");

            if (w.Overflow)
            {
                json = null;
                length = 0;
                return work.Fail(BasisGlbErrorKind.OverLimit, "Canonical JSON is more than " + BasisGlbErrors.FormatBytes(work.Limits.MaxJsonBytes)
                    + ". The maximum is " + BasisGlbErrors.FormatBytes(work.Limits.MaxJsonBytes) + ".");
            }
            json = w.Buffer;
            length = w.Length;
            return true;
        }

        private static void WriteView(JsonBuilder w, long offset, long length)
        {
            w.Raw("{\"buffer\":0");
            if (offset != 0)
            {
                w.Raw(",\"byteOffset\":");
                w.Int(offset);
            }
            w.Raw(",\"byteLength\":");
            w.Int(length);
        }

        private static void WriteNode(JsonBuilder w, BasisGltfPlan plan, int n)
        {
            w.Raw("{");
            bool first = true;
            int[] children = plan.NodeChildren[n];
            if (children.Length > 0)
            {
                w.Member(ref first, "children");
                w.IntArray(children);
            }
            if (plan.NodeMesh[n] >= 0) w.MemberInt(ref first, "mesh", plan.NodeMesh[n]);
            if (plan.NodeSkin[n] >= 0) w.MemberInt(ref first, "skin", plan.NodeSkin[n]);
            float[] trs = plan.NodeTrs;
            int o = n * 10;
            if (trs[o] != 0f || trs[o + 1] != 0f || trs[o + 2] != 0f)
            {
                w.Member(ref first, "translation");
                w.FloatArray(trs, o, 3);
            }
            if (trs[o + 3] != 0f || trs[o + 4] != 0f || trs[o + 5] != 0f || trs[o + 6] != 1f)
            {
                w.Member(ref first, "rotation");
                w.FloatArray(trs, o + 3, 4);
            }
            if (trs[o + 7] != 1f || trs[o + 8] != 1f || trs[o + 9] != 1f)
            {
                w.Member(ref first, "scale");
                w.FloatArray(trs, o + 7, 3);
            }
            w.Raw("}");
        }

        private static void WriteMesh(JsonBuilder w, BasisGltfCanonMesh mesh)
        {
            w.Raw("{\"primitives\":[");
            for (int p = 0; p < mesh.Primitives.Count; p++)
            {
                if (p > 0) w.Raw(",");
                BasisGltfCanonPrimitive primitive = mesh.Primitives[p];
                w.Raw("{\"attributes\":{\"POSITION\":");
                w.Int(primitive.Position);
                bool first = false;
                if (primitive.Normal >= 0) w.MemberInt(ref first, "NORMAL", primitive.Normal);
                if (primitive.Tangent >= 0) w.MemberInt(ref first, "TANGENT", primitive.Tangent);
                if (primitive.TexCoord0 >= 0) w.MemberInt(ref first, "TEXCOORD_0", primitive.TexCoord0);
                if (primitive.TexCoord1 >= 0) w.MemberInt(ref first, "TEXCOORD_1", primitive.TexCoord1);
                if (primitive.Color0 >= 0) w.MemberInt(ref first, "COLOR_0", primitive.Color0);
                if (primitive.Joints0 >= 0) w.MemberInt(ref first, "JOINTS_0", primitive.Joints0);
                if (primitive.Weights0 >= 0) w.MemberInt(ref first, "WEIGHTS_0", primitive.Weights0);
                w.Raw("}");
                if (primitive.Indices >= 0) w.MemberInt(ref first, "indices", primitive.Indices);
                if (primitive.Material >= 0) w.MemberInt(ref first, "material", primitive.Material);
                w.Raw("}");
            }
            w.Raw("]}");
        }

        private static void WriteMaterial(JsonBuilder w, BasisGltfCanonMaterial material)
        {
            w.Raw("{");
            bool first = true;
            float[] baseColor = material.BaseColor;
            bool hasBaseColor = baseColor[0] != 1f || baseColor[1] != 1f || baseColor[2] != 1f || baseColor[3] != 1f;
            bool hasPbr = hasBaseColor || material.BaseColorTex.Texture >= 0 || material.Metallic != 1f || material.Roughness != 1f
                || material.MetallicRoughnessTex.Texture >= 0;
            if (hasPbr)
            {
                w.Member(ref first, "pbrMetallicRoughness");
                w.Raw("{");
                bool inner = true;
                if (hasBaseColor)
                {
                    w.Member(ref inner, "baseColorFactor");
                    w.FloatArray(baseColor, 4);
                }
                if (material.BaseColorTex.Texture >= 0)
                {
                    w.Member(ref inner, "baseColorTexture");
                    WriteTextureRef(w, material.BaseColorTex, null);
                }
                if (material.Metallic != 1f)
                {
                    w.Member(ref inner, "metallicFactor");
                    w.Float(material.Metallic);
                }
                if (material.Roughness != 1f)
                {
                    w.Member(ref inner, "roughnessFactor");
                    w.Float(material.Roughness);
                }
                if (material.MetallicRoughnessTex.Texture >= 0)
                {
                    w.Member(ref inner, "metallicRoughnessTexture");
                    WriteTextureRef(w, material.MetallicRoughnessTex, null);
                }
                w.Raw("}");
            }
            if (material.NormalTex.Texture >= 0)
            {
                w.Member(ref first, "normalTexture");
                WriteTextureRef(w, material.NormalTex, "scale");
            }
            if (material.OcclusionTex.Texture >= 0)
            {
                w.Member(ref first, "occlusionTexture");
                WriteTextureRef(w, material.OcclusionTex, "strength");
            }
            if (material.EmissiveTex.Texture >= 0)
            {
                w.Member(ref first, "emissiveTexture");
                WriteTextureRef(w, material.EmissiveTex, null);
            }
            float[] emissive = material.Emissive;
            if (emissive[0] != 0f || emissive[1] != 0f || emissive[2] != 0f)
            {
                w.Member(ref first, "emissiveFactor");
                w.FloatArray(emissive, 3);
            }
            if (material.AlphaMode == 1)
            {
                w.Member(ref first, "alphaMode");
                w.Raw("\"MASK\"");
                if (material.AlphaCutoff != 0.5f)
                {
                    w.Member(ref first, "alphaCutoff");
                    w.Float(material.AlphaCutoff);
                }
            }
            else if (material.AlphaMode == 2)
            {
                w.Member(ref first, "alphaMode");
                w.Raw("\"BLEND\"");
            }
            if (material.DoubleSided)
            {
                w.Member(ref first, "doubleSided");
                w.Raw("true");
            }
            if (material.Unlit)
            {
                w.Member(ref first, "extensions");
                w.Raw("{\"KHR_materials_unlit\":{}}");
            }
            w.Raw("}");
        }

        private static void WriteTextureRef(JsonBuilder w, in BasisGltfCanonTextureRef reference, string factorName)
        {
            w.Raw("{\"index\":");
            w.Int(reference.Texture);
            bool first = false;
            if (reference.TexCoord != 0) w.MemberInt(ref first, "texCoord", reference.TexCoord);
            if (factorName != null && reference.ScaleOrStrength != 1f)
            {
                w.Member(ref first, factorName);
                w.Float(reference.ScaleOrStrength);
            }
            if (reference.HasTransform)
            {
                w.Member(ref first, "extensions");
                w.Raw("{\"KHR_texture_transform\":{");
                bool inner = true;
                if (reference.OffsetU != 0f || reference.OffsetV != 0f)
                {
                    w.Member(ref inner, "offset");
                    w.Raw("[");
                    w.Float(reference.OffsetU);
                    w.Raw(",");
                    w.Float(reference.OffsetV);
                    w.Raw("]");
                }
                if (reference.Rotation != 0f)
                {
                    w.Member(ref inner, "rotation");
                    w.Float(reference.Rotation);
                }
                if (reference.ScaleU != 1f || reference.ScaleV != 1f)
                {
                    w.Member(ref inner, "scale");
                    w.Raw("[");
                    w.Float(reference.ScaleU);
                    w.Raw(",");
                    w.Float(reference.ScaleV);
                    w.Raw("]");
                }
                w.Raw("}}");
            }
            w.Raw("}");
        }

        /// <summary>
        /// GLB = 12-byte header, JSON chunk padded with spaces, BIN chunk padded with zeros. The total is known before
        /// the single allocation; BIN data is copied straight from the source buffers into the output.
        /// </summary>
        public static long GlbLength(int jsonLength, long binLength)
        {
            return 12 + 8 + BasisGltfCanonicalizer.Align4(jsonLength) + 8 + BasisGltfCanonicalizer.Align4(binLength);
        }

        public static byte[] WriteGlb(BasisGltfPlan plan, byte[] json, int jsonLength)
        {
            int jsonPadded = (int)BasisGltfCanonicalizer.Align4(jsonLength);
            int binPadded = (int)BasisGltfCanonicalizer.Align4(plan.BinLength);
            int total = 12 + 8 + jsonPadded + 8 + binPadded;
            var glb = new byte[total];
            var span = new Span<byte>(glb);
            BinaryPrimitives.WriteUInt32LittleEndian(span, BasisGlbContainer.Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)jsonPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), BasisGlbContainer.JsonChunkType);
            Buffer.BlockCopy(json, 0, glb, 20, jsonLength);
            for (int i = 20 + jsonLength; i < 20 + jsonPadded; i++) glb[i] = (byte)' ';
            int binHeader = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(binHeader), (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(binHeader + 4), BasisGlbContainer.BinChunkType);
            int bin = binHeader + 8;

            for (int a = 0; a < plan.Accessors.Count; a++)
            {
                BasisGltfCanonAccessor accessor = plan.Accessors[a];
                int destination = bin + (int)accessor.ViewOffset;
                int elementSize = accessor.ElementSize;
                if (accessor.SourceStride == elementSize && accessor.DestStride == elementSize)
                {
                    Buffer.BlockCopy(accessor.Data, (int)accessor.DataOffset, glb, destination, accessor.Count * elementSize);
                    continue;
                }
                // Gaps, stride padding and view tails in the source are never relayed; padding in the output stays zero.
                long source = accessor.DataOffset;
                for (int i = 0; i < accessor.Count; i++)
                {
                    Buffer.BlockCopy(accessor.Data, (int)source, glb, destination, elementSize);
                    source += accessor.SourceStride;
                    destination += accessor.DestStride;
                }
            }
            for (int i = 0; i < plan.Images.Count; i++)
            {
                BasisGltfCanonImage image = plan.Images[i];
                Buffer.BlockCopy(image.Final, 0, glb, bin + (int)image.ViewOffset, image.Final.Length);
            }
            return glb;
        }

        /// <summary>Growable ASCII buffer capped at a maximum; past the cap it stops writing and sets <see cref="Overflow"/>.</summary>
        private sealed class JsonBuilder
        {
            private readonly int _max;
            public byte[] Buffer = new byte[4096];
            public int Length;
            public bool Overflow;

            public JsonBuilder(int max)
            {
                _max = max;
            }

            private bool Ensure(int extra)
            {
                if (Overflow) return false;
                long needed = (long)Length + extra;
                if (needed > _max)
                {
                    Overflow = true;
                    return false;
                }
                if (needed > Buffer.Length)
                {
                    long size = Math.Min(Math.Max(needed, (long)Buffer.Length * 2), _max);
                    Array.Resize(ref Buffer, (int)size);
                }
                return true;
            }

            public void Raw(string ascii)
            {
                if (!Ensure(ascii.Length)) return;
                for (int i = 0; i < ascii.Length; i++) Buffer[Length++] = (byte)ascii[i];
            }

            public void Int(long value)
            {
                Span<char> digits = stackalloc char[20];
                if (!value.TryFormat(digits, out int written, default, CultureInfo.InvariantCulture)) return;
                if (!Ensure(written)) return;
                for (int i = 0; i < written; i++) Buffer[Length++] = (byte)digits[i];
            }

            /// <summary>G9 round-trips every binary32 on every runtime (R has had Mono bugs); -0 is written as 0.</summary>
            public void Float(float value)
            {
                Raw(BasisGlbNumbers.PositiveZero(value).ToString("G9", CultureInfo.InvariantCulture));
            }

            public void Member(ref bool first, string name)
            {
                Raw(first ? "\"" : ",\"");
                first = false;
                Raw(name);
                Raw("\":");
            }

            public void MemberInt(ref bool first, string name, long value)
            {
                Member(ref first, name);
                Int(value);
            }

            public void ListString(ref bool first, string value)
            {
                Raw(first ? "\"" : ",\"");
                first = false;
                Raw(value);
                Raw("\"");
            }

            public void IntArray(int[] values)
            {
                Raw("[");
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) Raw(",");
                    Int(values[i]);
                }
                Raw("]");
            }

            public void FloatArray(float[] values, int count)
            {
                FloatArray(values, 0, count);
            }

            public void FloatArray(float[] values, int offset, int count)
            {
                Raw("[");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) Raw(",");
                    Float(values[offset + i]);
                }
                Raw("]");
            }
        }
    }
}
