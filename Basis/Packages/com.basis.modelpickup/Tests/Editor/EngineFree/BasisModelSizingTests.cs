using System;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelSizingTests
    {
        private static readonly float[] UsableLargestSides = { 1e-4f, 0.001f, 0.01f, 0.02f, 0.3f, 0.5f, 1f, 2f, 4f, 7.3f, 100f, 1000f, 1e4f };

        [TestCase(0.01f)]
        [TestCase(0.5f)]
        [TestCase(2f)]
        [TestCase(1000f)]
        [TestCase(1e-4f)]
        [TestCase(1e4f)]
        public void FitScalesTheLargestSideToHalfAMetre(float largestSide)
        {
            float scale = BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Fit, largestSide);
            Assert.That(scale * largestSide, Is.EqualTo(0.5f).Within(1e-6f));
        }

        [TestCase(0.02f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(4f)]
        public void OriginalKeepsTheAuthoredSizeInRange(float largestSide)
        {
            Assert.That(BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Original, largestSide), Is.EqualTo(1f));
        }

        [Test]
        public void OriginalClampsTinyModelsTo2CmAndHugeOnesTo4M()
        {
            float tiny = BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Original, 0.001f);
            Assert.That(tiny * 0.001f, Is.EqualTo(0.02f).Within(1e-6f));

            float huge = BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Original, 100f);
            Assert.That(huge * 100f, Is.EqualTo(4f).Within(1e-5f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(5e-5f)]
        [TestCase(2e4f)]
        public void AnUnusableLargestSideGivesUnitScale(float largestSide)
        {
            Assert.That(BasisModelSizing.IsUsableLargestDimension(largestSide), Is.False);
            Assert.That(BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Fit, largestSide), Is.EqualTo(1f));
            Assert.That(BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Original, largestSide), Is.EqualTo(1f));
            Assert.That(BasisModelSizing.ClampBaseScale(0.5f, largestSide), Is.EqualTo(1f));
            Assert.That(BasisModelSizing.ClampBaseScale(float.NaN, largestSide), Is.EqualTo(1f));
        }

        [Test]
        public void ClampBaseScaleIsBitIdenticalForHonestValues()
        {
            foreach (float largestSide in UsableLargestSides)
            {
                foreach (BasisModelSizeMode mode in new[] { BasisModelSizeMode.Fit, BasisModelSizeMode.Original })
                {
                    float honest = BasisModelSizing.ComputeBaseScale(mode, largestSide);
                    float clamped = BasisModelSizing.ClampBaseScale(honest, largestSide);
                    Assert.That(BitConverter.SingleToInt32Bits(clamped), Is.EqualTo(BitConverter.SingleToInt32Bits(honest)),
                        mode + " at " + largestSide);
                }
            }
        }

        [TestCase(1e6f)]
        [TestCase(1e-9f)]
        [TestCase(3f)]
        [TestCase(0.001f)]
        public void ClampBaseScaleForcesALyingScaleIntoTheOriginalRange(float lie)
        {
            const float largestSide = 2f;
            float clamped = BasisModelSizing.ClampBaseScale(lie, largestSide);
            Assert.That(clamped * largestSide, Is.InRange(0.02f - 1e-6f, 4f + 1e-6f));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(0f)]
        [TestCase(-1f)]
        public void ANonFiniteOrNonPositiveScaleClampsToFit(float lie)
        {
            Assert.That(BasisModelSizing.ClampBaseScale(lie, 2f), Is.EqualTo(BasisModelSizing.ComputeBaseScale(BasisModelSizeMode.Fit, 2f)));
        }

        [Test]
        public void ClampBaseScaleAlwaysReturnsAFinitePositiveScale()
        {
            float[] scales = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, float.Epsilon, -float.Epsilon, 0f, 1f, 1e30f, 1e-30f };
            float[] sides = { float.NaN, float.PositiveInfinity, 0f, float.Epsilon, 1e-4f, 1f, 1e4f, float.MaxValue };
            foreach (float scale in scales)
            {
                foreach (float side in sides)
                {
                    float clamped = BasisModelSizing.ClampBaseScale(scale, side);
                    Assert.That(BasisModelPoseValidation.IsFinite(clamped) && clamped > 0f, Is.True, scale + " at " + side);
                }
            }
        }

        [Test]
        public void TheUsableRangeMatchesTheValidatorsBoundsLimits()
        {
            Assert.That(BasisModelSizing.MinUsableLargestDimensionMeters, Is.EqualTo(BasisModelLimits.Desktop.MinBoundsExtentMeters));
            Assert.That(BasisModelSizing.MinUsableLargestDimensionMeters, Is.EqualTo(BasisModelLimits.Mobile.MinBoundsExtentMeters));
            Assert.That(BasisModelSizing.MaxUsableLargestDimensionMeters, Is.EqualTo(BasisModelLimits.Desktop.MaxBoundsExtentMeters));
            Assert.That(BasisModelSizing.MaxUsableLargestDimensionMeters, Is.EqualTo(BasisModelLimits.Mobile.MaxBoundsExtentMeters));
        }

        [Test]
        public void RemoteLimitsArePinned()
        {
            Assert.That(BasisModelSizing.RemoteLimits.MaxAbsPositionMeters, Is.EqualTo(1e5f));
            Assert.That(BasisModelSizing.RemoteLimits.MinScale, Is.EqualTo(0.1f));
            Assert.That(BasisModelSizing.RemoteLimits.MaxScale, Is.EqualTo(10f));
        }

        [Test]
        public void RemoteLimitsDropBadPosesAndClampUserScale()
        {
            var pose = new BasisModelPose(new BasisModelVec3(float.NaN, 0f, 0f), BasisModelQuat.Identity);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, BasisModelSizing.RemoteLimits), Is.False);
            pose = new BasisModelPose(new BasisModelVec3(0f, 2e5f, 0f), BasisModelQuat.Identity);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, BasisModelSizing.RemoteLimits), Is.False);
            pose = new BasisModelPose(new BasisModelVec3(0f, 1e5f, 0f), BasisModelQuat.Identity);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, BasisModelSizing.RemoteLimits), Is.True);

            float scale = 50f;
            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref scale, BasisModelSizing.RemoteLimits), Is.True);
            Assert.That(scale, Is.EqualTo(10f));
            scale = 0.01f;
            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref scale, BasisModelSizing.RemoteLimits), Is.True);
            Assert.That(scale, Is.EqualTo(0.1f));
            scale = 0f;
            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref scale, BasisModelSizing.RemoteLimits), Is.False);
        }

        [Test]
        public void TheHolderOffsetPutsTheBoundsCentreOnTheRoot()
        {
            var bounds = new BasisGlbAabb { MinX = -1f, MinY = 0f, MinZ = 2f, MaxX = 3f, MaxY = 4f, MaxZ = 6f };
            BasisModelSizing.HolderLocalPosition(bounds, 0.5f, out float x, out float y, out float z);
            Assert.That(x, Is.EqualTo(0.5f));
            Assert.That(y, Is.EqualTo(-1f));
            Assert.That(z, Is.EqualTo(-2f));

            // Same as −(Unity-space centre)·s: glTFast negates X.
            BasisGlbAabb unity = bounds.ToUnitySpace();
            Assert.That(x, Is.EqualTo(-unity.CenterX * 0.5f));
            Assert.That(y, Is.EqualTo(-unity.CenterY * 0.5f));
            Assert.That(z, Is.EqualTo(-unity.CenterZ * 0.5f));
        }

        [Test]
        public void FlatModelsGetTheMinimumThickness()
        {
            var poster = new BasisGlbAabb { MinX = -0.5f, MinY = 0f, MinZ = -0.25f, MaxX = 0.5f, MaxY = 0f, MaxZ = 0.25f };
            BasisModelSizing.ShapeSize(poster, 2f, out float x, out float y, out float z);
            Assert.That(x, Is.EqualTo(2f));
            Assert.That(y, Is.EqualTo(BasisModelSizing.MinShapeAxisMeters));
            Assert.That(z, Is.EqualTo(1f));
            Assert.That(BasisModelSizing.MinShapeAxisMeters, Is.EqualTo(0.01f));
        }

        [Test]
        public void ShapeAxesAreNaNSafeAndCapped()
        {
            Assert.That(BasisModelSizing.ShapeAxisMeters(float.NaN, 1f), Is.EqualTo(0.01f));
            Assert.That(BasisModelSizing.ShapeAxisMeters(1f, float.NaN), Is.EqualTo(0.01f));
            Assert.That(BasisModelSizing.ShapeAxisMeters(-3f, 1f), Is.EqualTo(0.01f));
            Assert.That(BasisModelSizing.ShapeAxisMeters(0.005f, 1f), Is.EqualTo(0.01f));
            Assert.That(BasisModelSizing.ShapeAxisMeters(0.25f, 2f), Is.EqualTo(0.5f));
            Assert.That(BasisModelSizing.ShapeAxisMeters(1e9f, 1f), Is.EqualTo(4f));
            Assert.That(BasisModelSizing.ShapeAxisMeters(float.PositiveInfinity, 1f), Is.EqualTo(4f));
        }

        [Test]
        public void AFitOrOriginalShapeNeverExceedsFourMetres()
        {
            foreach (float largestSide in UsableLargestSides)
            {
                foreach (BasisModelSizeMode mode in new[] { BasisModelSizeMode.Fit, BasisModelSizeMode.Original })
                {
                    float scale = BasisModelSizing.ComputeBaseScale(mode, largestSide);
                    float axis = BasisModelSizing.ShapeAxisMeters(largestSide, scale);
                    Assert.That(axis, Is.InRange(0.02f - 1e-6f, 4f), mode + " at " + largestSide);
                }
            }
        }
    }
}
