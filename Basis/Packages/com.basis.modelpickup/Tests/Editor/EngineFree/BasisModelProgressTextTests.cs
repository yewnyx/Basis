using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelProgressTextTests
    {
        [Test]
        public void InboundLabelReadsPercentThenRate()
        {
            Assert.That(
                BasisModelProgressText.BuildText(42, 131072f, false),
                Is.EqualTo("42%  128.0 KB/s")
            );
        }

        [Test]
        public void OutboundLabelIsMarkedAsTransmit()
        {
            Assert.That(
                BasisModelProgressText.BuildText(7, 0f, true),
                Is.EqualTo("tx 7%  0 B/s")
            );
        }

        [Test]
        public void RateFormatStepsThroughBytesKilobytesAndMegabytes()
        {
            Assert.That(BasisModelProgressText.FormatRate(512f), Is.EqualTo("512 B/s"));
            Assert.That(BasisModelProgressText.FormatRate(1536f), Is.EqualTo("1.5 KB/s"));
            Assert.That(
                BasisModelProgressText.FormatRate(3f * 1024f * 1024f),
                Is.EqualTo("3.0 MB/s")
            );
            Assert.That(BasisModelProgressText.FormatRate(-5f), Is.EqualTo("0 B/s"));
        }

        [Test]
        public void LabelKeyTracksPercentRateAndDirection()
        {
            int baseKey = BasisModelProgressText.TextKey(50, 100000f, false);

            Assert.That(
                BasisModelProgressText.TextKey(51, 100000f, false),
                Is.Not.EqualTo(baseKey)
            );
            Assert.That(
                BasisModelProgressText.TextKey(50, 300000f, false),
                Is.Not.EqualTo(baseKey)
            );
            Assert.That(
                BasisModelProgressText.TextKey(50, 100000f, true),
                Is.Not.EqualTo(baseKey)
            );
        }

        [Test]
        public void LabelKeyIgnoresRateNoiseBelowItsBucket()
        {
            Assert.That(
                BasisModelProgressText.TextKey(50, 100000f, false),
                Is.EqualTo(BasisModelProgressText.TextKey(50, 100050f, false))
            );
        }
    }
}
