using System.Collections.Generic;

namespace Basis.ModelPickup.Validation
{
    // Whitelisted glTF model, bound from JSON by BasisGltfJsonBinder. Only fields that the canonical output can
    // carry, or that a check needs, are kept; everything else is skipped (and flagged) at bind time. Indices are
    // source indices; -1 means absent. Names, generator and copyright are never kept: the canonical output has none.

    public static class BasisGltfComponent
    {
        public const int Byte = 5120;
        public const int UnsignedByte = 5121;
        public const int Short = 5122;
        public const int UnsignedShort = 5123;
        public const int UnsignedInt = 5125;
        public const int Float = 5126;

        public static int Size(int componentType)
        {
            switch (componentType)
            {
                case Byte:
                case UnsignedByte:
                    return 1;
                case Short:
                case UnsignedShort:
                    return 2;
                case UnsignedInt:
                case Float:
                    return 4;
                default:
                    return 0;
            }
        }
    }

    public static class BasisGltfType
    {
        public const byte None = 0;
        public const byte Scalar = 1;
        public const byte Vec2 = 2;
        public const byte Vec3 = 3;
        public const byte Vec4 = 4;
        public const byte Mat2 = 5;
        public const byte Mat3 = 6;
        public const byte Mat4 = 7;

        public static int Components(byte type)
        {
            switch (type)
            {
                case Scalar: return 1;
                case Vec2: return 2;
                case Vec3: return 3;
                case Vec4: return 4;
                case Mat2: return 4;
                case Mat3: return 9;
                case Mat4: return 16;
                default: return 0;
            }
        }

        public static string Name(byte type)
        {
            switch (type)
            {
                case Scalar: return "SCALAR";
                case Vec2: return "VEC2";
                case Vec3: return "VEC3";
                case Vec4: return "VEC4";
                case Mat2: return "MAT2";
                case Mat3: return "MAT3";
                case Mat4: return "MAT4";
                default: return null;
            }
        }
    }

    public sealed class BasisGltfDocument
    {
        public bool HasAsset;
        public string AssetVersion;
        public string AssetMinVersion;
        public List<string> ExtensionsUsed = new List<string>();
        public List<string> ExtensionsRequired = new List<string>();
        public int Scene = -1;
        public bool HasScene;
        public List<BasisGltfScene> Scenes = new List<BasisGltfScene>();
        public List<BasisGltfNode> Nodes = new List<BasisGltfNode>();
        public List<BasisGltfMesh> Meshes = new List<BasisGltfMesh>();
        public List<BasisGltfAccessor> Accessors = new List<BasisGltfAccessor>();
        public List<BasisGltfBufferView> BufferViews = new List<BasisGltfBufferView>();
        public List<BasisGltfBuffer> Buffers = new List<BasisGltfBuffer>();
        public List<BasisGltfMaterial> Materials = new List<BasisGltfMaterial>();
        public List<BasisGltfTexture> Textures = new List<BasisGltfTexture>();
        public List<BasisGltfImage> Images = new List<BasisGltfImage>();
        public List<BasisGltfSampler> Samplers = new List<BasisGltfSampler>();
        public List<BasisGltfSkin> Skins = new List<BasisGltfSkin>();
        public BasisGlbStripped Stripped;

        /// <summary>The JSON bytes; URI ranges index into this array.</summary>
        public byte[] Json;
    }

    public sealed class BasisGltfBuffer
    {
        public long ByteLength = -1;
        public bool HasUri;
        public int UriStart, UriLength;
        public bool UriHasEscapes;
    }

    public sealed class BasisGltfBufferView
    {
        public int Buffer = -1;
        public long ByteOffset;
        public long ByteLength = -1;
        public int ByteStride = -1;
    }

    public sealed class BasisGltfAccessor
    {
        public int BufferView = -1;
        public long ByteOffset;
        public int ComponentType;
        public bool Normalized;
        public int Count = -1;
        public byte Type;
        public bool HasSparse;
    }

    public sealed class BasisGltfPrimitive
    {
        public int Position = -1, Normal = -1, Tangent = -1, TexCoord0 = -1, TexCoord1 = -1, Color0 = -1;
        public int Joints0 = -1, Weights0 = -1, Indices = -1, Material = -1;
        public int Mode = 4;
        public bool HasTargets, HasExtraUv, HasExtraStreams;
    }

    public sealed class BasisGltfMesh
    {
        public List<BasisGltfPrimitive> Primitives = new List<BasisGltfPrimitive>();
    }

    public sealed class BasisGltfNode
    {
        public int[] Children;
        public int Mesh = -1, Skin = -1;
        public bool HasMatrix, HasT, HasR, HasS;
        public float[] Matrix;
        public float Tx, Ty, Tz;
        public float Rx, Ry, Rz, Rw = 1f;
        public float Sx = 1f, Sy = 1f, Sz = 1f;
    }

    public sealed class BasisGltfSkin
    {
        public int InverseBindMatrices = -1;
        public int Skeleton = -1;
        public int[] Joints;
    }

    public sealed class BasisGltfScene
    {
        public int[] Nodes;
    }

    public struct BasisGltfTextureRef
    {
        public bool Present;
        public int Index;
        public int TexCoord;
        public float ScaleOrStrength;
        public bool HasTransform;
        public float OffsetU, OffsetV, Rotation, ScaleU, ScaleV;
        public int TransformTexCoord;

        public static BasisGltfTextureRef Absent()
        {
            return new BasisGltfTextureRef { Index = -1, ScaleOrStrength = 1f, ScaleU = 1f, ScaleV = 1f, TransformTexCoord = -1 };
        }
    }

    public sealed class BasisGltfMaterial
    {
        public float[] BaseColor = { 1f, 1f, 1f, 1f };
        public float Metallic = 1f, Roughness = 1f, AlphaCutoff = 0.5f;
        public float[] Emissive = { 0f, 0f, 0f };
        public BasisGltfTextureRef BaseColorTex = BasisGltfTextureRef.Absent();
        public BasisGltfTextureRef MetallicRoughnessTex = BasisGltfTextureRef.Absent();
        public BasisGltfTextureRef NormalTex = BasisGltfTextureRef.Absent();
        public BasisGltfTextureRef OcclusionTex = BasisGltfTextureRef.Absent();
        public BasisGltfTextureRef EmissiveTex = BasisGltfTextureRef.Absent();
        /// <summary>0 OPAQUE, 1 MASK, 2 BLEND.</summary>
        public byte AlphaMode;
        public bool DoubleSided, Unlit, HasEmissiveStrength;
        public float EmissiveStrength = 1f;
    }

    public sealed class BasisGltfTexture
    {
        public int Sampler = -1;
        public int Source = -1;
    }

    public sealed class BasisGltfImage
    {
        public int BufferView = -1;
        public string MimeType;
        public bool HasUri;
        public int UriStart, UriLength;
        public bool UriHasEscapes;
    }

    public sealed class BasisGltfSampler
    {
        /// <summary>0 = absent.</summary>
        public int MagFilter, MinFilter;
        public int WrapS = 10497, WrapT = 10497;
    }
}
