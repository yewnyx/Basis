using System;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Per-model limits the validator enforces. The sender validates with <see cref="Desktop"/>, the largest
    /// tier any receiver accepts; each receiver validates with <see cref="ForDevice"/>. Aggregate budgets
    /// (per sender, resident, inbound reservations) belong to the runtime, not here.
    /// </summary>
    public struct BasisModelLimits
    {
        private const long MiB = 1024L * 1024L;

        // Bytes.
        public long MaxSourceBytes;             // the dropped .glb, or the whole .gltf text including base64
        public long MaxModelBytes;              // canonical GLB = wire bytes
        public long MaxSourceImageBytes;        // one embedded source image, before sanitising
        public long MaxImageBytes;              // one final (stripped) PNG
        public long MaxEstimatedDecodedBytes;

        // JSON reader bounds.
        public int MaxJsonBytes;
        public int MaxJsonDepth;
        public int MaxJsonTokens;
        public int MaxJsonStringBytes;
        public int MaxJsonKeyBytes;
        public int MaxJsonObjectKeys;
        public int MaxJsonNumberChars;
        public int MaxRawArrayLength;
        public int MaxDataUris;

        // Retained structure.
        public int MaxNodes;
        public int MaxNodeDepth;
        public int MaxMeshes;
        public int MaxPrimitives;
        public int MaxMeshInstances;
        public int MaxDrawCalls;
        public int MaxMaterials;
        public int MaxTextures;
        public int MaxImages;
        public int MaxSamplers;
        public int MaxAccessors;
        public int MaxAccessorElements;
        public int MaxSkins;
        public int MaxJointsPerSkin;

        // Geometry.
        public int MaxVertices;
        public int MaxTriangles;
        public long MaxRenderedTriangles;
        public long MaxSkinnedVertexInstances;

        // Textures.
        public int MaxTextureDimension;
        public int MaxSourceTextureDimension;
        public long MaxTexturePixelsPerImage;
        public long MaxSourceTexturePixels;
        public long MaxTotalTexturePixels;

        // Space (metres, glTF axes).
        public float MaxBoundsExtentMeters;
        public float MinBoundsExtentMeters;
        public float MaxBoundsAbsCoordinateMeters;
        public float MaxNodeTranslation;
        public float MaxNodeScale;
        public float MaxWorldMatrixAbs;

        public static readonly BasisModelLimits Desktop = CreateDesktop();
        public static readonly BasisModelLimits Mobile = CreateMobile();

        /// <summary>Mobile GPUs and devices with known memory of 4 GB or less get <see cref="Mobile"/>; unknown memory (0) does not.</summary>
        public static BasisModelLimits ForDevice(bool isMobileGpu, int systemMemoryMegabytes)
        {
            return isMobileGpu || (systemMemoryMegabytes > 0 && systemMemoryMegabytes <= 4096) ? Mobile : Desktop;
        }

        private static BasisModelLimits CreateDesktop()
        {
            return new BasisModelLimits
            {
                MaxSourceBytes = 64 * MiB,
                MaxModelBytes = 32 * MiB,
                MaxSourceImageBytes = 32 * MiB,
                MaxImageBytes = 8 * MiB,
                MaxEstimatedDecodedBytes = 256 * MiB,

                MaxJsonBytes = (int)(4 * MiB),
                MaxJsonDepth = 32,
                MaxJsonTokens = 1000000,
                MaxJsonStringBytes = 4096,
                MaxJsonKeyBytes = 256,
                MaxJsonObjectKeys = 256,
                MaxJsonNumberChars = 64,
                MaxRawArrayLength = 65536,
                MaxDataUris = 64,

                MaxNodes = 2048,
                MaxNodeDepth = 128,
                MaxMeshes = 256,
                MaxPrimitives = 512,
                MaxMeshInstances = 512,
                MaxDrawCalls = 1024,
                MaxMaterials = 64,
                MaxTextures = 32,
                MaxImages = 32,
                MaxSamplers = 32,
                MaxAccessors = 4096,
                MaxAccessorElements = 4194304,
                MaxSkins = 8,
                MaxJointsPerSkin = 256,

                MaxVertices = 500000,
                MaxTriangles = 500000,
                MaxRenderedTriangles = 1000000,
                MaxSkinnedVertexInstances = 1000000,

                MaxTextureDimension = 2048,
                MaxSourceTextureDimension = 4096,
                MaxTexturePixelsPerImage = 2048L * 2048L,
                MaxSourceTexturePixels = 4096L * 4096L,
                MaxTotalTexturePixels = 4096L * 4096L,

                MaxBoundsExtentMeters = 1e4f,
                MinBoundsExtentMeters = 1e-4f,
                MaxBoundsAbsCoordinateMeters = 1e5f,
                MaxNodeTranslation = 1e7f,
                MaxNodeScale = 1e6f,
                MaxWorldMatrixAbs = 1e9f,
            };
        }

        private static BasisModelLimits CreateMobile()
        {
            BasisModelLimits limits = CreateDesktop();
            limits.MaxModelBytes = 16 * MiB;
            limits.MaxEstimatedDecodedBytes = 96 * MiB;
            limits.MaxMeshInstances = 128;
            limits.MaxDrawCalls = 256;
            limits.MaxVertices = 150000;
            limits.MaxTriangles = 150000;
            limits.MaxRenderedTriangles = 300000;
            limits.MaxSkinnedVertexInstances = 300000;
            limits.MaxTotalTexturePixels = 2048L * 4096L;
            return limits;
        }

        /// <summary>
        /// Every entry point calls this first. The u16 caps exist because <see cref="BasisGlbClaims"/> carries
        /// those counts as u16; the reader caps bound the reader's own scratch allocation.
        /// </summary>
        public bool TryValidate(out string error)
        {
            error = null;
            if (MaxSourceBytes <= 0 || MaxModelBytes <= 0 || MaxSourceImageBytes <= 0 || MaxImageBytes <= 0
                || MaxEstimatedDecodedBytes <= 0)
            {
                error = "Model limits: every byte limit must be positive.";
                return false;
            }
            if (MaxJsonBytes <= 0 || MaxJsonDepth <= 0 || MaxJsonTokens <= 0 || MaxJsonStringBytes <= 0
                || MaxJsonKeyBytes <= 0 || MaxJsonObjectKeys <= 0 || MaxJsonNumberChars <= 0
                || MaxRawArrayLength <= 0 || MaxDataUris <= 0)
            {
                error = "Model limits: every JSON limit must be positive.";
                return false;
            }
            if (MaxNodes <= 0 || MaxNodeDepth <= 0 || MaxMeshes <= 0 || MaxPrimitives <= 0 || MaxMeshInstances <= 0
                || MaxDrawCalls <= 0 || MaxMaterials <= 0 || MaxTextures <= 0 || MaxImages <= 0 || MaxSamplers <= 0
                || MaxAccessors <= 0 || MaxAccessorElements <= 0 || MaxSkins <= 0 || MaxJointsPerSkin <= 0)
            {
                error = "Model limits: every structure limit must be positive.";
                return false;
            }
            if (MaxVertices <= 0 || MaxTriangles <= 0 || MaxRenderedTriangles <= 0 || MaxSkinnedVertexInstances <= 0
                || MaxTextureDimension <= 0 || MaxSourceTextureDimension <= 0 || MaxTexturePixelsPerImage <= 0
                || MaxSourceTexturePixels <= 0 || MaxTotalTexturePixels <= 0)
            {
                error = "Model limits: every geometry and texture limit must be positive.";
                return false;
            }
            if (!(MaxBoundsExtentMeters > 0f) || !(MinBoundsExtentMeters > 0f) || !(MaxBoundsAbsCoordinateMeters > 0f)
                || !(MaxNodeTranslation > 0f) || !(MaxNodeScale > 0f) || !(MaxWorldMatrixAbs > 0f)
                || float.IsInfinity(MaxBoundsExtentMeters) || float.IsInfinity(MaxBoundsAbsCoordinateMeters)
                || float.IsInfinity(MaxNodeTranslation) || float.IsInfinity(MaxNodeScale)
                || float.IsInfinity(MaxWorldMatrixAbs) || !(MinBoundsExtentMeters <= MaxBoundsExtentMeters))
            {
                error = "Model limits: spatial limits must be finite and positive, with the minimum extent below the maximum.";
                return false;
            }
            if (MaxNodes > ushort.MaxValue || MaxPrimitives > ushort.MaxValue || MaxMaterials > ushort.MaxValue
                || MaxImages > ushort.MaxValue || MaxMeshInstances > ushort.MaxValue || MaxTextures > ushort.MaxValue
                || MaxSkins > ushort.MaxValue || MaxTextureDimension > 16384
                || (long)MaxSkins * MaxJointsPerSkin > ushort.MaxValue)
            {
                error = "Model limits: counts carried as 16-bit claims must stay at or below 65,535, and textures at or below 16,384 px.";
                return false;
            }
            if (MaxModelBytes > int.MaxValue - 64 || MaxSourceBytes > int.MaxValue || MaxJsonBytes > MaxModelBytes
                || MaxImageBytes > MaxModelBytes)
            {
                error = "Model limits: the model must fit a byte array, and the JSON and image caps must not exceed it.";
                return false;
            }
            if (MaxJsonDepth > 256 || MaxJsonObjectKeys > 4096 || MaxJsonKeyBytes > 4096 || MaxJsonNumberChars > 64)
            {
                error = "Model limits: JSON reader bounds are too large (depth ≤ 256, keys ≤ 4,096, key bytes ≤ 4,096, number characters ≤ 64).";
                return false;
            }
            return true;
        }
    }
}
