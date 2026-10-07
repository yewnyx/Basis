using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Base scale and shape maths. "Largest dimension" L is <see cref="BasisGlbAabb.MaxExtent"/>: the longest side of
    /// the validated glTF-space bounds, in metres, before any scale. Every comparison is written so NaN fails it.
    /// </summary>
    public static class BasisModelSizing
    {
        public const float FitLargestDimensionMeters = 0.5f;
        public const float OriginalMinLargestDimensionMeters = 0.02f;
        public const float OriginalMaxLargestDimensionMeters = 4f;

        /// <summary>
        /// The range of L the validator accepts in both tiers (MinBoundsExtentMeters, MaxBoundsExtentMeters). Outside
        /// it there is no meaningful scale, and every division below stays finite inside it.
        /// </summary>
        public const float MinUsableLargestDimensionMeters = 1e-4f;
        public const float MaxUsableLargestDimensionMeters = 1e4f;

        /// <summary>
        /// Thinnest collider or placeholder axis. Flat models (posters, decals) are valid and have a zero axis, and a
        /// zero-thickness box cannot be grabbed reliably.
        /// </summary>
        public const float MinShapeAxisMeters = 0.01f;

        public const float MaxRemotePositionMeters = 1e5f;

        /// <summary>User (gesture) scale range on the root: the interactable's 10–1000 %.</summary>
        public const float MinUserScale = 0.1f;
        public const float MaxUserScale = 10f;

        /// <summary>Applied to every remote spawn, offer and transform pose. The server cache uses the same position bound.</summary>
        public static readonly BasisModelPoseLimits RemoteLimits =
            new BasisModelPoseLimits(MaxRemotePositionMeters, MinUserScale, MaxUserScale);

        public static bool IsUsableLargestDimension(float largestDimension)
        {
            return largestDimension >= MinUsableLargestDimensionMeters && largestDimension <= MaxUsableLargestDimensionMeters;
        }

        /// <summary>
        /// Fit: the largest side becomes 0.5 m. Original: authored size (scale 1), clamped so the largest side stays
        /// within 2 cm to 4 m. An unusable L gives 1, since there is nothing to scale against; such bounds never pass
        /// validation or admission.
        /// </summary>
        public static float ComputeBaseScale(BasisModelSizeMode mode, float largestDimension)
        {
            if (!IsUsableLargestDimension(largestDimension))
                return 1f;
            if (mode == BasisModelSizeMode.Original)
            {
                float min = OriginalMinLargestDimensionMeters / largestDimension;
                float max = OriginalMaxLargestDimensionMeters / largestDimension;
                return 1f < min ? min : 1f > max ? max : 1f;
            }
            return FitLargestDimensionMeters / largestDimension;
        }

        /// <summary>
        /// Forces a received base scale into what an honest sender could have chosen: L·s within 2 cm to 4 m. A NaN,
        /// infinite or non-positive scale becomes Fit. Honest Fit and Original values come back bit-identical, because
        /// the bounds are the same divisions <see cref="ComputeBaseScale"/> performs.
        /// </summary>
        public static float ClampBaseScale(float baseScale, float largestDimension)
        {
            if (!IsUsableLargestDimension(largestDimension))
                return 1f;
            if (!(baseScale > 0f) || float.IsInfinity(baseScale))
                return FitLargestDimensionMeters / largestDimension;
            float min = OriginalMinLargestDimensionMeters / largestDimension;
            if (baseScale < min)
                return min;
            float max = OriginalMaxLargestDimensionMeters / largestDimension;
            if (baseScale > max)
                return max;
            return baseScale;
        }

        /// <summary>
        /// Local position of the glTFast holder under the pickup root that puts the model's bounds centre on the root
        /// origin: −(Unity-space centre)·baseScale. glTFast negates X, so the glTF centre (cx, cy, cz) is (−cx, cy, cz) in
        /// Unity and the holder sits at (cx, −cy, −cz)·baseScale.
        /// </summary>
        public static void HolderLocalPosition(in BasisGlbAabb gltf, float baseScale, out float x, out float y, out float z)
        {
            x = gltf.CenterX * baseScale;
            y = -gltf.CenterY * baseScale;
            z = -gltf.CenterZ * baseScale;
        }

        /// <summary>
        /// One axis of the trigger box and placeholder for a side of <paramref name="sizeMeters"/>: size·baseScale, at
        /// least <see cref="MinShapeAxisMeters"/> and at most <see cref="OriginalMaxLargestDimensionMeters"/>. NaN gives
        /// the minimum.
        /// </summary>
        public static float ShapeAxisMeters(float sizeMeters, float baseScale)
        {
            float axis = sizeMeters * baseScale;
            if (!(axis >= MinShapeAxisMeters))
                return MinShapeAxisMeters;
            return axis > OriginalMaxLargestDimensionMeters ? OriginalMaxLargestDimensionMeters : axis;
        }

        /// <summary>Box and placeholder size per axis, in root-local metres (axis lengths are the same in glTF and Unity space).</summary>
        public static void ShapeSize(in BasisGlbAabb gltf, float baseScale, out float x, out float y, out float z)
        {
            x = ShapeAxisMeters(gltf.SizeX, baseScale);
            y = ShapeAxisMeters(gltf.SizeY, baseScale);
            z = ShapeAxisMeters(gltf.SizeZ, baseScale);
        }
    }
}
