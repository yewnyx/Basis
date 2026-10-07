using System;

namespace Basis.ModelPickup
{
    /// <summary>Bounds applied to remote poses and scales.</summary>
    public readonly struct BasisModelPoseLimits
    {
        public readonly float MaxAbsPositionMeters;
        public readonly float MinScale;
        public readonly float MaxScale;

        public BasisModelPoseLimits(float maxAbsPositionMeters, float minScale, float maxScale)
        {
            MaxAbsPositionMeters = maxAbsPositionMeters;
            MinScale = minScale;
            MaxScale = maxScale;
        }
    }

    /// <summary>
    /// Validation for every remote pose and scale (spawn, offer, transform) before it reaches a transform.
    /// A NaN or infinite component written into a Transform poisons physics and rendering for everyone
    /// nearby, so these are rejected where the data enters rather than guarded at each use.
    /// </summary>
    public static class BasisModelPoseValidation
    {
        private const float MinRotationLengthSquared = 1e-8f;
        private const float UnitLengthTolerance = 1e-3f;

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// False, leaving <paramref name="pose"/> untouched, if any component is non-finite, any position
        /// component is beyond <see cref="BasisModelPoseLimits.MaxAbsPositionMeters"/>, or the rotation is
        /// degenerate. A rotation is renormalised only when it is visibly off unit length, so legitimate
        /// quaternions pass through bit-identical.
        /// </summary>
        public static bool TrySanitizePose(ref BasisModelPose pose, in BasisModelPoseLimits limits)
        {
            float maxAbs = limits.MaxAbsPositionMeters;
            if (!IsWithin(pose.Position.X, maxAbs) || !IsWithin(pose.Position.Y, maxAbs) || !IsWithin(pose.Position.Z, maxAbs))
                return false;

            BasisModelQuat rotation = pose.Rotation;
            if (!IsFinite(rotation.X) || !IsFinite(rotation.Y) || !IsFinite(rotation.Z) || !IsFinite(rotation.W))
                return false;

            // Huge finite components overflow to infinity here, which is as unusable as a NaN.
            float lengthSquared = rotation.LengthSquared;
            if (!IsFinite(lengthSquared) || !(lengthSquared >= MinRotationLengthSquared))
                return false;

            if (Math.Abs(lengthSquared - 1f) > UnitLengthTolerance)
            {
                float inverseLength = 1f / MathF.Sqrt(lengthSquared);
                rotation.X *= inverseLength;
                rotation.Y *= inverseLength;
                rotation.Z *= inverseLength;
                rotation.W *= inverseLength;
                pose.Rotation = rotation;
            }
            return true;
        }

        /// <summary>False for a non-finite or non-positive scale; otherwise clamps it into the limits' range.</summary>
        public static bool TrySanitizeScale(ref float scale, in BasisModelPoseLimits limits)
        {
            if (!IsFinite(scale) || scale <= 0f)
                return false;
            scale = BasisModelMath.Clamp(scale, limits.MinScale, limits.MaxScale);
            return true;
        }

        // Written so a NaN limit rejects rather than admits.
        private static bool IsWithin(float value, float maxAbs)
        {
            return IsFinite(value) && Math.Abs(value) <= maxAbs;
        }
    }
}
