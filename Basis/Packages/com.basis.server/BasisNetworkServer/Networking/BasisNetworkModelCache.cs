using Basis.Network.Core;
using BasisNetworkCore;
using System;
using System.Collections.Generic;
using System.Threading;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    /// <summary>
    /// Keeps the bytes of every shared 3D model pickup (glTF/GLB) in server RAM so the rest of the
    /// room can be given a model the sharer only sent to the players standing near it, instead of the
    /// sharer having to send it again to every arrival.
    ///
    /// Self-contained: its own manager id, lock, budget, upload meter and replay pump
    /// (<see cref="BasisModelBandwidthGovernor"/>). It shares no state or code with the image cache,
    /// so the model pickup can be added or removed without the image side noticing, and a room full
    /// of pictures can never evict a model or the reverse.
    ///
    /// The cache is deliberately dumb about what a model *is*: it retains the client's own scene
    /// payloads verbatim and replays them stamped with the original owner's player id. A receiving
    /// client therefore sees exactly the OpSpawn/OpChunk stream it would have got from the owner,
    /// and needs no knowledge that the server answered instead.
    ///
    /// What it hands a player first is an offer - the sharer's own spawn header, opcode swapped - and
    /// nothing moves until that client measures the distance for itself and asks. The one thing the
    /// cache does read out of a payload is where a model has got to: a spawn header records where it
    /// was put, and pickups get carried around, so the pose the room last saw is kept and written over
    /// the header's before an offer or a replay goes out. Everything else stays opaque bytes,
    /// including the tail after the pose that carries the model's claims.
    ///
    /// Lifetime matches what clients already do with pickups, so the cache can never show a joiner
    /// something the room cannot see: an entry is dropped on its owner's despawn, when its owner
    /// disconnects, and when it is evicted - and the owner is told whenever that last one happens.
    /// </summary>
    public static class BasisNetworkModelCache
    {
        /// <summary>
        /// The model manager registers under this fixed string, so the server can resolve which
        /// dynamic NetworkID carries model traffic. Must match <c>BasisModelWire.FixedNetworkIdentifier</c>
        /// on the client.
        /// </summary>
        public const string ModelManagerIdentifier = "BasisModelPickupManager";

        /// <summary>
        /// Spawn headers are replayed unmetered as offers to every joiner, so a larger one is relayed
        /// live but never cached.
        /// </summary>
        public const int MaxSpawnHeaderBytes = 4096;

        /// <summary>
        /// Bounds a sharer's offer catalogue: 16 x 4 KiB of unmetered offers per sharer per joiner. A
        /// seventeenth model evicts that sharer's oldest.
        /// </summary>
        public const int MaxModelsPerOwner = 16;

        /// <summary>The model client's remote pose limit; a cached pose beyond it would make every offer unplaceable.</summary>
        public const float MaxAbsPositionMeters = 1e5f;

        // Mirrored from the model pickup manager. Wire protocol - changing either side is a break.
        private const byte OpSpawn = 1;
        private const byte OpChunk = 2;
        private const byte OpTransform = 3;
        private const byte OpDespawn = 4;

        /// <summary>
        /// Server to owner: "I am holding this model" / "I am no longer holding it". The owner skips
        /// re-sending held models to each arrival, which is the whole point of the buffer. Sent only
        /// to the owner and stamped with their own player id - the relay never echoes a sender to
        /// itself, so a message arriving under your own id cannot have come from another client.
        /// </summary>
        private const byte OpServerCacheState = 8;

        /// <summary>
        /// Server to client: "I am holding a model; here is its header, decide for yourself whether
        /// you want it." The sharer's own spawn header with only the opcode byte and the pose changed.
        /// </summary>
        private const byte OpServerCacheOffer = 9;

        /// <summary>
        /// Client to server: "send me that one." The client addresses it to itself, so it reaches the
        /// cache through the ordinary relay observation path without being broadcast to the room.
        /// </summary>
        private const byte OpServerCacheRequest = 10;

        private const int OpcodeBytes = 1;
        private const int GuidBytes = 16;
        private const int HeaderBytes = OpcodeBytes + GuidBytes;

        /// <summary>Position and rotation, seven floats, written right after totalChunks in a spawn header.</summary>
        private const int PoseBytes = (3 + 4) * sizeof(float);

        /// <summary>opcode + guid + pose + scale: 49 bytes. Fixed, so anything of another length is not one.</summary>
        private const int TransformBytes = HeaderBytes + PoseBytes + sizeof(float);

        /// <summary>Ceiling on a single owner name, matching the client's own read guard.</summary>
        private const int MaxOwnerNameBytes = 1024;

        private const long BytesPerMegabyte = 1024L * 1024L;

        // Pose sanity. Generous next to anything a client sends (unit quaternions, scale near 1), tight
        // enough that a value no receiver will place can never become the pose an offer carries.
        private const float MinRotationLengthSquared = 0.25f;
        private const float MaxRotationLengthSquared = 4f;
        private const float MaxTransformScale = 1000f;

        /// <summary>A cached model: one OpSpawn header plus every OpChunk it announced.</summary>
        private sealed class CachedModel
        {
            public ushort OwnerId;
            public long Sequence;

            /// <summary>Who has been told this model exists. One offer per player, ever.</summary>
            public readonly HashSet<ushort> Offered = new HashSet<ushort>();

            /// <summary>
            /// Who has actually been sent the bytes. Separate from <see cref="Offered"/> because a
            /// player may sit on an offer for as long as they like before walking close enough to want
            /// it, and because a repeated request must not buy a second copy.
            /// </summary>
            public readonly HashSet<ushort> Delivered = new HashSet<ushort>();

            public byte[] Spawn;
            public byte[][] Chunks;
            public int ChunksHeld;

            /// <summary>
            /// Where the pose sits inside <see cref="Spawn"/>. Walked once when the header is
            /// admitted rather than stepped over the owner name again on every offer.
            /// </summary>
            public int PoseOffset;

            /// <summary>
            /// The last pose the room was told about, or null while the model has not moved since it
            /// was shared. Retained verbatim like every other payload, so replaying it needs no new
            /// receive code - and it carries scale, which a spawn header does not.
            /// </summary>
            public byte[] Transform;

            public long Bytes;

            /// <summary>Servable once its spawn header and every chunk has landed.</summary>
            public bool Complete => Spawn != null && Chunks != null && ChunksHeld == Chunks.Length;
        }

        /// <summary>Running per-owner totals, so fairness checks are a lookup instead of a scan per chunk.</summary>
        private sealed class OwnerTally
        {
            public long Bytes;
            public int Entries;
        }

        /// <summary>
        /// Every held model. A plain dictionary: every read and write of it happens under
        /// <see cref="Gate"/>, so the eviction scans walk it with the struct enumerator instead of
        /// allocating a concurrent one per scan.
        /// </summary>
        private static readonly Dictionary<Guid, CachedModel> Models = new Dictionary<Guid, CachedModel>();

        /// <summary>Every read and mutation of entries, tallies and totals happens under this.</summary>
        private static readonly object Gate = new object();
        private static readonly Dictionary<ushort, OwnerTally> Owners = new Dictionary<ushort, OwnerTally>();

        private static long _totalBytes;
        private static long _sequence;

        /// <summary>
        /// When <see cref="Observe"/> last logged a fault, in UTC ticks. A payload shape that keeps
        /// throwing would otherwise log once per relayed model message.
        /// </summary>
        private static long _lastFaultLogTicks;

        private const long FaultLogIntervalTicks = TimeSpan.TicksPerSecond * 10;

        /// <summary>Bytes currently held. Diagnostics and tests.</summary>
        public static long TotalBytes => Interlocked.Read(ref _totalBytes);

        /// <summary>Number of complete or in-flight models held. Takes <see cref="Gate"/>; diagnostics and tests.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Models.Count;
                }
            }
        }

        /// <summary>
        /// How many held models are complete enough to hand to a joiner. Differs from
        /// <see cref="Count"/> while a share is still arriving. Takes <see cref="Gate"/>.
        /// </summary>
        public static int ServableCount
        {
            get
            {
                lock (Gate)
                {
                    int servable = 0;
                    foreach (KeyValuePair<Guid, CachedModel> pair in Models)
                    {
                        if (pair.Value.Complete)
                        {
                            servable++;
                        }
                    }
                    return servable;
                }
            }
        }

        /// <summary>Bytes currently held on behalf of one player.</summary>
        public static long BytesHeldFor(ushort ownerId)
        {
            lock (Gate)
            {
                return OwnerBytes(ownerId);
            }
        }

        private static bool Enabled => NetworkServer.Configuration?.ModelCacheEnabled ?? false;

        private static long MaxBytes
        {
            get
            {
                int configured = NetworkServer.Configuration?.ModelCacheMaxMegabytes ?? 0;
                return configured > 0 ? configured * BytesPerMegabyte : 0;
            }
        }

        /// <summary>
        /// Every owner is guaranteed this much before fair-share division applies, so a busy
        /// instance cannot shrink each person's allowance to something that fits no model at all.
        /// </summary>
        private static long MinimumOwnerBytes
        {
            get
            {
                int configured = NetworkServer.Configuration?.ModelCacheMinimumPerOwnerMegabytes ?? 0;
                return configured > 0 ? configured * BytesPerMegabyte : 0;
            }
        }

        public static void Reset()
        {
            lock (Gate)
            {
                Models.Clear();
                Owners.Clear();
                Interlocked.Exchange(ref _totalBytes, 0);
                _sequence = 0;
            }
            Interlocked.Exchange(ref _lastFaultLogTicks, 0);
        }

        /// <summary>
        /// The model manager's dynamic message index, read from the id database on every use rather
        /// than memoised. Ids restart from 0 whenever the instance empties and a single mapping can be
        /// removed, so a remembered id would go on matching whatever object took it next - and that
        /// object's opcode-1 messages would be run through the props gate. A lock-free dictionary
        /// read per scene message is small next to deserialising the message.
        /// </summary>
        public static bool TryGetManagerNetId(out ushort managerNetId)
        {
            return BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(ModelManagerIdentifier, out managerNetId);
        }

        /// <summary>True when this scene message is model traffic. Called for every non-image scene message.</summary>
        public static bool IsModelTraffic(ushort messageIndex)
        {
            return TryGetManagerNetId(out ushort managerNetId) && managerNetId == messageIndex;
        }

        /// <summary>
        /// Feeds one relayed model payload to the cache. The caller keeps relaying exactly as before -
        /// this only observes, so a cache that rejects or misparses a message can never stop the live
        /// send. <paramref name="payload"/> is copied where retained; the caller may reuse it.
        /// </summary>
        public static void Observe(
            ushort senderId,
            byte[] payload,
            int payloadLength,
            ushort[] recipients = null,
            int recipientsSize = 0
        )
        {
            if (!Enabled || payload == null || payloadLength < HeaderBytes || payloadLength > payload.Length)
            {
                return;
            }

            try
            {
                switch (payload[0])
                {
                    case OpSpawn:
                        ObserveSpawn(senderId, payload, payloadLength, recipients, recipientsSize);
                        break;
                    case OpChunk:
                        ObserveChunk(senderId, payload, payloadLength);
                        break;
                    case OpTransform:
                        ObserveTransform(payload, payloadLength);
                        break;
                    case OpServerCacheRequest:
                        ServeRequestedModel(senderId, ReadGuid(payload));
                        break;
                    case OpDespawn:
                        // Owner-only: if any peer's despawn evicted, anyone could clear another sharer's
                        // entry and immediately re-spawn the same id with content of their own. The
                        // owner echoes a despawn it honours from someone else, and that echo evicts.
                        Remove(ReadGuid(payload), senderId, ownerOnly: true);
                        break;
                }
            }
            catch (Exception e)
            {
                // Never let a malformed payload take down the relay thread; the live send already
                // happened and a client that sends nonsense simply goes uncached. Rate-limited: this
                // runs once per relayed model message.
                if (TryTakeFaultLog())
                {
                    BNL.LogError($"Model cache ignored a malformed payload from {senderId}: {e.Message}");
                }
            }
        }

        private static void ObserveSpawn(
            ushort senderId,
            byte[] payload,
            int payloadLength,
            ushort[] recipients,
            int recipientsSize
        )
        {
            if (payloadLength > MaxSpawnHeaderBytes)
            {
                // Spawn headers are replayed unmetered as offers to every joiner, so an oversized one
                // stays live-only. The relay is unaffected.
                return;
            }

            Guid id = ReadGuid(payload);
            if (!TryReadSpawnHeader(payload, payloadLength, out _, out int totalChunks, out int poseOffset) || totalChunks <= 0)
            {
                return;
            }
            if (poseOffset + PoseBytes > payloadLength || !TryReadPose(payload, poseOffset, out _))
            {
                // A spawn no receiver could place would be offered to every joiner and dropped by each.
                return;
            }

            lock (Gate)
            {
                if (Models.ContainsKey(id))
                {
                    // Already tracked. A join re-send repeats the spawn header; keep the first copy.
                    return;
                }

                // Charge the chunk-array backbone, not just the header. totalChunks is client-supplied
                // and `new byte[totalChunks][]` costs totalChunks references; with the backbone charged
                // an implausible count trips `cost > cap` and is refused before anything is allocated.
                long cost = (long)payloadLength + (long)totalChunks * IntPtr.Size;

                // Checked before the count cap so a spawn that could never be admitted does not cost
                // the owner an older model on the way to being refused.
                long cap = MaxBytes;
                if (cap <= 0 || cost > cap || cost > OwnerShare(senderId, cap))
                {
                    return;
                }
                while (OwnerEntries(senderId) >= MaxModelsPerOwner)
                {
                    if (!EvictOldestOwnedBy(senderId, id))
                    {
                        return;
                    }
                }

                if (!TryReserve(senderId, cost, id))
                {
                    return;
                }

                CachedModel entry = new CachedModel
                {
                    OwnerId = senderId,
                    Sequence = ++_sequence,
                    Spawn = Copy(payload, payloadLength),
                    PoseOffset = poseOffset,
                    Chunks = new byte[totalChunks][],
                    Bytes = cost,
                };
                SeedAlreadyHeld(entry, recipients, recipientsSize);
                Models[id] = entry;
                AddToTally(senderId, cost, 1);
                Interlocked.Add(ref _totalBytes, cost);
            }
        }

        /// <summary>
        /// Remembers where a model has got to. Taken from whoever sent it rather than from the owner
        /// alone, because control of a pickup passes to whoever picks it up. That is no wider a trust
        /// surface than it appears: the relay has already handed these exact bytes to the whole room,
        /// and all the cache does is keep what everybody has already been told - provided it is a pose
        /// a receiver can place. Anyone can send one, so an unplaceable one is ignored rather than
        /// allowed to hide the model from every later joiner.
        /// </summary>
        private static void ObserveTransform(byte[] payload, int payloadLength)
        {
            if (payloadLength != TransformBytes || !IsPlaceableTransform(payload))
            {
                return;
            }

            Guid id = ReadGuid(payload);
            lock (Gate)
            {
                if (!Models.TryGetValue(id, out CachedModel entry))
                {
                    return;
                }

                if (entry.Transform == null)
                {
                    // Charged and allocated once. Every later pose is copied over the same buffer,
                    // so a model being dragged around the room costs neither budget nor garbage
                    // after the first update. A replay takes a snapshot instead (ServeRequestedModel),
                    // because the pump sends what it was handed outside Gate.
                    if (!TryReserve(entry.OwnerId, TransformBytes, id))
                    {
                        return;
                    }
                    entry.Bytes += TransformBytes;
                    AddToTally(entry.OwnerId, TransformBytes, 0);
                    Interlocked.Add(ref _totalBytes, TransformBytes);
                    entry.Transform = Copy(payload, payloadLength);
                    return;
                }

                Buffer.BlockCopy(payload, 0, entry.Transform, 0, TransformBytes);
            }
        }

        private static void ObserveChunk(ushort senderId, byte[] payload, int payloadLength)
        {
            // opcode + guid + chunkIndex(4) + length(4) + bytes
            const int ChunkIndexOffset = HeaderBytes;
            if (payloadLength < ChunkIndexOffset + 8)
            {
                return;
            }

            Guid id = ReadGuid(payload);
            int chunkIndex = BitConverter.ToInt32(payload, ChunkIndexOffset);
            if (chunkIndex < 0)
            {
                return;
            }

            bool becameServable;
            lock (Gate)
            {
                if (!Models.TryGetValue(id, out CachedModel entry) || entry.OwnerId != senderId)
                {
                    return;
                }

                byte[][] slots = entry.Chunks;
                if (slots == null || chunkIndex >= slots.Length || slots[chunkIndex] != null)
                {
                    return;
                }

                long cost = payloadLength;
                if (!TryReserve(senderId, cost, id))
                {
                    // A refused chunk is never sent again, so this entry can no longer complete. Drop
                    // it now: it stops holding bytes and blocking a later re-send of the same id, and
                    // the rest of this transfer's chunks stop at the lookup above instead of each
                    // repeating the eviction scan.
                    Drop(id);
                    return;
                }

                slots[chunkIndex] = Copy(payload, payloadLength);
                entry.ChunksHeld++;
                entry.Bytes += cost;
                AddToTally(senderId, cost, 0);
                Interlocked.Add(ref _totalBytes, cost);
                becameServable = entry.Complete;
            }

            if (becameServable)
            {
                NotifyOwner(senderId, id, held: true);
                OfferToRoom(id);
            }
        }

        /// <summary>
        /// Makes room for <paramref name="cost"/> on behalf of <paramref name="ownerId"/>. Fairness
        /// is per owner: an owner over their share evicts their OWN oldest models and never anyone
        /// else's, so one person filling the buffer cannot push out everybody else's.
        /// Caller must hold <see cref="Gate"/>.
        /// </summary>
        private static bool TryReserve(ushort ownerId, long cost, Guid exclude)
        {
            long cap = MaxBytes;
            if (cap <= 0 || cost <= 0 || cost > cap)
            {
                return false;
            }

            long share = OwnerShare(ownerId, cap);
            if (cost > share)
            {
                // No amount of evicting this owner's other models makes a single oversized one fit.
                return false;
            }

            while (OwnerBytes(ownerId) + cost > share)
            {
                if (!EvictOldestOwnedBy(ownerId, exclude))
                {
                    return false;
                }
            }

            while (Interlocked.Read(ref _totalBytes) + cost > cap)
            {
                if (!EvictOldestOfHeaviestOwner(exclude))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Each distinct owner gets an equal slice of the buffer, never less than the configured
        /// minimum. The owner being admitted is counted even when they hold nothing yet, so the
        /// first model of a new sharer is sized against the room they are about to join.
        /// </summary>
        private static long OwnerShare(ushort ownerId, long cap)
        {
            int owners = Owners.Count + (Owners.ContainsKey(ownerId) ? 0 : 1);
            long share = cap / Math.Max(1, owners);
            long floor = Math.Min(cap, MinimumOwnerBytes);
            return Math.Max(share, floor);
        }

        private static long OwnerBytes(ushort ownerId)
        {
            return Owners.TryGetValue(ownerId, out OwnerTally tally) ? tally.Bytes : 0;
        }

        private static int OwnerEntries(ushort ownerId)
        {
            return Owners.TryGetValue(ownerId, out OwnerTally tally) ? tally.Entries : 0;
        }

        private static void AddToTally(ushort ownerId, long bytes, int entries)
        {
            if (!Owners.TryGetValue(ownerId, out OwnerTally tally))
            {
                tally = new OwnerTally();
                Owners[ownerId] = tally;
            }
            tally.Bytes += bytes;
            tally.Entries += entries;
        }

        private static bool EvictOldestOwnedBy(ushort ownerId, Guid exclude)
        {
            Guid oldest = Guid.Empty;
            long oldestSequence = long.MaxValue;
            bool found = false;

            foreach (KeyValuePair<Guid, CachedModel> pair in Models)
            {
                if (pair.Value.OwnerId != ownerId || pair.Key == exclude)
                {
                    continue;
                }
                if (pair.Value.Sequence < oldestSequence)
                {
                    oldestSequence = pair.Value.Sequence;
                    oldest = pair.Key;
                    found = true;
                }
            }

            return found && Drop(oldest);
        }

        private static bool EvictOldestOfHeaviestOwner(Guid exclude)
        {
            ushort heaviest = 0;
            long heaviestBytes = -1;
            foreach (KeyValuePair<ushort, OwnerTally> pair in Owners)
            {
                if (pair.Value.Bytes > heaviestBytes)
                {
                    heaviestBytes = pair.Value.Bytes;
                    heaviest = pair.Key;
                }
            }

            return heaviestBytes >= 0 && EvictOldestOwnedBy(heaviest, exclude);
        }

        /// <summary>Caller must hold <see cref="Gate"/>.</summary>
        private static bool Drop(Guid id)
        {
            if (!Models.Remove(id, out CachedModel entry))
            {
                return false;
            }
            Interlocked.Add(ref _totalBytes, -entry.Bytes);
            if (Owners.TryGetValue(entry.OwnerId, out OwnerTally tally))
            {
                tally.Bytes -= entry.Bytes;
                tally.Entries--;
                if (tally.Entries <= 0)
                {
                    Owners.Remove(entry.OwnerId);
                }
            }

            // Tell the owner they are back on the hook for this one. Without it an evicted model
            // would silently stop reaching new arrivals: the owner still believes we hold it and
            // skips re-sending, and we no longer have anything to send.
            if (entry.Complete)
            {
                NotifyOwner(entry.OwnerId, id, held: false);
            }
            return true;
        }

        /// <summary>
        /// Tells one player whether the server is holding a given model of theirs. Best effort: a
        /// peer that has already gone simply is not there to tell.
        /// </summary>
        private static void NotifyOwner(ushort ownerId, Guid id, bool held)
        {
            if (!TryGetManagerNetId(out ushort managerNetId))
            {
                return;
            }
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(ownerId, out NetPeer owner))
            {
                return;
            }

            byte[] payload = new byte[HeaderBytes + 1];
            payload[0] = OpServerCacheState;
            id.TryWriteBytes(new Span<byte>(payload, OpcodeBytes, GuidBytes));
            payload[HeaderBytes] = held ? (byte)1 : (byte)0;

            NetDataWriter writer = NetworkServer.RentWriter();
            SendPayload(owner, writer, managerNetId, ownerId, payload);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Drops a cached model. <paramref name="ownerOnly"/> restricts it to the player who shared
        /// it, which is what an OpDespawn off the wire always gets: anyone may ask, only the owner's
        /// word removes the server's copy.
        /// </summary>
        public static bool Remove(Guid id, ushort requesterId, bool ownerOnly)
        {
            lock (Gate)
            {
                if (!Models.TryGetValue(id, out CachedModel entry))
                {
                    return false;
                }
                if (ownerOnly && entry.OwnerId != requesterId)
                {
                    return false;
                }
                return Drop(id);
            }
        }

        /// <summary>
        /// Drops everything a departing player shared, and forgets them as a recipient so whoever is
        /// given the recycled id next starts with nothing offered or delivered. Clients already
        /// destroy a leaver's pickups, so keeping them cached would hand a joiner models nobody else
        /// can see.
        /// </summary>
        public static void RemovePlayerModels(int peerId)
        {
            if (peerId < 0 || peerId > ushort.MaxValue)
            {
                return;
            }
            ushort ownerId = (ushort)peerId;
            lock (Gate)
            {
                List<Guid> doomed = new List<Guid>();
                foreach (KeyValuePair<Guid, CachedModel> pair in Models)
                {
                    if (pair.Value.OwnerId == ownerId)
                    {
                        doomed.Add(pair.Key);
                    }
                }

                int doomedCount = doomed.Count;
                for (int index = 0; index < doomedCount; index++)
                {
                    Drop(doomed[index]);
                }

                foreach (KeyValuePair<Guid, CachedModel> pair in Models)
                {
                    pair.Value.Offered.Remove(ownerId);
                    pair.Value.Delivered.Remove(ownerId);
                }
            }
        }

        /// <summary>
        /// Tells an arriving peer what the room is holding, in the order it was shared, and stops
        /// there. Each offer is the sharer's own spawn header - at most 4 KiB - so a joiner arriving
        /// into a full instance pays for a catalogue rather than the models, and decides locally which
        /// are close enough to be worth asking for.
        /// </summary>
        public static void OfferCachedModelsToPeer(NetPeer newConnection)
        {
            if (!Enabled || newConnection == null)
            {
                return;
            }
            if (!TryGetManagerNetId(out ushort managerNetId))
            {
                return;
            }

            int peerId = newConnection.Id;
            if (peerId < 0 || peerId > ushort.MaxValue)
            {
                return;
            }
            ushort recipient = (ushort)peerId;

            List<byte[]> offers = new List<byte[]>();
            lock (Gate)
            {
                List<KeyValuePair<Guid, CachedModel>> ordered = new List<KeyValuePair<Guid, CachedModel>>(Models);
                ordered.Sort((left, right) => left.Value.Sequence.CompareTo(right.Value.Sequence));

                int orderedCount = ordered.Count;
                for (int index = 0; index < orderedCount; index++)
                {
                    CachedModel entry = ordered[index].Value;
                    if (!ShouldOffer(entry, recipient))
                    {
                        continue;
                    }
                    entry.Offered.Add(recipient);
                    offers.Add(BuildOffer(entry));
                }
            }

            int offerCount = offers.Count;
            if (offerCount == 0)
            {
                return;
            }

            // Offers go out unmetered and stamped with the recipient's own id rather than the
            // sharer's: they are small, and the client only trusts an offer under its own id.
            NetDataWriter writer = NetworkServer.RentWriter();
            for (int index = 0; index < offerCount; index++)
            {
                SendPayload(newConnection, writer, managerNetId, recipient, offers[index]);
            }
            NetworkServer.ReturnWriter(writer);

            BNL.Log($"Model cache offered {offerCount} model(s) to peer {peerId}.");
        }

        /// <summary>
        /// Tells everyone already in the room about a model that has just finished arriving. The
        /// sharer only sent it to the players it considered close enough, so without this the rest of
        /// the room would never learn the model exists.
        /// </summary>
        private static void OfferToRoom(Guid id)
        {
            if (!Enabled || !TryGetManagerNetId(out ushort managerNetId))
            {
                return;
            }

            foreach (KeyValuePair<int, NetPeer> pair in NetworkServer.AuthenticatedPeers)
            {
                int peerId = pair.Key;
                if (peerId < 0 || peerId > ushort.MaxValue || pair.Value == null)
                {
                    continue;
                }
                ushort recipient = (ushort)peerId;

                byte[] offer;
                lock (Gate)
                {
                    if (!Models.TryGetValue(id, out CachedModel entry) || !ShouldOffer(entry, recipient))
                    {
                        continue;
                    }
                    entry.Offered.Add(recipient);
                    offer = BuildOffer(entry);
                }

                NetDataWriter writer = NetworkServer.RentWriter();
                SendPayload(pair.Value, writer, managerNetId, recipient, offer);
                NetworkServer.ReturnWriter(writer);
            }
        }

        private static bool ShouldOffer(CachedModel entry, ushort recipient)
        {
            return entry.Complete
                && entry.OwnerId != recipient
                && !entry.Offered.Contains(recipient)
                && !entry.Delivered.Contains(recipient);
        }

        /// <summary>
        /// An offer is the spawn header with one byte changed. The position inside it is what the
        /// client measures its distance against, so it says where the model is now rather than where
        /// it was first put.
        /// </summary>
        private static byte[] BuildOffer(CachedModel entry)
        {
            byte[] offer = BuildSpawn(entry);
            offer[0] = OpServerCacheOffer;
            return offer;
        }

        /// <summary>
        /// The retained spawn header with the latest pose written over the one it was shared at.
        /// Copying rather than mutating keeps the retained header exactly as the sharer wrote it, and
        /// a replay already queued for somebody else keeps the bytes it was handed.
        /// </summary>
        private static byte[] BuildSpawn(CachedModel entry)
        {
            byte[] spawn = new byte[entry.Spawn.Length];
            Buffer.BlockCopy(entry.Spawn, 0, spawn, 0, entry.Spawn.Length);
            if (entry.Transform != null && entry.PoseOffset > 0 && entry.PoseOffset + PoseBytes <= spawn.Length)
            {
                Buffer.BlockCopy(entry.Transform, HeaderBytes, spawn, entry.PoseOffset, PoseBytes);
            }
            return spawn;
        }

        /// <summary>
        /// A client asking for one of the models it was offered. This is the only thing that moves
        /// model bytes out of the cache, and it moves them once per player per model.
        /// </summary>
        public static void ServeRequestedModel(ushort requesterId, Guid id)
        {
            if (!Enabled || !TryGetManagerNetId(out ushort managerNetId))
            {
                return;
            }
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(requesterId, out NetPeer peer) || peer == null)
            {
                return;
            }

            // Ordering matters on the wire: a chunk before its spawn header is discarded by the receiver.
            List<BasisModelBandwidthGovernor.PendingPayload> queued = new List<BasisModelBandwidthGovernor.PendingPayload>();
            lock (Gate)
            {
                if (!Models.TryGetValue(id, out CachedModel entry))
                {
                    return;
                }
                if (!entry.Complete || entry.OwnerId == requesterId)
                {
                    return;
                }
                if (!entry.Delivered.Add(requesterId))
                {
                    return;
                }
                entry.Offered.Add(requesterId);

                queued.Add(new BasisModelBandwidthGovernor.PendingPayload(entry.OwnerId, BuildSpawn(entry)));
                if (entry.Transform != null)
                {
                    // Ahead of the chunks rather than after them: the receiver raises its placeholder
                    // off the header, so pose and scale land while the model is still loading. A
                    // snapshot: later poses are copied over entry.Transform under Gate, while the
                    // pump sends this one outside it.
                    queued.Add(new BasisModelBandwidthGovernor.PendingPayload(entry.OwnerId, Copy(entry.Transform, entry.Transform.Length)));
                }
                int chunkCount = entry.Chunks.Length;
                for (int chunk = 0; chunk < chunkCount; chunk++)
                {
                    queued.Add(new BasisModelBandwidthGovernor.PendingPayload(entry.OwnerId, entry.Chunks[chunk]));
                }
            }

            // Paced when the operator has set a download rate, inline when they have not: a small
            // instance on a fast LAN has nothing to gain from metering, and 0 means "as fast as it
            // will go" rather than some hidden default.
            if (BasisModelBandwidthGovernor.EnqueueReplay(peer, queued))
            {
                BNL.Log($"Model cache queued {queued.Count} payload(s) for requesting peer {requesterId} (paced).");
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            int queuedCount = queued.Count;
            for (int index = 0; index < queuedCount; index++)
            {
                SendPayload(peer, writer, managerNetId, queued[index].OwnerId, queued[index].Payload);
            }
            NetworkServer.ReturnWriter(writer);

            BNL.Log($"Model cache served {queuedCount} payload(s) to requesting peer {requesterId}.");
        }

        /// <summary>
        /// One paced payload, from the replay pump. Resolves the manager id at send time rather than
        /// taking one captured at enqueue, because a replay spans many milliseconds and the id can be
        /// reset by the instance emptying in between.
        /// </summary>
        public static void SendReplay(NetPeer peer, ushort ownerId, byte[] payload)
        {
            if (peer == null || !TryGetManagerNetId(out ushort managerNetId))
            {
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                SendPayload(peer, writer, managerNetId, ownerId, payload);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        /// <summary>Test seam: recomputes every owner's bytes and entry count from the entries and compares.</summary>
        public static bool OwnerTallyMatchesEntriesForTests()
        {
            lock (Gate)
            {
                Dictionary<ushort, (long Bytes, int Entries)> expected = new Dictionary<ushort, (long Bytes, int Entries)>();
                long total = 0;
                foreach (KeyValuePair<Guid, CachedModel> pair in Models)
                {
                    expected.TryGetValue(pair.Value.OwnerId, out (long Bytes, int Entries) held);
                    expected[pair.Value.OwnerId] = (held.Bytes + pair.Value.Bytes, held.Entries + 1);
                    total += pair.Value.Bytes;
                }

                if (expected.Count != Owners.Count || total != Interlocked.Read(ref _totalBytes))
                {
                    return false;
                }
                foreach (KeyValuePair<ushort, (long Bytes, int Entries)> pair in expected)
                {
                    if (!Owners.TryGetValue(pair.Key, out OwnerTally tally)
                        || tally.Bytes != pair.Value.Bytes
                        || tally.Entries != pair.Value.Entries)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>Everything the cache sends goes out ReliableOrdered on the direct scene channel, stamped with <paramref name="ownerId"/>.</summary>
        private static void SendPayload(NetPeer peer, NetDataWriter writer, ushort managerNetId, ushort ownerId, byte[] payload)
        {
            if (payload == null)
            {
                return;
            }

            ServerSceneDataMessage message = new ServerSceneDataMessage
            {
                sceneDataMessage = new RemoteSceneDataMessage
                {
                    messageIndex = managerNetId,
                    payload = payload,
                    payloadLength = payload.Length,
                },
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = ownerId,
                },
            };

            writer.Reset();
            message.Serialize(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered);
        }

        private static Guid ReadGuid(byte[] payload)
        {
            return new Guid(new ReadOnlySpan<byte>(payload, OpcodeBytes, GuidBytes));
        }

        /// <summary>At most one fault line per interval, across all senders. Two racing faults may both log; that is harmless.</summary>
        private static bool TryTakeFaultLog()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastFaultLogTicks);
            if (last != 0 && now - last < FaultLogIntervalTicks)
            {
                return false;
            }
            Interlocked.Exchange(ref _lastFaultLogTicks, now);
            return true;
        }

        private static byte[] Copy(byte[] payload, int payloadLength)
        {
            byte[] copy = new byte[payloadLength];
            Buffer.BlockCopy(payload, 0, copy, 0, payloadLength);
            return copy;
        }

        /// <summary>
        /// Position within <see cref="MaxAbsPositionMeters"/> on every axis, rotation finite and no
        /// longer than twice unit. NaN fails every comparison, so it is refused without a separate
        /// check. The rotation's lower bound is left to <see cref="IsPlaceableTransform"/>: a spawn is
        /// the owner's own word about their own model, and a later transform replaces its pose anyway.
        /// </summary>
        private static bool TryReadPose(byte[] payload, int offset, out float rotationLengthSquared)
        {
            rotationLengthSquared = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!(Math.Abs(BitConverter.ToSingle(payload, offset + axis * sizeof(float))) <= MaxAbsPositionMeters))
                {
                    return false;
                }
            }

            float lengthSquared = 0f;
            for (int axis = 0; axis < 4; axis++)
            {
                float component = BitConverter.ToSingle(payload, offset + (3 + axis) * sizeof(float));
                lengthSquared += component * component;
            }
            if (!(lengthSquared <= MaxRotationLengthSquared))
            {
                return false;
            }

            rotationLengthSquared = lengthSquared;
            return true;
        }

        /// <summary>A transform any peer may send: placeable pose, a real rotation, and a positive bounded scale.</summary>
        private static bool IsPlaceableTransform(byte[] payload)
        {
            if (!TryReadPose(payload, HeaderBytes, out float rotationLengthSquared) || rotationLengthSquared < MinRotationLengthSquared)
            {
                return false;
            }
            float scale = BitConverter.ToSingle(payload, HeaderBytes + PoseBytes);
            return scale > 0f && scale <= MaxTransformScale;
        }

        /// <summary>
        /// Reads totalBytes and totalChunks out of an OpSpawn header, and where the pose that follows
        /// them begins. The owner name in front of them is a BinaryWriter string - a 7-bit encoded byte
        /// length then UTF-8 - so the fields after it sit at a variable offset and have to be walked to
        /// rather than indexed. Shared with <see cref="BasisModelShareGate"/>.
        /// </summary>
        public static bool TryReadSpawnHeader(byte[] payload, int payloadLength, out int totalBytes, out int totalChunks, out int poseOffset)
        {
            totalBytes = 0;
            totalChunks = 0;
            poseOffset = 0;
            if (payload == null || payloadLength > payload.Length)
            {
                return false;
            }

            int offset = HeaderBytes + 2; // opcode + guid + ushort ownerId
            if (!TrySkipWireString(payload, payloadLength, ref offset))
            {
                return false;
            }

            // fieldA, fieldB (format and tail length for models), totalBytes, totalChunks
            offset += 4 + 4;
            if (offset + 4 + 4 > payloadLength)
            {
                return false;
            }

            totalBytes = BitConverter.ToInt32(payload, offset);
            totalChunks = BitConverter.ToInt32(payload, offset + 4);
            poseOffset = offset + 8;
            return true;
        }

        /// <summary>
        /// Marks whoever the sharer already sent this model to as holding it. The relay knows
        /// precisely who that was, and without seeding it here the cache would offer the same model
        /// back to everyone who was standing nearby when it was shared. An untargeted share went to
        /// the whole room, so everyone counts. Caller must hold <see cref="Gate"/>.
        /// </summary>
        private static void SeedAlreadyHeld(CachedModel entry, ushort[] recipients, int recipientsSize)
        {
            if (recipients != null && recipientsSize > 0)
            {
                int count = Math.Min(recipientsSize, recipients.Length);
                for (int index = 0; index < count; index++)
                {
                    entry.Offered.Add(recipients[index]);
                    entry.Delivered.Add(recipients[index]);
                }
                return;
            }

            foreach (KeyValuePair<int, NetPeer> pair in NetworkServer.AuthenticatedPeers)
            {
                int peerId = pair.Key;
                if (peerId >= 0 && peerId <= ushort.MaxValue)
                {
                    entry.Offered.Add((ushort)peerId);
                    entry.Delivered.Add((ushort)peerId);
                }
            }
        }

        private static bool TrySkipWireString(byte[] payload, int payloadLength, ref int offset)
        {
            int length = 0;
            int shift = 0;
            while (true)
            {
                if (offset >= payloadLength || shift > 4 * 7)
                {
                    return false;
                }

                byte piece = payload[offset++];
                length |= (piece & 0x7F) << shift;
                if ((piece & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
            }

            if (length < 0 || length > MaxOwnerNameBytes || offset + length > payloadLength)
            {
                return false;
            }

            offset += length;
            return true;
        }
    }
}
