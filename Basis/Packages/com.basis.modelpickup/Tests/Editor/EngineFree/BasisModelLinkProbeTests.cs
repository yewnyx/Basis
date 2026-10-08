using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelLinkProbeTests
    {
        private const float Quiet = 20f;
        private const float Interval = BasisModelShareSettings.LinkProbeIntervalSeconds;

        [SetUp]
        public void SetUp()
        {
            BasisModelLinkProbe.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelLinkProbe.Reset();
        }

        /// <summary>Leaves the probe with a quiet baseline and one full ramp step applied.</summary>
        private static void SettleQuietBaseline()
        {
            BasisModelLinkProbe.Reset();
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            BasisModelLinkProbe.Observe(2f, Quiet, 0);
        }

        [Test]
        public void StartsAtTheAssumedBudgetUntilSomethingIsMeasured()
        {
            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void TheFirstSampleOnlyEstablishesAStartingPoint()
        {
            BasisModelLinkProbe.Observe(1f, Quiet, 0);

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void SamplesArrivingFasterThanTheControlIntervalAreIgnored()
        {
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            float before = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisModelLinkProbe.Observe(1f + Interval * 0.5f, Quiet, 0);

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(before)
            );
        }

        [Test]
        public void AQuietLinkRampsTheRateUp()
        {
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            BasisModelLinkProbe.Observe(2f, Quiet, 0);

            Assert.That(BasisModelLinkProbe.QueuingDelayMs, Is.EqualTo(0f));
            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(
                        BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond
                            * (1f + BasisModelShareSettings.LinkProbeRampFraction)
                    )
                    .Within(1f)
            );
        }

        [Test]
        public void AVeryFastLinkIsFoundInSecondsRatherThanMinutes()
        {
            BasisModelLinkProbe.Observe(0f, Quiet, 0);
            for (float t = Interval; t <= 30f; t += Interval)
                BasisModelLinkProbe.Observe(t, Quiet, 0);

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.MaxUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void TheRateClimbsByAFractionSoEveryScaleTakesTheSameNumberOfSteps()
        {
            SettleQuietBaseline();
            float low = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;
            BasisModelLinkProbe.Observe(3f, Quiet, 0);
            float lowGrowth = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond / low;

            for (float t = 4f; t <= 12f; t += 1f)
                BasisModelLinkProbe.Observe(t, Quiet, 0);
            float high = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;
            Assume.That(high, Is.LessThan(BasisModelShareSettings.MaxUplinkBudgetBytesPerSecond));
            BasisModelLinkProbe.Observe(13f, Quiet, 0);
            float highGrowth = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond / high;

            Assert.That(high, Is.GreaterThan(low * 10f));
            Assert.That(highGrowth, Is.EqualTo(lowGrowth).Within(0.001f));
        }

        [Test]
        public void TheBacklogThresholdScalesWithTheRateItIsJudging()
        {
            Assert.That(
                BasisModelLinkProbe.BacklogLimit(1024f),
                Is.EqualTo(BasisModelShareSettings.LinkProbeQueueBackoffPackets)
            );
            Assert.That(
                BasisModelLinkProbe.BacklogLimit(64f * 1024f * 1024f),
                Is.GreaterThan(BasisModelShareSettings.LinkProbeQueueBackoffPackets * 100)
            );
        }

        [Test]
        public void BacklogLimitUsesTheUnfragmentedPayloadMirror()
        {
            // max(96, ceil(1e6 × 0.5 / 988)) = 507 packets of the transport's largest unfragmented payload.
            Assert.That(BasisModelLinkProbe.BacklogLimit(1_000_000f), Is.EqualTo(507));
        }

        [Test]
        public void AHealthyFastTransferIsNotMistakenForABacklog()
        {
            // One interval of in-flight packets at a fast but healthy rate must not read as "behind".
            float fast = 32f * 1024f * 1024f;
            int inFlight = BasisModelLinkProbe.BacklogLimit(fast) - 1;

            BasisModelLinkProbe.Reset();
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            for (float t = 2f; t <= 24f; t += 1f)
                BasisModelLinkProbe.Observe(t, Quiet, 0);
            Assume.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.GreaterThan(fast)
            );
            float before = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisModelLinkProbe.Observe(25f, Quiet, inFlight);

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.GreaterThanOrEqualTo(before)
            );
        }

        [Test]
        public void QueuingDelayPastTheTargetBacksTheRateOff()
        {
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            BasisModelLinkProbe.Observe(2f, Quiet, 0);
            float peak = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisModelLinkProbe.Observe(
                3f,
                Quiet + BasisModelShareSettings.TargetQueuingDelayMs * 4f,
                0
            );

            Assert.That(
                BasisModelLinkProbe.QueuingDelayMs,
                Is.EqualTo(BasisModelShareSettings.TargetQueuingDelayMs * 4f).Within(0.001f)
            );
            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.LessThan(peak)
            );
        }

        [Test]
        public void DelayUnderTheTargetStillRampsUpButMoreSlowly()
        {
            // The baseline is only established by the first effective sample, so both runs need a settled
            // quiet baseline before the step under test or they would both read zero queuing delay.
            SettleQuietBaseline();
            float start = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;
            BasisModelLinkProbe.Observe(
                3f,
                Quiet + BasisModelShareSettings.TargetQueuingDelayMs * 0.5f,
                0
            );
            float gentle = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond - start;

            SettleQuietBaseline();
            BasisModelLinkProbe.Observe(3f, Quiet, 0);
            float full = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond - start;

            Assert.That(gentle, Is.GreaterThan(0f));
            Assert.That(gentle, Is.LessThan(full));
        }

        [Test]
        public void ABackedUpSendQueueHalvesTheRateInsteadOfRamping()
        {
            SettleQuietBaseline();
            float ramped = BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond;

            BasisModelLinkProbe.Reset();
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            BasisModelLinkProbe.Observe(
                2f,
                Quiet,
                BasisModelShareSettings.LinkProbeQueueBackoffPackets
            );

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo(
                        BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond
                            * BasisModelShareSettings.LinkProbeQueueBackoffFactor
                    )
                    .Within(1f)
            );
            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.LessThan(ramped)
            );
        }

        [Test]
        public void SustainedQueuingDelayDrivesTheRateToItsFloor()
        {
            SettleQuietBaseline();

            for (int i = 3; i < 30; i++)
                BasisModelLinkProbe.Observe(i, Quiet + 500f, 0);

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.MinUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void ATransfersOwnQueuingNeverBecomesTheBaselineItIsMeasuredAgainst()
        {
            SettleQuietBaseline();

            // Well past a single slot, and past the old single-expiry design's whole window: the quiet
            // minimum has to survive as long as any slot still remembers it, or the probe would decide its
            // own congestion was the new normal and ramp straight back into it.
            float slot = BasisModelShareSettings.LinkProbeBaselineWindowSeconds / 4f;
            for (float t = 3f; t <= 2f + slot * 3f; t += 1f)
                BasisModelLinkProbe.Observe(t, Quiet + 500f, 0);

            Assert.That(BasisModelLinkProbe.BaseRoundTripMs, Is.EqualTo(Quiet));
            Assert.That(BasisModelLinkProbe.QueuingDelayMs, Is.EqualTo(500f));
        }

        [Test]
        public void TheRateNeverLeavesItsFloorOrCeiling()
        {
            for (int i = 1; i < 200; i++)
            {
                BasisModelLinkProbe.Observe(
                    i,
                    Quiet,
                    BasisModelShareSettings.LinkProbeQueueBackoffPackets
                );
            }

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.MinUplinkBudgetBytesPerSecond)
            );

            BasisModelLinkProbe.Reset();
            for (int i = 1; i < 200; i++)
                BasisModelLinkProbe.Observe(i, Quiet, 0);

            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.MaxUplinkBudgetBytesPerSecond)
            );
        }

        [Test]
        public void APermanentlySlowerPathReArmsTheBaselineInsteadOfBackingOffForever()
        {
            SettleQuietBaseline();

            float slot = BasisModelShareSettings.LinkProbeBaselineWindowSeconds / 4f;
            for (float t = 3f; t <= 2f + slot * 5f; t += 1f)
                BasisModelLinkProbe.Observe(t, 200f, 0);

            Assert.That(BasisModelLinkProbe.BaseRoundTripMs, Is.EqualTo(200f));
            Assert.That(BasisModelLinkProbe.QueuingDelayMs, Is.EqualTo(0f));
        }

        [Test]
        public void AQuieterRoundTripImmediatelyBecomesTheNewBaseline()
        {
            BasisModelLinkProbe.Observe(1f, 80f, 0);
            BasisModelLinkProbe.Observe(2f, 80f, 0);
            BasisModelLinkProbe.Observe(3f, 30f, 0);

            Assert.That(BasisModelLinkProbe.BaseRoundTripMs, Is.EqualTo(30f));
            Assert.That(BasisModelLinkProbe.QueuingDelayMs, Is.EqualTo(0f));
        }

        [Test]
        public void TheShareIsHalfOfWhateverTheProbeDiscovered()
        {
            BasisModelLinkProbe.Observe(1f, Quiet, 0);
            BasisModelLinkProbe.Observe(2f, Quiet, 0);

            Assert.That(
                BasisModelBandwidth.UplinkBytesPerSecond,
                Is.EqualTo(BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond * 0.5)
                    .Within(0.001)
            );
        }
    }
}
