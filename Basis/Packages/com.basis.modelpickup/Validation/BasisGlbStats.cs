using System;

namespace Basis.ModelPickup.Validation
{
    /// <summary>Axis-aligned bounds in glTF space (right-handed, +Y up, metres), relative to the scene root, before base scale.</summary>
    public struct BasisGlbAabb
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public float SizeX => MaxX - MinX;
        public float SizeY => MaxY - MinY;
        public float SizeZ => MaxZ - MinZ;
        public float MaxExtent => Math.Max(SizeX, Math.Max(SizeY, SizeZ));
        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterY => (MinY + MaxY) * 0.5f;
        public float CenterZ => (MinZ + MaxZ) * 0.5f;

        /// <summary>glTFast negates X when it converts glTF space to Unity space (NodeExtension, Accessor.TryGetBounds).</summary>
        public BasisGlbAabb ToUnitySpace()
        {
            return new BasisGlbAabb
            {
                MinX = -MaxX,
                MinY = MinY,
                MinZ = MinZ,
                MaxX = -MinX,
                MaxY = MaxY,
                MaxZ = MaxZ,
            };
        }

        /// <summary>
        /// Per-bound |a - b| ≤ 1e-5·max(1, extent) + 1e-6. Sender and receiver compute bounds with the same code from the
        /// same canonical floats, so they normally match exactly; the tolerance only absorbs float formatting in claims.
        /// NaN on either side never compares equal.
        /// </summary>
        public bool ApproximatelyEquals(in BasisGlbAabb other)
        {
            double extent = Math.Max(1d, Math.Max((double)MaxExtent, other.MaxExtent));
            double tolerance = 1e-5 * extent + 1e-6;
            return BasisGlbNumbers.Within(Math.Abs((double)MinX - other.MinX), tolerance)
                && BasisGlbNumbers.Within(Math.Abs((double)MinY - other.MinY), tolerance)
                && BasisGlbNumbers.Within(Math.Abs((double)MinZ - other.MinZ), tolerance)
                && BasisGlbNumbers.Within(Math.Abs((double)MaxX - other.MaxX), tolerance)
                && BasisGlbNumbers.Within(Math.Abs((double)MaxY - other.MaxY), tolerance)
                && BasisGlbNumbers.Within(Math.Abs((double)MaxZ - other.MaxZ), tolerance);
        }

        public bool IsFinite()
        {
            return BasisGlbNumbers.IsFinite(MinX) && BasisGlbNumbers.IsFinite(MinY) && BasisGlbNumbers.IsFinite(MinZ)
                && BasisGlbNumbers.IsFinite(MaxX) && BasisGlbNumbers.IsFinite(MaxY) && BasisGlbNumbers.IsFinite(MaxZ);
        }

        /// <summary>
        /// The shared bounds rule (the validator's bounds pass and <see cref="BasisGlbClaims.TryAdmit"/>): flat models are fine, so only
        /// the largest extent has a minimum; every axis is in [0, max]; every coordinate is within the absolute cap.
        /// </summary>
        public bool TryCheckLimits(in BasisModelLimits limits, out string error)
        {
            error = null;
            if (!IsFinite())
            {
                error = "The model's bounds are not finite.";
                return false;
            }
            double sizeX = (double)MaxX - MinX;
            double sizeY = (double)MaxY - MinY;
            double sizeZ = (double)MaxZ - MinZ;
            if (!(sizeX >= 0d) || !(sizeY >= 0d) || !(sizeZ >= 0d))
            {
                error = "The model's bounds are inverted.";
                return false;
            }
            double maxExtent = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
            if (!BasisGlbNumbers.Within(maxExtent, limits.MaxBoundsExtentMeters))
            {
                error = "The model is " + BasisGlbErrors.Meters(maxExtent) + " across. The maximum is "
                    + BasisGlbErrors.Meters(limits.MaxBoundsExtentMeters) + ".";
                return false;
            }
            if (!(maxExtent >= limits.MinBoundsExtentMeters))
            {
                error = "The model's bounds are empty.";
                return false;
            }
            double cap = limits.MaxBoundsAbsCoordinateMeters;
            if (!BasisGlbNumbers.Within(Math.Abs((double)MinX), cap) || !BasisGlbNumbers.Within(Math.Abs((double)MinY), cap)
                || !BasisGlbNumbers.Within(Math.Abs((double)MinZ), cap) || !BasisGlbNumbers.Within(Math.Abs((double)MaxX), cap)
                || !BasisGlbNumbers.Within(Math.Abs((double)MaxY), cap) || !BasisGlbNumbers.Within(Math.Abs((double)MaxZ), cap))
            {
                error = "The model lies more than " + BasisGlbErrors.Meters(cap) + " from its origin.";
                return false;
            }
            return true;
        }
    }

    public struct BasisGlbStats
    {
        /// <summary>Whole canonical GLB; JSON and BIN are the padded chunk lengths.</summary>
        public int CanonicalBytes, JsonBytes, BinBytes;
        public int Nodes, Meshes, Primitives, Materials, Textures, Images, Samplers, Skins, Joints, Accessors;
        /// <summary>Canonical nodes with a mesh.</summary>
        public int MeshInstances;
        public int SkinnedMeshInstances;
        /// <summary>Σ over mesh instances of the mesh's primitive count (one submesh each).</summary>
        public int DrawCalls;
        /// <summary>Σ of unique glTFast vertex buffers (validator §13.1).</summary>
        public int Vertices;
        /// <summary>Σ over canonical primitives of index count / 3.</summary>
        public int Triangles;
        public long RenderedTriangles;
        public long SkinnedVertexInstances;
        /// <summary>Σ images w·h·distinct sampler keys (glTFast clones an image per extra sampler key).</summary>
        public long TexturePixels;
        public int MaxTextureDimension;
        public long EstimatedDecodedBytes, EstimatedPeakBytes;
        /// <summary>Vertices glTFast recalculates on the main thread (MeshGenerator RecalculateNormals/Tangents).</summary>
        public int RecalculatedNormalVertices, RecalculatedTangentVertices;
        public bool HasSkins, UsesQuantization, UsesUnlit, UsesTextureTransform;
        public BasisGlbAabb Bounds;

        /// <summary>Field-for-field equality, used by the sender self-check. Floats compare by value.</summary>
        public static bool AreEqual(in BasisGlbStats a, in BasisGlbStats b)
        {
            return a.CanonicalBytes == b.CanonicalBytes && a.JsonBytes == b.JsonBytes && a.BinBytes == b.BinBytes
                && a.Nodes == b.Nodes && a.Meshes == b.Meshes && a.Primitives == b.Primitives
                && a.Materials == b.Materials && a.Textures == b.Textures && a.Images == b.Images
                && a.Samplers == b.Samplers && a.Skins == b.Skins && a.Joints == b.Joints && a.Accessors == b.Accessors
                && a.MeshInstances == b.MeshInstances && a.SkinnedMeshInstances == b.SkinnedMeshInstances
                && a.DrawCalls == b.DrawCalls && a.Vertices == b.Vertices && a.Triangles == b.Triangles
                && a.RenderedTriangles == b.RenderedTriangles && a.SkinnedVertexInstances == b.SkinnedVertexInstances
                && a.TexturePixels == b.TexturePixels && a.MaxTextureDimension == b.MaxTextureDimension
                && a.EstimatedDecodedBytes == b.EstimatedDecodedBytes && a.EstimatedPeakBytes == b.EstimatedPeakBytes
                && a.RecalculatedNormalVertices == b.RecalculatedNormalVertices
                && a.RecalculatedTangentVertices == b.RecalculatedTangentVertices
                && a.HasSkins == b.HasSkins && a.UsesQuantization == b.UsesQuantization && a.UsesUnlit == b.UsesUnlit
                && a.UsesTextureTransform == b.UsesTextureTransform
                && a.Bounds.MinX == b.Bounds.MinX && a.Bounds.MinY == b.Bounds.MinY && a.Bounds.MinZ == b.Bounds.MinZ
                && a.Bounds.MaxX == b.Bounds.MaxX && a.Bounds.MaxY == b.Bounds.MaxY && a.Bounds.MaxZ == b.Bounds.MaxZ;
        }
    }

    /// <summary>NaN-safe comparisons: every limit check is written as "must be within", so NaN fails it.</summary>
    public static class BasisGlbNumbers
    {
        public static bool Within(double value, double maximum)
        {
            return value <= maximum;
        }

        public static bool IsFinite(float value)
        {
            return (BitConverter.SingleToInt32Bits(value) & 0x7F800000) != 0x7F800000;
        }

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Normalises -0 to +0 so equal values always format and hash the same.</summary>
        public static float PositiveZero(float value)
        {
            return value == 0f ? 0f : value;
        }
    }
}
