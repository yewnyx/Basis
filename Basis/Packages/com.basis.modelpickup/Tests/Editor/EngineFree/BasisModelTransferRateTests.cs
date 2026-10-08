using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelTransferRateTests
    {
        [Test]
        public void TheFirstSampleOnlyStartsTheWindow()
        {
            var rate = new BasisModelTransferRate { MovedBytes = 5000 };

            rate.Sample(10f);

            Assert.That(rate.BytesPerSecond, Is.EqualTo(0f));
            Assert.That(rate.SampleTime, Is.EqualTo(10f));
            Assert.That(rate.SampleBytes, Is.EqualTo(5000));
        }

        [Test]
        public void SamplesInsideTheIntervalAreIgnored()
        {
            var rate = new BasisModelTransferRate();
            rate.Sample(10f);
            rate.MovedBytes = 1000;

            rate.Sample(10f + BasisModelShareSettings.TransferRateSampleSeconds * 0.5f);

            Assert.That(rate.BytesPerSecond, Is.EqualTo(0f));
            Assert.That(rate.SampleBytes, Is.EqualTo(0));
        }

        [Test]
        public void TheRateSmoothsTowardTheInstantValue()
        {
            var rate = new BasisModelTransferRate();
            rate.Sample(10f);

            rate.MovedBytes = 1000;
            rate.Sample(11f);
            Assert.That(rate.BytesPerSecond, Is.EqualTo(1000f).Within(0.001f), "the first measurement is taken as is");

            rate.MovedBytes = 4000;
            rate.Sample(12f);
            float expected = 1000f + (3000f - 1000f) * BasisModelShareSettings.TransferRateSmoothing;
            Assert.That(rate.BytesPerSecond, Is.EqualTo(expected).Within(0.001f));
            Assert.That(rate.SampleBytes, Is.EqualTo(4000));
        }

        [Test]
        public void FractionClampsToOne()
        {
            var rate = new BasisModelTransferRate { MovedBytes = 150 };

            Assert.That(rate.Fraction(100), Is.EqualTo(1f));
            Assert.That(rate.Fraction(300), Is.EqualTo(0.5f));
            Assert.That(rate.Fraction(0), Is.EqualTo(0f));
            Assert.That(rate.Fraction(-1), Is.EqualTo(0f));
        }
    }
}
