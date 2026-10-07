namespace Basis.ModelPickup
{
    /// <summary>
    /// Transfer, transform-sync and bandwidth tuning for the model pickup's transport. Engine-free, so the
    /// dotnet harness compiles it with the rest of the core. The values match the image pickup's own, so the
    /// two features behave alike on the same line; device-tier limits live in <see cref="BasisModelTierLimits"/>.
    /// </summary>
    public static class BasisModelShareSettings
    {
        /// <summary>
        /// Payload bytes per chunk. A wire constant: the receiver derives chunk offsets from it. 16 KiB keeps
        /// every relayed chunk inside the server's pooled writers.
        /// </summary>
        public const int ChunkPayloadBytes = 16 * 1024;

        /// <summary>How often a transfer's throughput readout is resampled, in seconds.</summary>
        public const float TransferRateSampleSeconds = 0.25f;

        /// <summary>Weight of the newest throughput sample in a transfer's smoothed rate.</summary>
        public const float TransferRateSmoothing = 0.35f;

        public const float TransmitTransformHz = 15f;
        public const float MovedPositionEpsilon = 0.001f;
        public const float MovedRotationEpsilonDegrees = 0.5f;
        public const float MovedScaleEpsilon = 0.01f;

        /// <summary>
        /// Share of the measured uplink below that model replication may occupy; the rest stays free for
        /// pose, voice, and object sync. Per-frame chunk caps bound per-frame cost only — this is what bounds
        /// the rate, and it is enforced by <see cref="BasisModelBandwidth"/>.
        ///
        /// Applies to the local uplink only. A server-advertised relay budget is already a deliberate
        /// figure from whoever runs the server, so it is spent as given rather than halved again.
        /// </summary>
        public const float ShareBandwidthFraction = 0.5f;

        /// <summary>
        /// Uplink assumed for Basis traffic before <see cref="BasisModelLinkProbe"/> has measured anything,
        /// in bytes per second. Deliberately below what the slowest supported connection can carry — the
        /// probe climbs quickly, so guessing low costs a fraction of a second on a fast link, while guessing
        /// high spends the first seconds of every session congesting a slow one.
        /// </summary>
        public const long StartingUplinkBudgetBytesPerSecond = 64L * 1024L;

        /// <summary>
        /// Uplink the probe will always allow, so a transfer never stalls outright. One chunk every couple
        /// of seconds after the share fraction is applied, which still refreshes the inbound deadline.
        /// </summary>
        public const long MinUplinkBudgetBytesPerSecond = 16L * 1024L;

        /// <summary>
        /// Ceiling on the probed uplink. Far above what pickup transfers can use, and present only so a
        /// mismeasurement cannot ask the rest of the client for something absurd.
        /// </summary>
        public const long MaxUplinkBudgetBytesPerSecond = 256L * 1024L * 1024L;

        /// <summary>
        /// Queuing delay the probe aims to sit under, in milliseconds — round-trip time above the quietest
        /// recent round trip. Below this the rate climbs; above it the rate falls. Kept under a voice frame
        /// so transfers yield before anyone can hear or feel them.
        /// </summary>
        public const float TargetQueuingDelayMs = 18f;

        public const float LinkProbeIntervalSeconds = 0.5f;

        /// <summary>
        /// Share of the current rate the probe adds or removes per second. Proportional rather than fixed
        /// because the supported connections span roughly 1 Mb/s to 25 Gb/s: a step sized for the bottom of
        /// that range would take minutes to find the top, and a step sized for the top would obliterate the
        /// bottom. A fixed fraction crosses the whole range in the same handful of seconds either way.
        /// </summary>
        public const float LinkProbeRampFraction = 0.6f;

        /// <summary>
        /// How much round-trip history the quiet baseline is drawn from. Long enough that a transfer's own
        /// queuing never becomes the baseline it is measured against, short enough that a path which
        /// genuinely got slower is not fought forever.
        /// </summary>
        public const float LinkProbeBaselineWindowSeconds = 60f;

        /// <summary>Smallest absolute ramp step, so the proportional term still moves at the floor.</summary>
        public const float LinkProbeRampBytesPerSecond = 32L * 1024L;

        /// <summary>
        /// Fewest queued outgoing packets that can count as a backlog. The live threshold is whichever is
        /// larger, this or one control interval's worth of packets at the current rate — a fixed depth
        /// cannot mean the same thing at 1 Mb/s and at 25 Gb/s, where a perfectly healthy transfer keeps far
        /// more than this in flight at any instant.
        /// </summary>
        public const int LinkProbeQueueBackoffPackets = 96;

        public const float LinkProbeQueueBackoffFactor = 0.5f;

        /// <summary>
        /// Server egress one client may cause, in bytes per second, when the server has not said how much
        /// it can afford. A relayed packet costs this budget once per recipient the server forwards it to;
        /// peers on a direct link cost it nothing.
        ///
        /// Only a guess, and deliberately a timid one, because a client cannot see the far end of the
        /// relay — which is why a server that does advertise a figure overrides it outright rather than
        /// being averaged with it. Divided by the fan-out this is roughly 25 KB/s per sharer in a
        /// twenty-player instance, so an instance running on the fallback shares slowly no matter how much
        /// pipe either end has. Servers should set ImageShareEgressMegabitsPerSecond, the egress figure the
        /// model pickup reads too.
        /// </summary>
        public const long RelayEgressBudgetBytesPerSecond = 512L * 1024L;

        /// <summary>How much unspent budget either bucket may bank, in seconds.</summary>
        public const float ShareBandwidthBurstSeconds = 0.25f;

        /// <summary>
        /// Mirror of <c>BasisNetworkCommons.MaxUnfragmentedPayload</c>, the largest payload one datagram
        /// carries, which this engine-free code cannot reference. A Unity test fails if the two drift.
        /// </summary>
        public const int MaxUnfragmentedPayloadBytes = 988;

        /// <summary>Default bound on any remote position component, in metres, for pose validation.</summary>
        public const float MaxAbsPositionMeters = 1000000f;
    }
}
