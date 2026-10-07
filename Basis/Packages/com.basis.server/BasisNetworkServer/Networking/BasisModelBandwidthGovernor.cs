using Basis.Network.Core;
using BasisNetworkCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Basis.Network.Server.Generic
{
    /// <summary>
    /// Server-side rate control for model pickup traffic, in both directions: a per-sender bucket
    /// under relayed model uploads, and a per-receiver paced queue for model cache replays.
    ///
    /// Model-only on purpose. Images keep their own <see cref="BasisImageBandwidthGovernor"/>, and
    /// sharing nothing with it is what lets the model pickup be added or removed without touching
    /// image code. The price is that one sender sharing images and models at once may spend each
    /// budget in full, and one receiver may be replayed both at once.
    ///
    /// The rates are the image-share settings, read and never written. The model client paces its
    /// uploads against the same advertised <see cref="Configuration.ImageShareEgressMegabitsPerSecond"/>,
    /// so enforcing any other figure would either drop honest transfers or never bite.
    ///
    /// Overrun is dropped rather than queued, as for images: model chunks are ReliableOrdered, so a
    /// dropped relay ends that transfer and the receivers time it out, whereas queueing the overrun
    /// would grow exactly the server-side backlog this exists to prevent. A sender inside its budget
    /// is never dropped.
    /// </summary>
    public static class BasisModelBandwidthGovernor
    {
        /// <summary>Megabits per second to bytes per second.</summary>
        private const double MegabitsToBytes = 125_000.0;

        /// <summary>
        /// Seconds of budget a bucket may bank while idle. A share is a wall of chunks and then
        /// nothing; two seconds covers a chunk train across a tick boundary without funding a flood.
        /// </summary>
        private const double BurstSeconds = 2.0;

        /// <summary>How often the replay pump wakes while it has work.</summary>
        private const int PumpIntervalMs = 25;

        // ── Upload: per-sender egress buckets ────────────────────────────────────────────────

        private sealed class EgressBucket
        {
            public double Tokens;
            public long LastRefillTicks;
        }

        private static readonly ConcurrentDictionary<ushort, EgressBucket> Egress =
            new ConcurrentDictionary<ushort, EgressBucket>();

        private static long _droppedMessages;
        private static long _droppedBytes;

        /// <summary>Model relays refused because the sender was over its enforced budget.</summary>
        public static long DroppedMessages => Interlocked.Read(ref _droppedMessages);

        /// <summary>Bytes of server egress those refusals avoided, counting fan-out.</summary>
        public static long DroppedBytes => Interlocked.Read(ref _droppedBytes);

        private static double EnforcedEgressBytesPerSecond
        {
            get
            {
                Configuration configuration = NetworkServer.Configuration;
                if (configuration == null)
                {
                    return 0;
                }

                int megabits = configuration.ImageShareEgressMegabitsPerSecond;
                if (megabits <= 0)
                {
                    return 0; // nothing advertised means nothing to enforce
                }

                int percent = configuration.ImageShareEgressEnforcementPercent;
                if (percent < 100)
                {
                    percent = 100; // enforcing below what was advertised is never correct
                }

                return megabits * MegabitsToBytes * (percent / 100.0);
            }
        }

        /// <summary>
        /// Charges one model relay against its sender's budget. Returns false when the sender is
        /// over, in which case the caller must not relay the message.
        /// </summary>
        /// <param name="senderId">Player id of the sharer.</param>
        /// <param name="bytes">Wire bytes multiplied by the number of peers this will be sent to.</param>
        public static bool TryConsumeEgress(ushort senderId, long bytes)
        {
            double ratePerSecond = EnforcedEgressBytesPerSecond;
            if (ratePerSecond <= 0 || bytes <= 0)
            {
                return true; // disabled
            }

            if (!Egress.TryGetValue(senderId, out EgressBucket bucket))
            {
                // Looked up first so the relay path allocates one bucket per sender, not one per call.
                bucket = Egress.GetOrAdd(senderId, new EgressBucket
                {
                    Tokens = ratePerSecond * BurstSeconds,
                    LastRefillTicks = DateTime.UtcNow.Ticks,
                });
            }

            lock (bucket)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                double elapsedSeconds = (nowTicks - bucket.LastRefillTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 0)
                {
                    bucket.LastRefillTicks = nowTicks;
                    double ceiling = ratePerSecond * BurstSeconds;
                    bucket.Tokens = Math.Min(ceiling, bucket.Tokens + ratePerSecond * elapsedSeconds);
                }

                // Gate on having something rather than on the whole charge fitting, and let the
                // bucket go negative: a single chunk times a wide fan-out can exceed the whole burst,
                // and demanding it fit would stall that sender forever. Going negative repays itself
                // at the configured rate, so the long-run average is still exactly the budget.
                if (bucket.Tokens <= 0)
                {
                    Interlocked.Increment(ref _droppedMessages);
                    Interlocked.Add(ref _droppedBytes, bytes);
                    return false;
                }

                bucket.Tokens -= bytes;
                return true;
            }
        }

        // ── Download: paced cache replay ─────────────────────────────────────────────────────

        /// <summary>A cached payload plus the owner it must be stamped with on the way out.</summary>
        public readonly struct PendingPayload
        {
            public readonly ushort OwnerId;
            public readonly byte[] Payload;

            public PendingPayload(ushort ownerId, byte[] payload)
            {
                OwnerId = ownerId;
                Payload = payload;
            }
        }

        /// <summary>One receiving peer's outstanding replay, drained by the pump at the configured rate.</summary>
        private sealed class ReplayJob
        {
            /// <summary>The key this job is filed under in <see cref="Replays"/>; fixed at creation.</summary>
            public int PeerId;
            public NetPeer Peer;
            public List<PendingPayload> Payloads;
            public int Cursor;
            public double Tokens;
            public long LastRefillTicks;

            /// <summary>
            /// Guards <see cref="Payloads"/>, <see cref="Cursor"/> and <see cref="Retired"/>. The cache
            /// appends to a live job from the relay thread while the pump is draining it.
            /// </summary>
            public readonly object Sync = new object();

            /// <summary>
            /// Set under <see cref="Sync"/> once the job is finished or dropped, so an append racing
            /// the removal starts a fresh job instead of writing into one nobody will drain.
            /// </summary>
            public bool Retired;
        }

        /// <summary>
        /// Outstanding replays by peer id. A plain dictionary under <see cref="ReplaysSync"/>: the pump
        /// copies the jobs into its own reused list once per pass rather than enumerating a
        /// concurrent dictionary, which allocated an enumerator every 25 ms while replays were queued.
        /// </summary>
        private static readonly Dictionary<int, ReplayJob> Replays = new Dictionary<int, ReplayJob>();

        /// <summary>
        /// Guards <see cref="Replays"/> and nothing else. Lock order is job Sync, then
        /// <see cref="PumpSync"/>, then this; nothing takes either of the others while holding it.
        /// </summary>
        private static readonly object ReplaysSync = new object();

        /// <summary>Guards the pump fields below.</summary>
        private static readonly object PumpSync = new object();
        private static Thread _pump;
        private static bool _pumpRunning;

        /// <summary>
        /// Bumped by <see cref="Reset"/>. A pump thread stops once the generation it was started under
        /// is gone, so a quick server stop/start can never leave two pumps draining one queue.
        /// </summary>
        private static int _pumpGeneration;

        /// <summary>
        /// Whether enqueuing starts the background pump. Tests turn this off and drive
        /// <see cref="PumpOnceForTests"/> themselves, so a rate assertion is not a race against a timer.
        /// </summary>
        public static bool AutoPump = true;

        private static double ReplayBytesPerSecond
        {
            get
            {
                Configuration configuration = NetworkServer.Configuration;
                if (configuration == null)
                {
                    return 0;
                }
                int megabits = configuration.ImageShareDownloadMegabitsPerSecond;
                return megabits <= 0 ? 0 : megabits * MegabitsToBytes;
            }
        }

        /// <summary>
        /// Queues a peer's model replay to be delivered at the configured rate. Returns false when
        /// pacing is disabled, in which case the caller sends inline.
        /// </summary>
        public static bool EnqueueReplay(NetPeer peer, List<PendingPayload> payloads)
        {
            if (peer == null || payloads == null || payloads.Count == 0)
            {
                return false;
            }

            double ratePerSecond = ReplayBytesPerSecond;
            if (ratePerSecond <= 0)
            {
                return false;
            }

            // Append rather than replace: a peer asks for models one at a time as it walks into range
            // of each, and overwriting the job would throw away whatever had not gone out yet.
            ReplayJob existing;
            lock (ReplaysSync)
            {
                Replays.TryGetValue(peer.Id, out existing);
            }
            if (existing != null)
            {
                lock (existing.Sync)
                {
                    if (!existing.Retired)
                    {
                        existing.Payloads.AddRange(payloads);
                        existing.Peer = peer;
                        EnsurePump();
                        return true;
                    }
                }
            }

            ReplayJob job = new ReplayJob
            {
                PeerId = peer.Id,
                Peer = peer,
                Payloads = payloads,
                Cursor = 0,
                Tokens = ratePerSecond * BurstSeconds,
                LastRefillTicks = DateTime.UtcNow.Ticks,
            };

            lock (ReplaysSync)
            {
                Replays[peer.Id] = job;
            }
            EnsurePump();
            return true;
        }

        /// <summary>
        /// Starts the pump if it is not running. The pump exits once the queue is empty, so a server
        /// that never paces a model replay - or has finished one - keeps no thread for it.
        /// </summary>
        private static void EnsurePump()
        {
            if (!AutoPump)
            {
                return;
            }

            lock (PumpSync)
            {
                if (_pumpRunning)
                {
                    return;
                }
                _pumpRunning = true;
                int generation = _pumpGeneration;
                _pump = new Thread(() => PumpLoop(generation))
                {
                    Name = "BasisModelReplayPump",
                    IsBackground = true,
                };
                _pump.Start();
            }
        }

        private static void PumpLoop(int generation)
        {
            bool faultLogged = false;
            // One per pump thread, reused every pass: a stale pump finishing its last pass after a
            // Reset never shares a list with the pump that replaced it.
            List<ReplayJob> pass = new List<ReplayJob>();
            while (true)
            {
                try
                {
                    PumpOnce(generation, pass);
                }
                catch (Exception exception)
                {
                    // A replay is a nicety; never let it take the pump thread down. Logged once per
                    // pump, so a fault that repeats every pass cannot flood the log.
                    if (!faultLogged)
                    {
                        faultLogged = true;
                        BNL.LogError($"[ModelReplay] pump error: {exception.Message}");
                    }
                }

                // Checked under the same lock EnsurePump takes: a job added after this sees the pump
                // stopped and starts a new one, a job added before it keeps this one running.
                lock (PumpSync)
                {
                    if (generation != _pumpGeneration)
                    {
                        return;
                    }
                    bool empty;
                    lock (ReplaysSync)
                    {
                        empty = Replays.Count == 0;
                    }
                    if (empty)
                    {
                        _pumpRunning = false;
                        _pump = null;
                        return;
                    }
                }

                Thread.Sleep(PumpIntervalMs);
            }
        }

        /// <summary>
        /// One pass over the jobs queued when it starts. A job added during the pass is drained from
        /// the next one, 25 ms later. <paramref name="pass"/> is the caller's scratch; it is left empty.
        /// </summary>
        private static void PumpOnce(int generation, List<ReplayJob> pass)
        {
            pass.Clear();
            lock (ReplaysSync)
            {
                if (Replays.Count == 0)
                {
                    return;
                }
                foreach (KeyValuePair<int, ReplayJob> pair in Replays)
                {
                    pass.Add(pair.Value);
                }
            }

            try
            {
                PumpJobs(generation, pass);
            }
            finally
            {
                // Holds no job (and so no payload list) alive between passes.
                pass.Clear();
            }
        }

        private static void PumpJobs(int generation, List<ReplayJob> pass)
        {
            double ratePerSecond = ReplayBytesPerSecond;
            int jobCount = pass.Count;
            for (int index = 0; index < jobCount; index++)
            {
                if (generation != Volatile.Read(ref _pumpGeneration))
                {
                    return; // retired by Reset mid-pass
                }

                ReplayJob job = pass[index];

                // A rate turned off under us drops the remaining work rather than holding the payload
                // list alive. Departure is handled by RemovePeer on the disconnect path, and a send
                // that races a disconnect is harmless: TrySend is what it says it is.
                if (ratePerSecond <= 0 || job.Peer == null)
                {
                    lock (job.Sync)
                    {
                        job.Retired = true;
                    }
                    RemoveJob(job.PeerId, job);
                    continue;
                }

                long nowTicks = DateTime.UtcNow.Ticks;
                double elapsedSeconds = (nowTicks - job.LastRefillTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 0)
                {
                    job.LastRefillTicks = nowTicks;
                    double ceiling = ratePerSecond * BurstSeconds;
                    job.Tokens = Math.Min(ceiling, job.Tokens + ratePerSecond * elapsedSeconds);
                }

                while (job.Tokens > 0)
                {
                    PendingPayload pending;
                    lock (job.Sync)
                    {
                        if (job.Cursor >= job.Payloads.Count)
                        {
                            break;
                        }
                        pending = job.Payloads[job.Cursor];
                        job.Cursor++;
                    }

                    int size = pending.Payload?.Length ?? 0;
                    if (size == 0)
                    {
                        continue;
                    }

                    job.Tokens -= size;
                    BasisNetworkModelCache.SendReplay(job.Peer, pending.OwnerId, pending.Payload);
                }

                bool retired;
                lock (job.Sync)
                {
                    retired = job.Cursor >= job.Payloads.Count;
                    job.Retired = retired;
                }
                if (retired)
                {
                    RemoveJob(job.PeerId, job);
                }
            }
        }

        /// <summary>
        /// Removes this exact job. Matched on the value, not just the key: an append that saw the job
        /// retired may already have put a fresh one under the same peer id, and that one must stay.
        /// </summary>
        private static void RemoveJob(int peerId, ReplayJob job)
        {
            lock (ReplaysSync)
            {
                if (Replays.TryGetValue(peerId, out ReplayJob filed) && ReferenceEquals(filed, job))
                {
                    Replays.Remove(peerId);
                }
            }
        }

        /// <summary>Forgets a peer's bucket and any queued replay. Called when they disconnect.</summary>
        public static void RemovePeer(int peerId)
        {
            ReplayJob job;
            bool removed;
            lock (ReplaysSync)
            {
                removed = Replays.Remove(peerId, out job);
            }
            if (removed)
            {
                lock (job.Sync)
                {
                    job.Retired = true;
                }
            }
            if (peerId >= 0 && peerId <= ushort.MaxValue)
            {
                Egress.TryRemove((ushort)peerId, out _);
            }
        }

        /// <summary>Drops all state and retires the pump thread. Called on server start and stop, and from tests.</summary>
        public static void Reset()
        {
            lock (PumpSync)
            {
                _pumpRunning = false;
                _pumpGeneration++;
                _pump = null;
            }
            AutoPump = true;
            lock (ReplaysSync)
            {
                Replays.Clear();
            }
            Egress.Clear();
            Interlocked.Exchange(ref _droppedMessages, 0);
            Interlocked.Exchange(ref _droppedBytes, 0);
        }

        /// <summary>Test seam: runs one pump pass without the background thread.</summary>
        public static void PumpOnceForTests() => PumpOnce(Volatile.Read(ref _pumpGeneration), new List<ReplayJob>());

        /// <summary>Test seam: whether a peer still has replay work outstanding.</summary>
        public static bool HasPendingReplay(int peerId)
        {
            lock (ReplaysSync)
            {
                return Replays.ContainsKey(peerId);
            }
        }

        /// <summary>Test seam: the pump thread currently running, if any.</summary>
        public static Thread PumpThreadForTests
        {
            get
            {
                lock (PumpSync)
                {
                    return _pump;
                }
            }
        }
    }
}
