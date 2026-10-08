using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup.Tests.Validation
{
    /// <summary>Knobs for malformed GLB containers.</summary>
    internal sealed class BasisGlbTestContainer
    {
        public uint Magic = 0x46546C67;
        public uint Version = 2;
        public long LengthOverride = -1;
        public uint JsonType = 0x4E4F534A;
        public uint BinType = 0x004E4942;
        public bool SwapChunks;
        public bool DuplicateJson;
        public bool DuplicateBin;
        public bool ExtraChunk;
        public uint ExtraChunkType = 0x12345678;
        public int TrailingBytes;
        public byte JsonPadByte = 0x20;
        public bool OmitBin;
        public long JsonLengthOverride = -1;
        public long BinLengthOverride = -1;
        public bool Utf8Bom;
    }

    /// <summary>
    /// Assembles glTF JSON from raw fragments (so tests can inject anything) plus a BIN stream. Every collection is a
    /// list of raw JSON object strings; the helpers append well-formed entries and return their index.
    /// </summary>
    internal sealed class BasisGlbTestBuilder
    {
        public string Asset = "{\"version\":\"2.0\"}";
        public string SceneIndex = "0";
        public List<int> SceneRoots = new List<int>();
        public List<string> Scenes;
        public string ExtensionsUsed;
        public string ExtensionsRequired;
        public string ExtraRootMembers;
        public string BufferJson;
        public List<string> Nodes = new List<string>();
        public List<string> Meshes = new List<string>();
        public List<string> Accessors = new List<string>();
        public List<string> BufferViews = new List<string>();
        public List<string> Materials = new List<string>();
        public List<string> Textures = new List<string>();
        public List<string> Images = new List<string>();
        public List<string> Samplers = new List<string>();
        public List<string> Skins = new List<string>();
        private readonly MemoryStream _bin = new MemoryStream();

        public long BinLength => _bin.Length;

        internal static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        public int AddView(byte[] data, int? byteStride = null, int? target = null)
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            long offset = _bin.Length;
            _bin.Write(data, 0, data.Length);
            var json = new StringBuilder("{\"buffer\":0,\"byteOffset\":").Append(offset).Append(",\"byteLength\":").Append(data.Length);
            if (byteStride.HasValue) json.Append(",\"byteStride\":").Append(byteStride.Value);
            if (target.HasValue) json.Append(",\"target\":").Append(target.Value);
            json.Append('}');
            BufferViews.Add(json.ToString());
            return BufferViews.Count - 1;
        }

        public int AddAccessor(int view, int componentType, int count, string type, bool normalized = false, int byteOffset = 0, string extraJson = null)
        {
            var json = new StringBuilder("{\"bufferView\":").Append(view).Append(",\"componentType\":").Append(componentType)
                .Append(",\"count\":").Append(count).Append(",\"type\":\"").Append(type).Append('"');
            if (normalized) json.Append(",\"normalized\":true");
            if (byteOffset != 0) json.Append(",\"byteOffset\":").Append(byteOffset);
            if (extraJson != null) json.Append(',').Append(extraJson);
            json.Append('}');
            Accessors.Add(json.ToString());
            return Accessors.Count - 1;
        }

        /// <summary>Float accessor in its own view; VEC3 also gets (correct) min/max.</summary>
        public int AddFloats(string type, params float[] values)
        {
            int components = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : 16;
            var data = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(data, i * 4);
            int view = AddView(data);
            string extra = null;
            if (type == "VEC3")
            {
                var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < values.Length; i++)
                {
                    min[i % 3] = Math.Min(min[i % 3], values[i]);
                    max[i % 3] = Math.Max(max[i % 3], values[i]);
                }
                extra = "\"min\":[" + F(min[0]) + "," + F(min[1]) + "," + F(min[2]) + "],\"max\":[" + F(max[0]) + "," + F(max[1]) + "," + F(max[2]) + "]";
            }
            return AddAccessor(view, BasisGltfComponent.Float, values.Length / components, type, false, 0, extra);
        }

        public int AddIndices8(params int[] indices)
        {
            var data = new byte[indices.Length];
            for (int i = 0; i < indices.Length; i++) data[i] = (byte)indices[i];
            return AddAccessor(AddView(data), BasisGltfComponent.UnsignedByte, indices.Length, "SCALAR");
        }

        public int AddIndices16(params int[] indices)
        {
            var data = new byte[indices.Length * 2];
            for (int i = 0; i < indices.Length; i++) BitConverter.GetBytes((ushort)indices[i]).CopyTo(data, i * 2);
            return AddAccessor(AddView(data), BasisGltfComponent.UnsignedShort, indices.Length, "SCALAR");
        }

        public int AddIndices32(params int[] indices)
        {
            var data = new byte[indices.Length * 4];
            for (int i = 0; i < indices.Length; i++) BitConverter.GetBytes((uint)indices[i]).CopyTo(data, i * 4);
            return AddAccessor(AddView(data), BasisGltfComponent.UnsignedInt, indices.Length, "SCALAR");
        }

        /// <summary>A mesh with one triangle primitive.</summary>
        public int AddMesh(int position, int indices = -1, string extraAttributes = null, int material = -1, string extraPrimitive = null)
        {
            Meshes.Add("{\"primitives\":[" + Primitive(position, indices, extraAttributes, material, extraPrimitive) + "]}");
            return Meshes.Count - 1;
        }

        public static string Primitive(int position, int indices = -1, string extraAttributes = null, int material = -1, string extraPrimitive = null)
        {
            var json = new StringBuilder("{\"attributes\":{\"POSITION\":").Append(position);
            if (extraAttributes != null) json.Append(',').Append(extraAttributes);
            json.Append('}');
            if (indices >= 0) json.Append(",\"indices\":").Append(indices);
            if (material >= 0) json.Append(",\"material\":").Append(material);
            if (extraPrimitive != null) json.Append(',').Append(extraPrimitive);
            json.Append('}');
            return json.ToString();
        }

        public int AddNode(string json, bool root = true)
        {
            Nodes.Add(json);
            if (root) SceneRoots.Add(Nodes.Count - 1);
            return Nodes.Count - 1;
        }

        public int AddImage(byte[] bytes, string mime = "image/png")
        {
            int view = AddView(bytes);
            Images.Add("{\"bufferView\":" + view + (mime != null ? ",\"mimeType\":\"" + mime + "\"" : string.Empty) + "}");
            return Images.Count - 1;
        }

        public int AddTexture(int source, int sampler = -1)
        {
            Textures.Add("{" + (sampler >= 0 ? "\"sampler\":" + sampler + "," : string.Empty) + "\"source\":" + source + "}");
            return Textures.Count - 1;
        }

        public int AddMaterial(string json)
        {
            Materials.Add(json);
            return Materials.Count - 1;
        }

        public byte[] BinBytes()
        {
            return _bin.ToArray();
        }

        public string BuildJson()
        {
            return BuildJson(BufferJson ?? (BinLength > 0 ? "{\"byteLength\":" + BinLength + "}" : null));
        }

        private string BuildJson(string bufferJson)
        {
            var json = new StringBuilder("{\"asset\":").Append(Asset);
            if (ExtensionsUsed != null) json.Append(",\"extensionsUsed\":").Append(ExtensionsUsed);
            if (ExtensionsRequired != null) json.Append(",\"extensionsRequired\":").Append(ExtensionsRequired);
            if (SceneIndex != null) json.Append(",\"scene\":").Append(SceneIndex);
            if (Scenes != null)
            {
                Append(json, "scenes", Scenes);
            }
            else
            {
                json.Append(",\"scenes\":[{\"nodes\":[");
                for (int i = 0; i < SceneRoots.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append(SceneRoots[i]);
                }
                json.Append("]}]");
            }
            Append(json, "nodes", Nodes);
            Append(json, "meshes", Meshes);
            Append(json, "accessors", Accessors);
            Append(json, "bufferViews", BufferViews);
            if (bufferJson != null) json.Append(",\"buffers\":[").Append(bufferJson).Append(']');
            Append(json, "materials", Materials);
            Append(json, "textures", Textures);
            Append(json, "images", Images);
            Append(json, "samplers", Samplers);
            Append(json, "skins", Skins);
            if (ExtraRootMembers != null) json.Append(',').Append(ExtraRootMembers);
            json.Append('}');
            return json.ToString();
        }

        private static void Append(StringBuilder json, string name, List<string> items)
        {
            if (items.Count == 0) return;
            json.Append(",\"").Append(name).Append("\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(items[i]);
            }
            json.Append(']');
        }

        public byte[] BuildGlb(BasisGlbTestContainer c = null)
        {
            c = c ?? new BasisGlbTestContainer();
            return Assemble(Encoding.UTF8.GetBytes(BuildJson()), BinBytes(), c);
        }

        /// <summary>The same model as a .gltf whose buffer is a base64 data: URI.</summary>
        public byte[] BuildGltfWithDataUris(bool bom = false)
        {
            byte[] bin = BinBytes();
            string buffer = "{\"byteLength\":" + bin.Length + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bin) + "\"}";
            byte[] json = Encoding.UTF8.GetBytes(BuildJson(buffer));
            if (!bom) return json;
            var withBom = new byte[json.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Buffer.BlockCopy(json, 0, withBom, 3, json.Length);
            return withBom;
        }

        internal static byte[] Assemble(byte[] json, byte[] bin, BasisGlbTestContainer c)
        {
            if (c.Utf8Bom)
            {
                var withBom = new byte[json.Length + 3];
                withBom[0] = 0xEF;
                withBom[1] = 0xBB;
                withBom[2] = 0xBF;
                Buffer.BlockCopy(json, 0, withBom, 3, json.Length);
                json = withBom;
            }
            byte[] jsonChunk = Chunk(c.JsonType, Pad(json, c.JsonPadByte), c.JsonLengthOverride);
            byte[] binChunk = bin.Length > 0 && !c.OmitBin ? Chunk(c.BinType, Pad(bin, 0), c.BinLengthOverride) : null;
            var chunks = new List<byte[]>();
            if (c.SwapChunks && binChunk != null)
            {
                chunks.Add(binChunk);
                chunks.Add(jsonChunk);
            }
            else
            {
                chunks.Add(jsonChunk);
                if (c.DuplicateJson) chunks.Add(jsonChunk);
                if (binChunk != null) chunks.Add(binChunk);
                if (c.DuplicateBin && binChunk != null) chunks.Add(binChunk);
            }
            if (c.ExtraChunk) chunks.Add(Chunk(c.ExtraChunkType, new byte[4], -1));
            long total = 12;
            foreach (byte[] chunk in chunks) total += chunk.Length;
            total += c.TrailingBytes;
            var glb = new byte[total];
            BitConverter.GetBytes(c.Magic).CopyTo(glb, 0);
            BitConverter.GetBytes(c.Version).CopyTo(glb, 4);
            BitConverter.GetBytes((uint)(c.LengthOverride >= 0 ? c.LengthOverride : total)).CopyTo(glb, 8);
            int o = 12;
            foreach (byte[] chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, glb, o, chunk.Length);
                o += chunk.Length;
            }
            return glb;
        }

        private static byte[] Pad(byte[] data, byte pad)
        {
            int length = (data.Length + 3) & ~3;
            var padded = new byte[length];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            for (int i = data.Length; i < length; i++) padded[i] = pad;
            return padded;
        }

        private static byte[] Chunk(uint type, byte[] data, long lengthOverride)
        {
            var chunk = new byte[data.Length + 8];
            BitConverter.GetBytes((uint)(lengthOverride >= 0 ? lengthOverride : data.Length)).CopyTo(chunk, 0);
            BitConverter.GetBytes(type).CopyTo(chunk, 4);
            Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
            return chunk;
        }

        // ---- factories -------------------------------------------------------------------------------------------

        internal static readonly float[] TrianglePositions = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };

        public static BasisGlbTestBuilder Triangle()
        {
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", TrianglePositions);
            int mesh = b.AddMesh(position);
            b.AddNode("{\"mesh\":" + mesh + "}");
            return b;
        }

        /// <summary>A 1 m × 1 m quad in the XZ plane (SizeY = 0), u16 indices, normals and UVs.</summary>
        public static BasisGlbTestBuilder IndexedQuad16()
        {
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f);
            int normal = b.AddFloats("VEC3", 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f);
            int uv = b.AddFloats("VEC2", 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f);
            int indices = b.AddIndices16(0, 2, 1, 0, 3, 2);
            int mesh = b.AddMesh(position, indices, "\"NORMAL\":" + normal + ",\"TEXCOORD_0\":" + uv);
            b.AddNode("{\"mesh\":" + mesh + "}");
            return b;
        }

        public static BasisGlbTestBuilder TexturedQuad(int width, int height)
        {
            BasisGlbTestBuilder b = IndexedQuad16();
            int image = b.AddImage(BasisGlbTestPng.Create(width, height));
            int texture = b.AddTexture(image);
            int material = b.AddMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":" + texture + "}}}");
            b.Meshes[0] = "{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":" + material + "}]}";
            return b;
        }

        /// <summary>A vertical strip of quads skinned to a chain of <paramref name="joints"/> joints, one joint per row.</summary>
        public static BasisGlbTestBuilder SkinnedStrip(int joints)
        {
            var b = new BasisGlbTestBuilder();
            int rows = joints + 1;
            var positions = new List<float>();
            var jointData = new List<byte>();
            var weights = new List<float>();
            for (int r = 0; r < rows; r++)
            {
                for (int side = 0; side < 2; side++)
                {
                    positions.Add(side);
                    positions.Add(r * 0.5f);
                    positions.Add(0f);
                    int joint = Math.Min(r, joints - 1);
                    jointData.Add((byte)joint);
                    jointData.Add(0);
                    jointData.Add(0);
                    jointData.Add(0);
                    weights.Add(1f);
                    weights.Add(0f);
                    weights.Add(0f);
                    weights.Add(0f);
                }
            }
            var indices = new List<int>();
            for (int r = 0; r < joints; r++)
            {
                int a = r * 2;
                indices.Add(a);
                indices.Add(a + 1);
                indices.Add(a + 3);
                indices.Add(a);
                indices.Add(a + 3);
                indices.Add(a + 2);
            }
            int position = b.AddFloats("VEC3", positions.ToArray());
            int jointsAccessor = b.AddAccessor(b.AddView(jointData.ToArray()), BasisGltfComponent.UnsignedByte, rows * 2, "VEC4");
            int weightsAccessor = b.AddFloats("VEC4", weights.ToArray());
            int index = b.AddIndices16(indices.ToArray());
            var ibm = new List<float>();
            for (int j = 0; j < joints; j++)
            {
                // Joint j sits at y = 0.5 * j (parent-relative 0.5 per link); its inverse bind matrix undoes that.
                ibm.AddRange(new[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -0.5f * j, 0f, 1f });
            }
            int ibmAccessor = b.AddFloats("MAT4", ibm.ToArray());
            int mesh = b.AddMesh(position, index, "\"JOINTS_0\":" + jointsAccessor + ",\"WEIGHTS_0\":" + weightsAccessor);
            int firstJoint = b.Nodes.Count + 1;
            var jointList = new StringBuilder();
            for (int j = 0; j < joints; j++)
            {
                if (j > 0) jointList.Append(',');
                jointList.Append(firstJoint + j);
            }
            b.AddNode("{\"mesh\":" + mesh + ",\"skin\":0}");
            for (int j = 0; j < joints; j++)
            {
                var parts = new List<string>();
                if (j > 0) parts.Add("\"translation\":[0,0.5,0]");
                if (j + 1 < joints) parts.Add("\"children\":[" + (firstJoint + j + 1) + "]");
                b.AddNode("{" + string.Join(",", parts) + "}", j == 0);
            }
            b.Skins.Add("{\"inverseBindMatrices\":" + ibmAccessor + ",\"skeleton\":" + firstJoint + ",\"joints\":[" + jointList + "]}");
            return b;
        }

        /// <summary>KHR_mesh_quantization: u16 positions, i8 normals, u8 normalised UVs (all padded VEC3/VEC2 views).</summary>
        public static BasisGlbTestBuilder Quantized()
        {
            var b = new BasisGlbTestBuilder();
            var positions = new byte[] { 0, 0, 0, 0, 0, 0, 0xE8, 0x03, 0, 0, 0, 0, 0, 0, 0xE8, 0x03, 0, 0 }; // (0,0,0) (1000,0,0) (0,1000,0)
            int position = b.AddAccessor(b.AddView(positions), BasisGltfComponent.UnsignedShort, 3, "VEC3", false, 0,
                "\"min\":[0,0,0],\"max\":[1000,1000,0]");
            var normals = new byte[] { 0, 0, 127, 0, 0, 127, 0, 0, 127 };
            int normal = b.AddAccessor(b.AddView(normals), BasisGltfComponent.Byte, 3, "VEC3", true);
            var uvs = new byte[] { 0, 0, 255, 0, 0, 255 };
            int uv = b.AddAccessor(b.AddView(uvs), BasisGltfComponent.UnsignedByte, 3, "VEC2", true);
            int mesh = b.AddMesh(position, -1, "\"NORMAL\":" + normal + ",\"TEXCOORD_0\":" + uv);
            b.AddNode("{\"mesh\":" + mesh + ",\"scale\":[0.001,0.001,0.001]}");
            b.ExtensionsUsed = "[\"KHR_mesh_quantization\"]";
            b.ExtensionsRequired = "[\"KHR_mesh_quantization\"]";
            return b;
        }

        /// <summary>A chain of <paramref name="depth"/> nodes, each translated 1 m in X; the leaf has the triangle.</summary>
        public static BasisGlbTestBuilder Hierarchy(int depth)
        {
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", TrianglePositions);
            int mesh = b.AddMesh(position);
            for (int i = 0; i < depth; i++)
            {
                bool leaf = i == depth - 1;
                string body = "\"translation\":[1,0,0]" + (leaf ? ",\"mesh\":" + mesh : ",\"children\":[" + (i + 1) + "]");
                b.AddNode("{" + body + "}", i == 0);
            }
            return b;
        }

        /// <summary>A triangle with a morph target, an animation, a camera and extras: everything here is stripped.</summary>
        public static BasisGlbTestBuilder AnimatedMorphed()
        {
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", TrianglePositions);
            int target = b.AddFloats("VEC3", 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f);
            int times = b.AddFloats("SCALAR", 0f, 1f);
            int values = b.AddFloats("VEC3", 0f, 0f, 0f, 0f, 1f, 0f);
            b.Meshes.Add("{\"primitives\":[" + Primitive(position, -1, null, -1, "\"targets\":[{\"POSITION\":" + target + "}]")
                + "],\"weights\":[0.5],\"extras\":{\"targetNames\":[\"a\"]}}");
            int mesh = b.Meshes.Count - 1;
            b.AddNode("{\"mesh\":" + mesh + ",\"camera\":0,\"extras\":{\"note\":1}}");
            b.ExtraRootMembers = "\"animations\":[{\"channels\":[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"translation\"}}],\"samplers\":[{\"input\":"
                + times + ",\"output\":" + values + "}]}],\"cameras\":[{\"type\":\"perspective\",\"perspective\":{\"yfov\":1,\"znear\":0.1}}]";
            return b;
        }
    }
}
