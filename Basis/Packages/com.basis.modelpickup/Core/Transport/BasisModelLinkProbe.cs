namespace Basis.ModelPickup
{
    /// <summary>
    /// Discovers how much uplink model transfers may take before they start hurting anything else. The
    /// manager ticks it once per frame; a second sample in the same frame would distort the estimate.
    ///
    /// A fixed byte budget is either too slow on a good line or still too fast on a bad one, so the rate is
    /// probed instead of assumed: it ramps up while the link stays quiet and backs off as soon as the link
    /// shows strain. Two signals say "strain", and they catch different halves of the problem:
    ///
    /// Round-trip time above the quietest round trip seen recently is the gradient signal. An over-filled
    /// uplink queues before it drops, and that queuing delay is exactly what voice and pose feel first, so
    /// steering to a small target delay is the same thing as staying out of everyone else's way. Because the
    /// round trip is measured against the server, the number also rises when the server is the congested
    /// party — which wants the same response.
    ///
    /// Depth of this client's own outgoing reliable queue is the emergency signal. It moves the moment the
    /// transport stops draining as fast as the chunks arrive, without waiting a round trip for the news.
    ///
    /// This governs the local uplink only. The relay's fan-out budget stays a fixed ceiling — a fast local
    /// line is no reason to make a server forward more. This half is engine-free; <c>Tick</c>, which reads the
    /// server peer, lives in the Unity half.
    /// </summary>
    public static partial class BasisModelLinkProbe
    {
        private const int BaselineSlotCount = 4;

        /// <summary>
        /// Rolling per-slot minima of the round trip. The baseline is the smallest of them, so it tracks a
        /// path that genuinely got slower while refusing to adopt a delay this transfer caused itself: a
        /// single expiring slot would take whatever the round trip happened to be at expiry — congestion
        /// included — and then ramp merrily into it.
        /// </summary>
        private static readonly float[] _baselineSlots = new float[BaselineSlotCount];

        private static int _baselineSlot;
        private static float _baselineSlotExpiry;
        /// <summary>The quietest recent round trip, the baseline queuing delay is measured from.</summary>
        public static float BaseRoundTripMs;

        private static float _rateBytesPerSecond;
        private static float _lastSampleTime;

        /// <summary>Round trip above <see cref="BaseRoundTripMs"/> at the last sample: the gradient signal.</summary>
        public static float QueuingDelayMs;

        /// <summary>Last queue depth folded in; diagnostics only.</summary>
        public static int QueuedPackets;

        public static float DiscoveredUplinkBytesPerSecond =>
            _rateBytesPerSecond > 0f
                ? _rateBytesPerSecond
                : BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond;

        /// <summary>Forgets everything measured. The manager reaches this only through the uplink's ResetForNewConnection.</summary>
        public static void Reset()
        {
            BaseRoundTripMs = 0f;
            _baselineSlotExpiry = 0f;
            _baselineSlot = 0;
            for (int i = 0; i < BaselineSlotCount; i++)
                _baselineSlots[i] = 0f;
            _rateBytesPerSecond = BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond;
            _lastSampleTime = 0f;
            QueuingDelayMs = 0f;
            QueuedPackets = 0;
        }

        /// <summary>
        /// Queue depth that counts as the transport falling behind at <paramref name="rateBytesPerSecond"/>:
        /// more than one control interval of data still waiting to go out. Scaling with the rate is what
        /// lets one threshold mean the same thing on a 1 Mb/s line and a 25 Gb/s one.
        /// </summary>
        public static int BacklogLimit(float rateBytesPerSecond)
        {
            float inFlight =
                rateBytesPerSecond
                * BasisModelShareSettings.LinkProbeIntervalSeconds
                / BasisModelShareSettings.MaxUnfragmentedPayloadBytes;
            return BasisModelMath.Max(
                BasisModelShareSettings.LinkProbeQueueBackoffPackets,
                BasisModelMath.CeilToInt(inFlight)
            );
        }

        /// <summary>
        /// Folds one round trip into the rolling minima and republishes the baseline. Slots rotate on a
        /// timer, each keeping the smallest round trip it saw, so the baseline only rises once every slot in
        /// <see cref="BasisModelShareSettings.LinkProbeBaselineWindowSeconds"/> of history has risen.
        /// </summary>
        private static void UpdateBaseline(float now, float roundTripMs)
        {
            float slotSeconds =
                BasisModelShareSettings.LinkProbeBaselineWindowSeconds / BaselineSlotCount;

            if (_baselineSlotExpiry <= 0f || now - _baselineSlotExpiry >= slotSeconds * BaselineSlotCount)
            {
                for (int i = 0; i < BaselineSlotCount; i++)
                    _baselineSlots[i] = roundTripMs;
                _baselineSlot = 0;
                _baselineSlotExpiry = now + slotSeconds;
            }
            else
            {
                while (now >= _baselineSlotExpiry)
                {
                    _baselineSlot = (_baselineSlot + 1) % BaselineSlotCount;
                    _baselineSlots[_baselineSlot] = roundTripMs;
                    _baselineSlotExpiry += slotSeconds;
                }
            }

            if (roundTripMs < _baselineSlots[_baselineSlot])
                _baselineSlots[_baselineSlot] = roundTripMs;

            float lowest = _baselineSlots[0];
            for (int i = 1; i < BaselineSlotCount; i++)
            {
                if (_baselineSlots[i] < lowest)
                    lowest = _baselineSlots[i];
            }
            BaseRoundTripMs = lowest;
        }

        /// <summary>
        /// One control step. While the outgoing queue is at or past <see cref="BacklogLimit"/> the rate is
        /// halved and nothing else applies; otherwise it moves toward
        /// <see cref="BasisModelShareSettings.TargetQueuingDelayMs"/> of queuing delay. Either way it never
        /// leaves the configured floor and ceiling — so a transfer always makes some progress, and a fast
        /// LAN cannot talk the estimate into something the rest of the client will not survive.
        ///
        /// The baseline round trip is whatever the first effective sample reports, so a probe that starts
        /// during congestion measures from a congested baseline until the window re-arms. That is the usual
        /// trade for delay-based control and is why the baseline expires rather than only ever falling.
        /// </summary>
        public static void Observe(float now, float roundTripMs, int queuedPackets)
        {
            if (_rateBytesPerSecond <= 0f)
                _rateBytesPerSecond = BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond;

            if (_lastSampleTime <= 0f)
            {
                _lastSampleTime = now;
                return;
            }

            float elapsed = now - _lastSampleTime;
            if (elapsed < BasisModelShareSettings.LinkProbeIntervalSeconds)
                return;
            _lastSampleTime = now;
            QueuedPackets = queuedPackets;

            if (roundTripMs > 0f)
            {
                UpdateBaseline(now, roundTripMs);
                QueuingDelayMs = BasisModelMath.Max(0f, roundTripMs - BaseRoundTripMs);
            }

            if (queuedPackets >= BacklogLimit(_rateBytesPerSecond))
            {
                // The backlog reading replaces the delay response rather than being averaged with it. A
                // step that both ramped up and then halved would settle at the ramp constant no matter how
                // badly the queue was backed up, since the two cancel; yielding outright decays toward the
                // floor for as long as the transport stays behind.
                _rateBytesPerSecond *= BasisModelShareSettings.LinkProbeQueueBackoffFactor;
            }
            else
            {
                float target = BasisModelShareSettings.TargetQueuingDelayMs;
                float offTarget = target > 0f ? (target - QueuingDelayMs) / target : 0f;
                float step = BasisModelMath.Max(
                    BasisModelShareSettings.LinkProbeRampBytesPerSecond,
                    _rateBytesPerSecond * BasisModelShareSettings.LinkProbeRampFraction
                );
                _rateBytesPerSecond += step * BasisModelMath.Clamp(offTarget, -1f, 1f) * elapsed;
            }

            _rateBytesPerSecond = BasisModelMath.Clamp(
                _rateBytesPerSecond,
                BasisModelShareSettings.MinUplinkBudgetBytesPerSecond,
                BasisModelShareSettings.MaxUplinkBudgetBytesPerSecond
            );
        }
    }
}
