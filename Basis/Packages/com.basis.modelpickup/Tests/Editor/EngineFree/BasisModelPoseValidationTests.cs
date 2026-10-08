using System;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelPoseValidationTests
    {
        private static readonly BasisModelPoseLimits Limits = new BasisModelPoseLimits(1e6f, 1f / 64f, 64f);

        private static BasisModelPose ValidPose()
        {
            return new BasisModelPose(new BasisModelVec3(1f, 2f, 3f), BasisModelQuat.Identity);
        }

        [TestCase(float.NaN, 0)]
        [TestCase(float.NaN, 1)]
        [TestCase(float.NaN, 2)]
        [TestCase(float.PositiveInfinity, 0)]
        [TestCase(float.PositiveInfinity, 1)]
        [TestCase(float.PositiveInfinity, 2)]
        [TestCase(float.NegativeInfinity, 0)]
        [TestCase(float.NegativeInfinity, 1)]
        [TestCase(float.NegativeInfinity, 2)]
        public void NonFinitePositionIsRejected(float value, int axis)
        {
            BasisModelPose pose = ValidPose();
            if (axis == 0)
                pose.Position.X = value;
            else if (axis == 1)
                pose.Position.Y = value;
            else
                pose.Position.Z = value;

            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, Limits), Is.False);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void NonFiniteRotationIsRejected(float value)
        {
            for (int component = 0; component < 4; component++)
            {
                BasisModelPose pose = ValidPose();
                if (component == 0)
                    pose.Rotation.X = value;
                else if (component == 1)
                    pose.Rotation.Y = value;
                else if (component == 2)
                    pose.Rotation.Z = value;
                else
                    pose.Rotation.W = value;

                Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, Limits), Is.False, $"component {component}");
            }
        }

        [Test]
        public void FarPositionIsRejected()
        {
            BasisModelPose atLimit = ValidPose();
            atLimit.Position.Y = -1e6f;
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref atLimit, Limits), Is.True);

            BasisModelPose beyond = ValidPose();
            beyond.Position.X = 1.0001e6f;
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref beyond, Limits), Is.False);

            BasisModelPose beyondNegative = ValidPose();
            beyondNegative.Position.Z = -2e6f;
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref beyondNegative, Limits), Is.False);

            var tight = new BasisModelPoseLimits(1e5f, 0.1f, 10f);
            BasisModelPose modelFar = ValidPose();
            modelFar.Position.X = 2e5f;
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref modelFar, tight), Is.False);
        }

        [Test]
        public void DegenerateRotationIsRejected()
        {
            BasisModelPose zero = ValidPose();
            zero.Rotation = new BasisModelQuat(0f, 0f, 0f, 0f);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref zero, Limits), Is.False);

            BasisModelPose tiny = ValidPose();
            tiny.Rotation = new BasisModelQuat(0f, 0f, 0f, 1e-5f);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref tiny, Limits), Is.False);

            // Finite components whose squares overflow are as unusable as a NaN.
            BasisModelPose huge = ValidPose();
            huge.Rotation = new BasisModelQuat(1e20f, 0f, 0f, 1e20f);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref huge, Limits), Is.False);
        }

        [Test]
        public void NonUnitRotationIsNormalised()
        {
            BasisModelPose pose = ValidPose();
            pose.Rotation = new BasisModelQuat(0f, 0f, 0f, 2f);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, Limits), Is.True);
            Assert.That(pose.Rotation.W, Is.EqualTo(1f).Within(1e-6f));

            BasisModelPose skewed = ValidPose();
            skewed.Rotation = new BasisModelQuat(0.5f, 0.5f, 0.5f, 0.6f);
            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref skewed, Limits), Is.True);
            Assert.That(skewed.Rotation.LengthSquared, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void UnitRotationIsLeftBitIdentical()
        {
            var halfTurn = new BasisModelQuat(0.70710677f, 0f, 0f, 0.70710677f);
            var slightlyOff = new BasisModelQuat(0f, 0f, 0f, 1.0004f);
            foreach (BasisModelQuat rotation in new[] { BasisModelQuat.Identity, halfTurn, slightlyOff })
            {
                BasisModelPose pose = ValidPose();
                pose.Rotation = rotation;

                Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, Limits), Is.True);
                Assert.That(Bits(pose.Rotation.X), Is.EqualTo(Bits(rotation.X)));
                Assert.That(Bits(pose.Rotation.Y), Is.EqualTo(Bits(rotation.Y)));
                Assert.That(Bits(pose.Rotation.Z), Is.EqualTo(Bits(rotation.Z)));
                Assert.That(Bits(pose.Rotation.W), Is.EqualTo(Bits(rotation.W)));
                Assert.That(pose.Position.X, Is.EqualTo(1f));
            }
        }

        [Test]
        public void ARejectedPoseIsLeftUntouched()
        {
            BasisModelPose pose = ValidPose();
            pose.Rotation = new BasisModelQuat(0f, 0f, 0f, 2f);
            pose.Position.X = float.NaN;

            Assert.That(BasisModelPoseValidation.TrySanitizePose(ref pose, Limits), Is.False);
            Assert.That(pose.Rotation.W, Is.EqualTo(2f));
        }

        [Test]
        public void ScaleIsClampedIntoRange()
        {
            float small = 0.001f;
            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref small, Limits), Is.True);
            Assert.That(small, Is.EqualTo(1f / 64f));

            float large = 1000f;
            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref large, Limits), Is.True);
            Assert.That(large, Is.EqualTo(64f));

            float ordinary = 1.25f;
            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref ordinary, Limits), Is.True);
            Assert.That(ordinary, Is.EqualTo(1.25f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void NonPositiveOrNonFiniteScaleIsRejected(float value)
        {
            float scale = value;

            Assert.That(BasisModelPoseValidation.TrySanitizeScale(ref scale, Limits), Is.False);
            Assert.That(BitConverter.SingleToInt32Bits(scale), Is.EqualTo(BitConverter.SingleToInt32Bits(value)));
        }

        [Test]
        public void IsFiniteMatchesTheIeeeClasses()
        {
            Assert.That(BasisModelPoseValidation.IsFinite(0f), Is.True);
            Assert.That(BasisModelPoseValidation.IsFinite(float.MaxValue), Is.True);
            Assert.That(BasisModelPoseValidation.IsFinite(float.Epsilon), Is.True);
            Assert.That(BasisModelPoseValidation.IsFinite(float.NaN), Is.False);
            Assert.That(BasisModelPoseValidation.IsFinite(float.PositiveInfinity), Is.False);
            Assert.That(BasisModelPoseValidation.IsFinite(float.NegativeInfinity), Is.False);
        }

        private static int Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }
    }
}
