using System;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Byte-rate governor for model replication. The image pickup keeps its own; each takes
    /// <see cref="BasisModelShareSettings.ShareBandwidthFraction"/> of what its probe measures.
    ///
    /// Two resources are metered separately because one packet spends them at different rates: uplink
    /// tokens cover what this client writes to sockets — one copy for the relay however many players it
    /// reaches, plus one copy per directly connected peer — while relay tokens cover what the server
    /// forwards on this client's behalf, which is the packet once per relayed recipient. Peers on a direct
    /// link never touch the relay bucket, so an instance that is fully P2P transfers at the full uplink rate.
    ///
    /// The uplink budget is whatever <see cref="BasisModelLinkProbe"/> currently measures the link as having
    /// spare, reduced by the share fraction so pose, voice, and object sync keep the rest. The relay budget is
    /// whatever the server said it can afford to forward, taken at face value; only the fallback used when a
    /// server says nothing is a guess, and it is a small one.
    ///
    /// The manager drives this only through <c>BasisModelUplink</c>, once per frame; <see cref="BeginFrame"/>
    /// is idempotent within a frame, so a second tick in the same frame cannot refill twice.
    /// </summary>
    public static class BasisModelBandwidth
    {
        /// <summary>Uplink budget left this frame, in bytes; negative while a packet's deficit is being repaid.</summary>
        public static double UplinkTokens;

        /// <summary>Relay budget left this frame, in bytes; negative while a packet's deficit is being repaid.</summary>
        public static double RelayTokens;

        private static bool _hasFrame;
        private static int _frame;

        /// <summary>
        /// Server egress budget advertised for this client, in bytes per second, or 0 before a server has
        /// said anything. Refreshed from the join handshake rather than read here so this stays testable
        /// without a live connection.
        /// </summary>
        public static long ServerRelayBudgetBytesPerSecond;

        public static double UplinkBytesPerSecond =>
            BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond
            * BasisModelShareSettings.ShareBandwidthFraction;

        public static double RelayBytesPerSecond =>
            ServerRelayBudgetBytesPerSecond > 0
                ? ServerRelayBudgetBytesPerSecond
                : BasisModelShareSettings.RelayEgressBudgetBytesPerSecond;

        public static double UplinkCapacityBytes =>
            UplinkBytesPerSecond * BasisModelShareSettings.ShareBandwidthBurstSeconds;

        public static double RelayCapacityBytes =>
            RelayBytesPerSecond * BasisModelShareSettings.ShareBandwidthBurstSeconds;

        /// <summary>
        /// Returns both buckets to full. The advertised relay budget is dropped with them: it describes the
        /// server just left, and carrying a generous one into an instance that advertises nothing would spend
        /// a pipe that is not there. The frame counter survives, so a reset mid-frame cannot cause a second
        /// refill.
        /// </summary>
        public static void Reset()
        {
            ServerRelayBudgetBytesPerSecond = 0;
            UplinkTokens = UplinkCapacityBytes;
            RelayTokens = RelayCapacityBytes;
        }

        /// <summary>
        /// Credits one tick of budget, clamped to <see cref="BasisModelShareSettings.ShareBandwidthBurstSeconds"/>
        /// so a long stall — a level load, an alt-tab, a breakpoint — cannot bank seconds of credit and then
        /// release them as one spike. The manager goes through <see cref="BeginFrame"/>; tests may call this.
        /// </summary>
        public static void Refill(float deltaSeconds)
        {
            if (!(deltaSeconds > 0f))
                return;

            UplinkTokens = Math.Min(UplinkCapacityBytes, UplinkTokens + UplinkBytesPerSecond * deltaSeconds);
            RelayTokens = Math.Min(RelayCapacityBytes, RelayTokens + RelayBytesPerSecond * deltaSeconds);
        }

        public static bool HasBegunFrame(int frame)
        {
            return _hasFrame && _frame == frame;
        }

        /// <summary>
        /// The first call for <paramref name="frame"/> refills; any later call for the same frame does nothing and
        /// returns false.
        /// </summary>
        public static bool BeginFrame(int frame, float deltaSeconds)
        {
            if (HasBegunFrame(frame))
                return false;
            _hasFrame = true;
            _frame = frame;
            Refill(deltaSeconds);
            return true;
        }

        /// <summary>
        /// Charges one packet against both buckets and reports whether it may be sent now. A packet costing
        /// more than the remaining budget still goes out while the buckets are positive and leaves them in
        /// deficit until the refill repays it, so a chunk that is larger than the burst capacity — a wide
        /// fan-out, a big instance — can never wedge a transfer permanently.
        /// </summary>
        public static bool TryConsume(int wireBytes, int directRecipients, int relayRecipients)
        {
            if (wireBytes <= 0)
                return true;

            int direct = Math.Max(0, directRecipients);
            int relay = Math.Max(0, relayRecipients);
            if (direct == 0 && relay == 0)
                return true;

            // Relay first: a packet the server cannot forward yet waits on the relay whatever the uplink holds.
            if (relay > 0 && RelayTokens <= 0d)
                return false;
            if (UplinkTokens <= 0d)
                return false;

            UplinkTokens -= (double)wireBytes * direct + (relay > 0 ? wireBytes : 0d);
            RelayTokens -= (double)wireBytes * relay;
            return true;
        }
    }
}
