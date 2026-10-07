using System;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The handful of <c>UnityEngine.Mathf</c> functions the engine-free transport relies on, copied with their
    /// exact semantics (NaN handling, banker's rounding, clamping order) so it produces the same floats the
    /// engine would.
    /// </summary>
    public static class BasisModelMath
    {
        /// <summary>Exponential approach rate a remote follower uses toward its latest network target, per second.</summary>
        public const float RemoteTransformSmoothingRate = 12f;

        /// <summary>As Mathf.Clamp: a NaN value passes through unchanged.</summary>
        public static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        public static float Clamp01(float value)
        {
            if (value < 0f)
                return 0f;
            if (value > 1f)
                return 1f;
            return value;
        }

        public static float Max(float a, float b)
        {
            return a > b ? a : b;
        }

        public static float Min(float a, float b)
        {
            return a < b ? a : b;
        }

        public static int Max(int a, int b)
        {
            return a > b ? a : b;
        }

        public static int Min(int a, int b)
        {
            return a < b ? a : b;
        }

        /// <summary>Rounds half to even, exactly as Mathf.RoundToInt does (2.5 → 2, 3.5 → 4).</summary>
        public static int RoundToInt(float value)
        {
            return (int)Math.Round(value);
        }

        public static int CeilToInt(float value)
        {
            return (int)Math.Ceiling(value);
        }

        public static int FloorToInt(float value)
        {
            return (int)Math.Floor(value);
        }

        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Clamp01(t);
        }

        /// <summary>
        /// Blend factor that moves a remote pickup the same distance toward its target per second regardless
        /// of frame rate: two half-length steps compound to exactly one full step. Negative deltas give 0.
        /// </summary>
        public static float RemoteTransformLerpFactor(float deltaTime)
        {
            return 1f - (float)Math.Exp(-RemoteTransformSmoothingRate * Max(0f, deltaTime));
        }
    }
}
