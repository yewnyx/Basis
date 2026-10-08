using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelBandwidthTests
    {
        private const int Chunk = BasisModelShareSettings.ChunkPayloadBytes;

        /// <summary>A chunk packet as the outbound queue meters it: payload plus the 25-byte header.</summary>
        private const int Packet = Chunk + BasisModelShareWire.ChunkHeaderBytes;

        private const float Step = 1f / 60f;

        // The frame counter survives Reset by design, so every BeginFrame here needs a frame nobody used yet.
        private static int _frame = 1 << 30;

        private static int NextFrame()
        {
            return ++_frame;
        }

        [SetUp]
        public void SetUp()
        {
            BasisModelLinkProbe.Reset();
            BasisModelBandwidth.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelLinkProbe.Reset();
            BasisModelBandwidth.Reset();
        }

        [Test]
        public void TheUplinkGetsHalfOfTheMeasuredLineAndTheRelayFallsBackUntilAServerSpeaks()
        {
            Assert.That(
                BasisModelBandwidth.UplinkBytesPerSecond,
                Is.EqualTo(BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond * 0.5)
                    .Within(0.001)
            );
            Assert.That(
                BasisModelBandwidth.RelayBytesPerSecond,
                Is.EqualTo((double)BasisModelShareSettings.RelayEgressBudgetBytesPerSecond)
                    .Within(0.001)
            );
        }

        [Test]
        public void AnAdvertisedBudgetReplacesTheFallbackAndIsSpentInFull()
        {
            // Taken at face value rather than halved: the operator who configured it has already decided
            // what their pipe is worth, and discounting it again would quietly hand them half.
            BasisModelBandwidth.ServerRelayBudgetBytesPerSecond = 25_000_000L;

            Assert.That(
                BasisModelBandwidth.RelayBytesPerSecond,
                Is.EqualTo(25_000_000d).Within(0.001)
            );
        }

        [Test]
        public void LeavingAnInstanceForgetsWhatTheLastServerAdvertised()
        {
            BasisModelBandwidth.ServerRelayBudgetBytesPerSecond = 25_000_000L;

            BasisModelBandwidth.Reset();

            Assert.That(
                BasisModelBandwidth.RelayBytesPerSecond,
                Is.EqualTo((double)BasisModelShareSettings.RelayEgressBudgetBytesPerSecond)
                    .Within(0.001)
            );
        }

        [Test]
        public void ASharerGetsTheAdvertisedBudgetDividedByTheFanOut()
        {
            RampProbePastTheRelayBudget();

            double solo = SustainedPayloadBytesPerSecond(1, 25_000_000L);
            double crowded = SustainedPayloadBytesPerSecond(20, 25_000_000L);

            Assert.That(solo, Is.EqualTo(25_000_000d).Within(25_000_000d * 0.05));
            Assert.That(crowded, Is.EqualTo(1_250_000d).Within(1_250_000d * 0.05));
        }

        [Test]
        public void AnAdvertisedBudgetMovesAModelInSecondsWhereTheFallbackTookMinutes()
        {
            // The complaint this whole path exists to answer: a twenty-player instance on the built-in
            // guess crawled, and no amount of pipe at either end changed it, because the guess never
            // asked anyone. Four megabytes is an ordinary textured model.
            const int Model = 4 * 1024 * 1024;
            const int FanOut = 20;

            RampProbePastTheRelayBudget();

            double fallbackSeconds = SecondsToSend(Model, FanOut, 0L);
            double advertisedSeconds = SecondsToSend(Model, FanOut, 25_000_000L);

            Assert.That(fallbackSeconds, Is.GreaterThan(120d));
            Assert.That(advertisedSeconds, Is.LessThan(5d));
        }

        /// <summary>
        /// Idles the probe up to its ceiling so the uplink bucket is not what these measurements land on.
        /// Live it gets there the same way, by ramping on a quiet link from the moment the feature arms.
        /// </summary>
        private static void RampProbePastTheRelayBudget()
        {
            float now = 0f;
            for (int step = 0; step < 200; step++)
            {
                now += BasisModelShareSettings.LinkProbeIntervalSeconds;
                BasisModelLinkProbe.Observe(now, 10f, 0);
            }
        }

        /// <summary>Rate a relayed transfer settles at, in payload bytes per second, over ten seconds.</summary>
        private static double SustainedPayloadBytesPerSecond(int relayRecipients, long advertisedBudget)
        {
            const int Steps = 600;

            BasisModelBandwidth.Reset();
            BasisModelBandwidth.ServerRelayBudgetBytesPerSecond = advertisedBudget;
            double delivered = 0d;
            for (int step = 0; step < Steps; step++)
            {
                BasisModelBandwidth.Refill(Step);
                while (BasisModelBandwidth.TryConsume(Chunk, 0, relayRecipients))
                    delivered += Chunk;
            }
            return delivered / (Steps * Step);
        }

        /// <summary>How long one model of <paramref name="payloadBytes"/> takes at a given fan-out.</summary>
        private static double SecondsToSend(int payloadBytes, int relayRecipients, long advertisedBudget)
        {
            const int MaxSteps = 60 * 60 * 30;

            BasisModelBandwidth.Reset();
            BasisModelBandwidth.ServerRelayBudgetBytesPerSecond = advertisedBudget;
            double sent = 0d;
            for (int step = 0; step < MaxSteps; step++)
            {
                BasisModelBandwidth.Refill(Step);
                while (sent < payloadBytes && BasisModelBandwidth.TryConsume(Chunk, 0, relayRecipients))
                    sent += Chunk;
                if (sent >= payloadBytes)
                    return (step + 1) * Step;
            }
            return double.PositiveInfinity;
        }

        [Test]
        public void RelayedSendCostsUplinkOnceAndRelayOncePerRecipient()
        {
            double uplinkBefore = BasisModelBandwidth.UplinkTokens;
            double relayBefore = BasisModelBandwidth.RelayTokens;

            Assert.That(BasisModelBandwidth.TryConsume(1000, 0, 8), Is.True);

            Assert.That(
                uplinkBefore - BasisModelBandwidth.UplinkTokens,
                Is.EqualTo(1000d).Within(0.001)
            );
            Assert.That(
                relayBefore - BasisModelBandwidth.RelayTokens,
                Is.EqualTo(8000d).Within(0.001)
            );
        }

        [Test]
        public void DirectSendCostsUplinkPerPeerAndNoRelay()
        {
            double uplinkBefore = BasisModelBandwidth.UplinkTokens;
            double relayBefore = BasisModelBandwidth.RelayTokens;

            Assert.That(BasisModelBandwidth.TryConsume(1000, 3, 0), Is.True);

            Assert.That(
                uplinkBefore - BasisModelBandwidth.UplinkTokens,
                Is.EqualTo(3000d).Within(0.001)
            );
            Assert.That(BasisModelBandwidth.RelayTokens, Is.EqualTo(relayBefore));
        }

        [Test]
        public void MixedSendChargesOneRelayCopyPlusEachDirectPeer()
        {
            double uplinkBefore = BasisModelBandwidth.UplinkTokens;

            Assert.That(BasisModelBandwidth.TryConsume(500, 2, 4), Is.True);

            Assert.That(
                uplinkBefore - BasisModelBandwidth.UplinkTokens,
                Is.EqualTo(1500d).Within(0.001)
            );
            Assert.That(
                BasisModelBandwidth.RelayCapacityBytes
                    - BasisModelBandwidth.RelayTokens,
                Is.EqualTo(2000d).Within(0.001)
            );
        }

        [Test]
        public void AnExhaustedRelayBucketStillLetsDirectPeersTransfer()
        {
            // Wide but small sends, so the relay bucket empties while the uplink bucket — which a relayed
            // send only ever charges one copy against — still has room. That separation is the whole point:
            // being unable to afford the server's fan-out says nothing about a direct link.
            while (BasisModelBandwidth.RelayTokens > 0d)
            {
                Assert.That(BasisModelBandwidth.TryConsume(64, 0, 64), Is.True);
            }

            Assert.That(BasisModelBandwidth.UplinkTokens, Is.GreaterThan(0d));
            Assert.That(BasisModelBandwidth.TryConsume(64, 0, 1), Is.False);
            Assert.That(BasisModelBandwidth.TryConsume(64, 1, 0), Is.True);
        }

        [Test]
        public void SendsStopWhenTheUplinkBucketIsSpentAndResumeAfterRefill()
        {
            while (BasisModelBandwidth.UplinkTokens > 0d)
            {
                Assert.That(BasisModelBandwidth.TryConsume(Chunk, 1, 0), Is.True);
            }

            Assert.That(BasisModelBandwidth.TryConsume(Chunk, 1, 0), Is.False);

            BasisModelBandwidth.Refill(1f);

            Assert.That(BasisModelBandwidth.TryConsume(Chunk, 1, 0), Is.True);
        }

        [Test]
        public void RefillCreditsTheConfiguredRateAndClampsToTheBurstWindow()
        {
            BasisModelBandwidth.TryConsume(Chunk, 1, 0);
            double spent = BasisModelBandwidth.UplinkCapacityBytes
                - BasisModelBandwidth.UplinkTokens;
            Assert.That(spent, Is.GreaterThan(0d));

            BasisModelBandwidth.Refill(0.001f);
            Assert.That(
                BasisModelBandwidth.UplinkTokens,
                Is.EqualTo(
                        BasisModelBandwidth.UplinkCapacityBytes
                            - spent
                            + BasisModelBandwidth.UplinkBytesPerSecond * 0.001d
                    )
                    .Within(0.01)
            );

            BasisModelBandwidth.Refill(600f);
            Assert.That(
                BasisModelBandwidth.UplinkTokens,
                Is.EqualTo(BasisModelBandwidth.UplinkCapacityBytes).Within(0.001)
            );
            Assert.That(
                BasisModelBandwidth.RelayTokens,
                Is.EqualTo(BasisModelBandwidth.RelayCapacityBytes).Within(0.001)
            );
        }

        [Test]
        public void APacketLargerThanTheBucketStillSendsAndIsRepaidBeforeTheNextOne()
        {
            int oversized = (int)BasisModelBandwidth.RelayCapacityBytes * 4;

            Assert.That(BasisModelBandwidth.TryConsume(oversized, 0, 1), Is.True);
            Assert.That(BasisModelBandwidth.RelayTokens, Is.LessThan(0d));
            Assert.That(BasisModelBandwidth.TryConsume(Chunk, 0, 1), Is.False);

            BasisModelBandwidth.Refill(3600f);

            Assert.That(BasisModelBandwidth.TryConsume(Chunk, 0, 1), Is.True);
        }

        [Test]
        public void SendsWithNoRecipientsAreFree()
        {
            double uplinkBefore = BasisModelBandwidth.UplinkTokens;

            Assert.That(BasisModelBandwidth.TryConsume(Chunk, 0, 0), Is.True);

            Assert.That(BasisModelBandwidth.UplinkTokens, Is.EqualTo(uplinkBefore));
        }

        // ── Per-frame gating ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void BeginFrameRefillsOncePerFrameNoMatterHowOftenItIsCalled()
        {
            while (BasisModelBandwidth.UplinkTokens > 0d)
                BasisModelBandwidth.TryConsume(Chunk, 1, 0);

            int frame = NextFrame();
            Assert.That(BasisModelBandwidth.HasBegunFrame(frame), Is.False);
            Assert.That(BasisModelBandwidth.BeginFrame(frame, Step), Is.True);
            double afterOne = BasisModelBandwidth.UplinkTokens;

            Assert.That(BasisModelBandwidth.BeginFrame(frame, Step), Is.False);
            Assert.That(BasisModelBandwidth.HasBegunFrame(frame), Is.True);
            Assert.That(BasisModelBandwidth.UplinkTokens, Is.EqualTo(afterOne));

            Assert.That(BasisModelBandwidth.BeginFrame(NextFrame(), Step), Is.True);
            Assert.That(BasisModelBandwidth.UplinkTokens, Is.GreaterThan(afterOne));
        }
    }
}
