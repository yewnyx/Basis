using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// Pins the transport's tuning. The chunk size is a wire constant both ends derive offsets from, and the
    /// rest match the image pickup's own, so the two features behave alike on the same line.
    /// </summary>
    public sealed class BasisModelShareSettingsTests
    {
        [Test]
        public void TransportConstantsArePinned()
        {
            Assert.That(BasisModelShareSettings.ChunkPayloadBytes, Is.EqualTo(16384));
            Assert.That(BasisModelShareSettings.TransferRateSampleSeconds, Is.EqualTo(0.25f));
            Assert.That(BasisModelShareSettings.TransferRateSmoothing, Is.EqualTo(0.35f));
            Assert.That(BasisModelShareSettings.TransmitTransformHz, Is.EqualTo(15f));
            Assert.That(BasisModelShareSettings.MovedPositionEpsilon, Is.EqualTo(0.001f));
            Assert.That(BasisModelShareSettings.MovedRotationEpsilonDegrees, Is.EqualTo(0.5f));
            Assert.That(BasisModelShareSettings.MovedScaleEpsilon, Is.EqualTo(0.01f));
            Assert.That(BasisModelShareSettings.ShareBandwidthFraction, Is.EqualTo(0.5f));
            Assert.That(BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond, Is.EqualTo(64L * 1024L));
            Assert.That(BasisModelShareSettings.MinUplinkBudgetBytesPerSecond, Is.EqualTo(16L * 1024L));
            Assert.That(BasisModelShareSettings.MaxUplinkBudgetBytesPerSecond, Is.EqualTo(256L * 1024L * 1024L));
            Assert.That(BasisModelShareSettings.TargetQueuingDelayMs, Is.EqualTo(18f));
            Assert.That(BasisModelShareSettings.LinkProbeIntervalSeconds, Is.EqualTo(0.5f));
            Assert.That(BasisModelShareSettings.LinkProbeRampFraction, Is.EqualTo(0.6f));
            Assert.That(BasisModelShareSettings.LinkProbeBaselineWindowSeconds, Is.EqualTo(60f));
            Assert.That(BasisModelShareSettings.LinkProbeRampBytesPerSecond, Is.EqualTo(32768f));
            Assert.That(BasisModelShareSettings.LinkProbeQueueBackoffPackets, Is.EqualTo(96));
            Assert.That(BasisModelShareSettings.LinkProbeQueueBackoffFactor, Is.EqualTo(0.5f));
            Assert.That(BasisModelShareSettings.RelayEgressBudgetBytesPerSecond, Is.EqualTo(512L * 1024L));
            Assert.That(BasisModelShareSettings.ShareBandwidthBurstSeconds, Is.EqualTo(0.25f));
            Assert.That(BasisModelShareSettings.MaxUnfragmentedPayloadBytes, Is.EqualTo(988));
            Assert.That(BasisModelShareSettings.MaxAbsPositionMeters, Is.EqualTo(1000000f));
        }
    }
}
