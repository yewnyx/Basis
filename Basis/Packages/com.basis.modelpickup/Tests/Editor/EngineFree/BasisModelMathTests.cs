using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelMathTests
    {
        [Test]
        public void RemoteTransformSmoothingIsFrameRateIndependentAndBounded()
        {
            float oneSixtieth = BasisModelMath.RemoteTransformLerpFactor(1f / 60f);
            float oneOneTwentieth = BasisModelMath.RemoteTransformLerpFactor(1f / 120f);
            float twoOneTwentiethSteps = 1f - (1f - oneOneTwentieth) * (1f - oneOneTwentieth);

            Assert.That(twoOneTwentiethSteps, Is.EqualTo(oneSixtieth).Within(0.000001f));
            Assert.That(BasisModelMath.RemoteTransformLerpFactor(10f), Is.InRange(0f, 1f));
            Assert.That(BasisModelMath.RemoteTransformLerpFactor(-1f), Is.EqualTo(0f));
        }

        [Test]
        public void RoundToIntUsesBankersRoundingLikeMathf()
        {
            Assert.That(BasisModelMath.RoundToInt(2.5f), Is.EqualTo(2));
            Assert.That(BasisModelMath.RoundToInt(3.5f), Is.EqualTo(4));
            Assert.That(BasisModelMath.RoundToInt(-2.5f), Is.EqualTo(-2));
            Assert.That(BasisModelMath.RoundToInt(2.4f), Is.EqualTo(2));
            Assert.That(BasisModelMath.CeilToInt(2.1f), Is.EqualTo(3));
            Assert.That(BasisModelMath.FloorToInt(-2.1f), Is.EqualTo(-3));
        }

        [Test]
        public void ClampPassesNaNThroughLikeMathf()
        {
            Assert.That(float.IsNaN(BasisModelMath.Clamp(float.NaN, 0f, 1f)), Is.True);
            Assert.That(BasisModelMath.Clamp(-1f, 0f, 1f), Is.EqualTo(0f));
            Assert.That(BasisModelMath.Clamp(2f, 0f, 1f), Is.EqualTo(1f));
            Assert.That(BasisModelMath.Clamp(5, 1, 3), Is.EqualTo(3));
            Assert.That(BasisModelMath.Clamp(-5, 1, 3), Is.EqualTo(1));
            // Mathf.Max/Min are plain comparisons, so a NaN second operand is what comes back from both.
            Assert.That(float.IsNaN(BasisModelMath.Max(0f, float.NaN)), Is.True);
            Assert.That(float.IsNaN(BasisModelMath.Min(0f, float.NaN)), Is.True);
            Assert.That(BasisModelMath.Max(float.NaN, 0f), Is.EqualTo(0f));
        }

        [Test]
        public void LerpClampsT()
        {
            Assert.That(BasisModelMath.Lerp(10f, 20f, 0.5f), Is.EqualTo(15f));
            Assert.That(BasisModelMath.Lerp(10f, 20f, -1f), Is.EqualTo(10f));
            Assert.That(BasisModelMath.Lerp(10f, 20f, 2f), Is.EqualTo(20f));
            Assert.That(BasisModelMath.Clamp01(1.5f), Is.EqualTo(1f));
            Assert.That(BasisModelMath.Clamp01(-0.5f), Is.EqualTo(0f));
        }
    }
}
