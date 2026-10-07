using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// The 88-byte, little-endian summary a sender puts in the spawn tail so receivers can refuse a model before
    /// downloading it. Claims only drive bounded decisions before validation; afterwards <see cref="Verify"/> checks
    /// the receiver's own stats against them.
    /// <code>
    /// 0  u8  FormatVersion (1)          1  u8  Flags (bit0 skins, bit1 quantization, bit2 unlit)
    /// 2  u16 Primitives  4 u16 Nodes  6 u16 Materials  8 u16 Images  10 u16 Joints
    /// 12 i32 Vertices   16 i32 Triangles   20 i32 DrawCalls
    /// 24 i64 TexturePixels   32 i64 EstimatedDecodedBytes
    /// 40 f32 MinX MinY MinZ MaxX MaxY MaxZ (glTF space, metres, before base scale)
    /// 64 i64 RenderedTriangles   72 i64 SkinnedVertexInstances
    /// 80 u16 MeshInstances  82 u16 Textures  84 u16 Skins  86 u16 MaxTextureDimension
    /// </code>
    /// </summary>
    public struct BasisGlbClaims
    {
        public const int EncodedSize = 88;
        public const byte CurrentFormatVersion = 1;
        public const byte FlagHasSkins = 1 << 0;
        public const byte FlagUsesQuantization = 1 << 1;
        public const byte FlagUsesUnlit = 1 << 2;
        private const byte KnownFlags = FlagHasSkins | FlagUsesQuantization | FlagUsesUnlit;

        public byte FormatVersion;
        public byte Flags;
        public ushort Primitives, Nodes, Materials, Images, Joints;
        public int Vertices, Triangles, DrawCalls;
        public long TexturePixels, EstimatedDecodedBytes;
        public BasisGlbAabb Bounds;
        public long RenderedTriangles, SkinnedVertexInstances;
        public ushort MeshInstances, Textures, Skins, MaxTextureDimension;

        public bool HasSkins => (Flags & FlagHasSkins) != 0;
        public bool UsesQuantization => (Flags & FlagUsesQuantization) != 0;
        public bool UsesUnlit => (Flags & FlagUsesUnlit) != 0;

        public static BasisGlbClaims FromStats(in BasisGlbStats stats)
        {
            byte flags = 0;
            if (stats.HasSkins) flags |= FlagHasSkins;
            if (stats.UsesQuantization) flags |= FlagUsesQuantization;
            if (stats.UsesUnlit) flags |= FlagUsesUnlit;
            // Stats are within limits whose u16 caps TryValidate enforces; clamping only matters for hand-made stats,
            // and Verify then fails them, which is the right outcome.
            return new BasisGlbClaims
            {
                FormatVersion = CurrentFormatVersion,
                Flags = flags,
                Primitives = U16(stats.Primitives),
                Nodes = U16(stats.Nodes),
                Materials = U16(stats.Materials),
                Images = U16(stats.Images),
                Joints = U16(stats.Joints),
                Vertices = Math.Max(0, stats.Vertices),
                Triangles = Math.Max(0, stats.Triangles),
                DrawCalls = Math.Max(0, stats.DrawCalls),
                TexturePixels = Math.Max(0L, stats.TexturePixels),
                EstimatedDecodedBytes = Math.Max(0L, stats.EstimatedDecodedBytes),
                Bounds = stats.Bounds,
                RenderedTriangles = Math.Max(0L, stats.RenderedTriangles),
                SkinnedVertexInstances = Math.Max(0L, stats.SkinnedVertexInstances),
                MeshInstances = U16(stats.MeshInstances),
                Textures = U16(stats.Textures),
                Skins = U16(stats.Skins),
                MaxTextureDimension = U16(stats.MaxTextureDimension),
            };
        }

        private static ushort U16(int value)
        {
            return (ushort)Math.Min(Math.Max(value, 0), ushort.MaxValue);
        }

        /// <summary>Writes the 88 bytes. False when the destination is too short; nothing is written then.</summary>
        public bool TryWrite(Span<byte> destination)
        {
            if (destination.Length < EncodedSize) return false;
            destination[0] = FormatVersion;
            destination[1] = Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), Primitives);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), Nodes);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6), Materials);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8), Images);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(10), Joints);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(12), Vertices);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(16), Triangles);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(20), DrawCalls);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(24), TexturePixels);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(32), EstimatedDecodedBytes);
            WriteSingle(destination.Slice(40), Bounds.MinX);
            WriteSingle(destination.Slice(44), Bounds.MinY);
            WriteSingle(destination.Slice(48), Bounds.MinZ);
            WriteSingle(destination.Slice(52), Bounds.MaxX);
            WriteSingle(destination.Slice(56), Bounds.MaxY);
            WriteSingle(destination.Slice(60), Bounds.MaxZ);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(64), RenderedTriangles);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(72), SkinnedVertexInstances);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(80), MeshInstances);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(82), Textures);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(84), Skins);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(86), MaxTextureDimension);
            return true;
        }

        /// <summary>Reads 88 bytes; trailing bytes are ignored. Rejects unknown versions and flags, negatives, non-finite or inverted bounds.</summary>
        public static bool TryRead(ReadOnlySpan<byte> source, out BasisGlbClaims claims, out string error)
        {
            claims = default;
            error = null;
            if (source.Length < EncodedSize)
            {
                error = "Model claims are " + source.Length.ToString(CultureInfo.InvariantCulture) + " bytes; "
                    + EncodedSize.ToString(CultureInfo.InvariantCulture) + " are required.";
                return false;
            }
            var read = new BasisGlbClaims
            {
                FormatVersion = source[0],
                Flags = source[1],
                Primitives = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2)),
                Nodes = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4)),
                Materials = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6)),
                Images = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(8)),
                Joints = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(10)),
                Vertices = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(12)),
                Triangles = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(16)),
                DrawCalls = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(20)),
                TexturePixels = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(24)),
                EstimatedDecodedBytes = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(32)),
                Bounds = new BasisGlbAabb
                {
                    MinX = ReadSingle(source.Slice(40)),
                    MinY = ReadSingle(source.Slice(44)),
                    MinZ = ReadSingle(source.Slice(48)),
                    MaxX = ReadSingle(source.Slice(52)),
                    MaxY = ReadSingle(source.Slice(56)),
                    MaxZ = ReadSingle(source.Slice(60)),
                },
                RenderedTriangles = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(64)),
                SkinnedVertexInstances = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(72)),
                MeshInstances = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(80)),
                Textures = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(82)),
                Skins = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(84)),
                MaxTextureDimension = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(86)),
            };
            if (!read.TryCheckWellFormed(out error)) return false;
            claims = read;
            return true;
        }

        private bool TryCheckWellFormed(out string error)
        {
            error = null;
            if (FormatVersion != CurrentFormatVersion)
            {
                error = "Model claims use format version " + FormatVersion.ToString(CultureInfo.InvariantCulture)
                    + "; only version 1 is supported.";
                return false;
            }
            if ((Flags & ~KnownFlags) != 0)
            {
                error = "Model claims set unknown flag bits.";
                return false;
            }
            if (Vertices < 0 || Triangles < 0 || DrawCalls < 0 || TexturePixels < 0 || EstimatedDecodedBytes < 0
                || RenderedTriangles < 0 || SkinnedVertexInstances < 0)
            {
                error = "Model claims contain a negative count.";
                return false;
            }
            if (!Bounds.IsFinite())
            {
                error = "Model claims contain non-finite bounds.";
                return false;
            }
            if (!(Bounds.MinX <= Bounds.MaxX) || !(Bounds.MinY <= Bounds.MaxY) || !(Bounds.MinZ <= Bounds.MaxZ))
            {
                error = "Model claims contain inverted bounds.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Header-time admission against the receiver's tier, before anything is reserved or allocated. Every field
        /// is checked: each count against its limit, the counts every renderable model has, skin consistency, and
        /// the bounds rule shared with the validator (flat models allowed; extent and origin distance capped).
        /// </summary>
        public bool TryAdmit(int wireBytes, in BasisModelLimits limits, out string error)
        {
            if (!TryCheckWellFormed(out error)) return false;
            if (wireBytes <= 0 || wireBytes > limits.MaxModelBytes)
            {
                error = wireBytes <= 0
                    ? "The model is empty."
                    : BasisGlbErrors.Bytes("Model", wireBytes, limits.MaxModelBytes);
                return false;
            }
            long maxJoints = (long)limits.MaxSkins * limits.MaxJointsPerSkin;
            if (!Check("Primitive count", Primitives, limits.MaxPrimitives, out error)
                || !Check("Node count", Nodes, limits.MaxNodes, out error)
                || !Check("Material count", Materials, limits.MaxMaterials, out error)
                || !Check("Image count", Images, limits.MaxImages, out error)
                || !Check("Joint count", Joints, maxJoints, out error)
                || !Check("Vertex count", Vertices, limits.MaxVertices, out error)
                || !Check("Triangle count", Triangles, limits.MaxTriangles, out error)
                || !Check("Draw call count", DrawCalls, limits.MaxDrawCalls, out error)
                || !Check("Texture pixel count", TexturePixels, limits.MaxTotalTexturePixels, out error)
                || !Check("Rendered triangle count", RenderedTriangles, limits.MaxRenderedTriangles, out error)
                || !Check("Skinned vertex instance count", SkinnedVertexInstances, limits.MaxSkinnedVertexInstances, out error)
                || !Check("Mesh instance count", MeshInstances, limits.MaxMeshInstances, out error)
                || !Check("Texture count", Textures, limits.MaxTextures, out error)
                || !Check("Skin count", Skins, limits.MaxSkins, out error)
                || !Check("Texture dimension", MaxTextureDimension, limits.MaxTextureDimension, out error))
            {
                return false;
            }
            if (EstimatedDecodedBytes > limits.MaxEstimatedDecodedBytes)
            {
                error = BasisGlbErrors.Bytes("Estimated decoded memory", EstimatedDecodedBytes, limits.MaxEstimatedDecodedBytes);
                return false;
            }
            if (Triangles < 1 || Vertices < 3 || Primitives < 1 || Nodes < 1 || MeshInstances < 1 || DrawCalls < 1)
            {
                error = "Model claims describe nothing to display.";
                return false;
            }
            if (HasSkins != (Skins > 0) || Joints < Skins || Joints > (long)Skins * limits.MaxJointsPerSkin)
            {
                error = "Model claims describe inconsistent skins.";
                return false;
            }
            return Bounds.TryCheckLimits(limits, out error);
        }

        private static bool Check(string label, long value, long maximum, out string error)
        {
            if (value > maximum)
            {
                error = BasisGlbErrors.Limit(label, value, maximum);
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>After validation: every actual count must be at or below its claim, the flags equal, and the bounds approximately equal.</summary>
        public bool Verify(in BasisGlbStats actual, out string error)
        {
            if (!AtMost("Primitive count", actual.Primitives, Primitives, out error)
                || !AtMost("Node count", actual.Nodes, Nodes, out error)
                || !AtMost("Material count", actual.Materials, Materials, out error)
                || !AtMost("Image count", actual.Images, Images, out error)
                || !AtMost("Joint count", actual.Joints, Joints, out error)
                || !AtMost("Vertex count", actual.Vertices, Vertices, out error)
                || !AtMost("Triangle count", actual.Triangles, Triangles, out error)
                || !AtMost("Draw call count", actual.DrawCalls, DrawCalls, out error)
                || !AtMost("Texture pixel count", actual.TexturePixels, TexturePixels, out error)
                || !AtMost("Estimated decoded bytes", actual.EstimatedDecodedBytes, EstimatedDecodedBytes, out error)
                || !AtMost("Rendered triangle count", actual.RenderedTriangles, RenderedTriangles, out error)
                || !AtMost("Skinned vertex instance count", actual.SkinnedVertexInstances, SkinnedVertexInstances, out error)
                || !AtMost("Mesh instance count", actual.MeshInstances, MeshInstances, out error)
                || !AtMost("Texture count", actual.Textures, Textures, out error)
                || !AtMost("Skin count", actual.Skins, Skins, out error)
                || !AtMost("Texture dimension", actual.MaxTextureDimension, MaxTextureDimension, out error))
            {
                return false;
            }
            if (actual.HasSkins != HasSkins || actual.UsesQuantization != UsesQuantization || actual.UsesUnlit != UsesUnlit)
            {
                error = "The model's feature flags do not match its claims.";
                return false;
            }
            if (!Bounds.ApproximatelyEquals(actual.Bounds))
            {
                error = "The model's bounds do not match its claims.";
                return false;
            }
            error = null;
            return true;
        }

        private static bool AtMost(string label, long actual, long claimed, out string error)
        {
            if (actual > claimed)
            {
                error = label + " is " + BasisGlbErrors.N(actual) + " but the sender claimed " + BasisGlbErrors.N(claimed) + ".";
                return false;
            }
            error = null;
            return true;
        }

        private static void WriteSingle(Span<byte> destination, float value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));
        }

        private static float ReadSingle(ReadOnlySpan<byte> source)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
        }
    }
}
