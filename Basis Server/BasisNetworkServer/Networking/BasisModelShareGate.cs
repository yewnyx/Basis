using Basis.Network.Core;
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using static BasisPermissions.PermissionManager;

namespace Basis.Network.Server.Generic
{
    public enum BasisModelSpawnVerdict : byte
    {
        Allowed = 0,
        PropsLocked = 1,
        NotPermitted = 2,
        NoIdentity = 3,
    }

    /// <summary>
    /// Server half of the props rules for model pickups. Mirrors the prop-load check
    /// (BasisServerHandleEvents.LoadResource): new models need basis.resource.load.prop, and while
    /// PropsLocked also basis.resource.lockbypass.prop. Only NEW shares are refused: a model fully
    /// shared before the lock keeps re-sending to arrivals, moving and despawning, the way loaded
    /// props stay.
    ///
    /// "Fully shared" is checked, not assumed: a re-send skips the permission lookup only when its
    /// header matches what was admitted (same totalBytes and totalChunks) and every chunk of it was
    /// seen from the owner. Otherwise a sharer could register header-only ids before a lock and pour
    /// new content into them afterwards. What remains is a sharer replacing a model it had fully
    /// shared with different bytes of exactly the same size and chunk count.
    ///
    /// Covers relayed copies only; P2P-direct copies never reach the server.
    /// </summary>
    public static class BasisModelShareGate
    {
        public const int MaxTrackedTransfersPerOwner = 64;
        public const int NoticeIntervalSeconds = 10;
        public const string LockedNotice = "Model sharing is currently disabled by an admin.";

        /// <summary>
        /// Largest transfer whose chunks are tracked: the biggest model at 16 KiB chunks. A larger
        /// count is never treated as fully shared, so its re-sends are always re-checked.
        /// </summary>
        public const int MaxTrackedChunks = 2048;

        private const byte OpSpawn = 1;
        private const byte OpChunk = 2;
        private const byte OpDespawn = 4;
        private const int OpcodeBytes = 1;
        private const int GuidBytes = 16;
        private const int HeaderBytes = OpcodeBytes + GuidBytes;
        private const int ChunkIndexBytes = 4;

        private sealed class Transfer
        {
            public ushort OwnerId;
            public bool Denied;
            public int TotalBytes;
            public int TotalChunks;
            public int ChunksSeen;

            /// <summary>One bit per chunk index, allocated on the first counted chunk and dropped once all are in.</summary>
            public byte[] SeenChunks;

            public bool FullyShared => TotalChunks > 0 && ChunksSeen == TotalChunks;
        }

        /// <summary>Guards everything below except <see cref="LastNoticeTicks"/>. Never held across a permission lookup.</summary>
        private static readonly object Sync = new object();
        private static readonly Dictionary<Guid, Transfer> Transfers = new Dictionary<Guid, Transfer>();
        private static readonly Dictionary<ushort, int> PerOwner = new Dictionary<ushort, int>();

        private static readonly ConcurrentDictionary<ushort, long> LastNoticeTicks = new ConcurrentDictionary<ushort, long>();
        private static readonly long NoticeIntervalTicks = TimeSpan.TicksPerSecond * NoticeIntervalSeconds;

        /// <summary>When <see cref="AllowRelay"/> last logged a fault, in UTC ticks; 0 before the first.</summary>
        private static long _lastFaultLogTicks;

        /// <summary>
        /// Decides whether one model payload may be relayed. Never throws. Transforms, claims, cache
        /// requests and unknown opcodes always pass; spawns and chunks are what carry new content.
        /// </summary>
        public static bool AllowRelay(NetPeer sender, byte[] payload, int payloadLength)
        {
            if (sender == null || payload == null || payloadLength < HeaderBytes || payloadLength > payload.Length)
            {
                // Nothing to gate, and the cache ignores it too.
                return true;
            }

            byte opcode = payload[0];
            try
            {
                switch (opcode)
                {
                    case OpSpawn:
                        return AllowSpawn(sender, payload, payloadLength);
                    case OpChunk:
                        return AllowChunk((ushort)sender.Id, payload, payloadLength);
                    case OpDespawn:
                        ForgetDespawned((ushort)sender.Id, ReadGuid(payload));
                        return true;
                    default:
                        return true;
                }
            }
            catch (Exception e)
            {
                // A fault here must not stop the relay thread. A spawn fails closed, since letting it
                // through would bypass the lock; anything else is not content and passes. Logged at
                // most once per interval: this runs once per relayed model message. A limiter of its
                // own, so a fault never spends the sender's lock notice.
                if (TryTakeFaultLog())
                {
                    BNL.LogError($"Model share gate failed on a payload from peer {sender.Id}: {e.Message}");
                }
                return opcode != OpSpawn;
            }
        }

        public static BasisModelSpawnVerdict EvaluateSpawn(NetPeer sender)
        {
            if (sender == null)
            {
                return BasisModelSpawnVerdict.NoIdentity;
            }

            try
            {
                string uuid = null;
                var identity = NetworkServer.AuthIdentity;
                if (identity == null || !identity.NetIDToUUID(sender, out uuid) || string.IsNullOrEmpty(uuid))
                {
                    // Same fallback as disconnect cleanup: the stored connect metadata carries the
                    // server-computed UUID, so model sharing works with UseAuthIdentity off too.
                    uuid = BasisSavedState.GetLastPlayerMetaData(sender, out var meta) ? meta.playerUUID : null;
                }
                return EvaluateSpawnForUuid(uuid);
            }
            catch (Exception e)
            {
                if (TryTakeNotice((ushort)sender.Id))
                {
                    BNL.LogWarning($"Model share permission check for peer {sender.Id} failed: {e.Message}");
                }
                return BasisModelSpawnVerdict.NotPermitted;
            }
        }

        /// <summary>The prop-load rule in the order LoadResource applies it.</summary>
        public static BasisModelSpawnVerdict EvaluateSpawnForUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid))
            {
                return BasisModelSpawnVerdict.NoIdentity;
            }
            // The string overload: the NetPeer one logs an error on every miss.
            if (BasisGlobalLockManager.PropsLocked && !PermissionIntegration.HasValidRequirement(uuid, PermNodes.ResourceLockBypassProp))
            {
                return BasisModelSpawnVerdict.PropsLocked;
            }
            if (!PermissionIntegration.HasValidRequirement(uuid, PermNodes.ResourceLoadProp))
            {
                return BasisModelSpawnVerdict.NotPermitted;
            }
            return BasisModelSpawnVerdict.Allowed;
        }

        /// <summary>Forgets a departed sharer's transfers. A temporary list is fine on the disconnect path.</summary>
        public static void RemovePeer(int peerId)
        {
            if (peerId < 0 || peerId > ushort.MaxValue)
            {
                return;
            }
            ushort ownerId = (ushort)peerId;
            lock (Sync)
            {
                List<Guid> doomed = new List<Guid>();
                foreach (KeyValuePair<Guid, Transfer> pair in Transfers)
                {
                    if (pair.Value.OwnerId == ownerId)
                    {
                        doomed.Add(pair.Key);
                    }
                }
                for (int index = 0; index < doomed.Count; index++)
                {
                    Transfers.Remove(doomed[index]);
                }
                PerOwner.Remove(ownerId);
            }
            LastNoticeTicks.TryRemove(ownerId, out _);
        }

        public static void Reset()
        {
            lock (Sync)
            {
                Transfers.Clear();
                PerOwner.Clear();
            }
            LastNoticeTicks.Clear();
            Interlocked.Exchange(ref _lastFaultLogTicks, 0);
        }

        public static int TrackedCountForTests(ushort ownerId)
        {
            lock (Sync)
            {
                return PerOwner.TryGetValue(ownerId, out int tracked) ? tracked : 0;
            }
        }

        public static bool IsDeniedForTests(Guid id)
        {
            lock (Sync)
            {
                return Transfers.TryGetValue(id, out Transfer transfer) && transfer.Denied;
            }
        }

        private static bool AllowSpawn(NetPeer sender, byte[] payload, int payloadLength)
        {
            ushort senderId = (ushort)sender.Id;
            Guid id = ReadGuid(payload);
            bool hasTotals = BasisNetworkModelCache.TryReadSpawnHeader(payload, payloadLength, out int totalBytes, out int totalChunks, out _)
                && totalChunks > 0;

            if (hasTotals)
            {
                lock (Sync)
                {
                    if (Transfers.TryGetValue(id, out Transfer known)
                        && known.OwnerId == senderId
                        && !known.Denied
                        && known.TotalBytes == totalBytes
                        && known.TotalChunks == totalChunks
                        && known.FullyShared)
                    {
                        // A re-send of a model this sharer finished sharing while allowed.
                        return true;
                    }
                }
            }

            // Outside Sync: permission lookups take their own lock.
            BasisModelSpawnVerdict verdict = EvaluateSpawn(sender);
            bool allowed = verdict == BasisModelSpawnVerdict.Allowed;
            if (hasTotals)
            {
                lock (Sync)
                {
                    RecordLocked(id, senderId, !allowed, totalBytes, totalChunks);
                }
            }
            if (!allowed)
            {
                Notify(sender, verdict);
            }
            return allowed;
        }

        private static void RecordLocked(Guid id, ushort ownerId, bool denied, int totalBytes, int totalChunks)
        {
            if (Transfers.TryGetValue(id, out Transfer existing))
            {
                if (existing.OwnerId != ownerId)
                {
                    // A second sender can never rewrite another's entry.
                    return;
                }
                existing.Denied = denied;
                if (existing.TotalBytes != totalBytes || existing.TotalChunks != totalChunks)
                {
                    // New content under an old id: what was seen before says nothing about these bytes.
                    existing.TotalBytes = totalBytes;
                    existing.TotalChunks = totalChunks;
                    existing.ChunksSeen = 0;
                    existing.SeenChunks = null;
                }
                return;
            }

            PerOwner.TryGetValue(ownerId, out int tracked);
            if (tracked >= MaxTrackedTransfersPerOwner)
            {
                // Untracked: its chunks relay, and receivers that never saw the spawn discard them.
                // A re-send is evaluated again.
                return;
            }

            Transfers[id] = new Transfer
            {
                OwnerId = ownerId,
                Denied = denied,
                TotalBytes = totalBytes,
                TotalChunks = totalChunks,
            };
            PerOwner[ownerId] = tracked + 1;
        }

        private static bool AllowChunk(ushort senderId, byte[] payload, int payloadLength)
        {
            if (payloadLength < HeaderBytes + ChunkIndexBytes)
            {
                return true;
            }

            Guid id = ReadGuid(payload);
            int chunkIndex = BitConverter.ToInt32(payload, HeaderBytes);
            lock (Sync)
            {
                if (!Transfers.TryGetValue(id, out Transfer transfer) || transfer.OwnerId != senderId)
                {
                    // Not a tracked share of this sender's: receivers only take chunks from the owner.
                    return true;
                }
                if (transfer.Denied)
                {
                    return false;
                }
                CountChunkLocked(transfer, chunkIndex);
                return true;
            }
        }

        private static void CountChunkLocked(Transfer transfer, int chunkIndex)
        {
            if (transfer.FullyShared
                || transfer.TotalChunks > MaxTrackedChunks
                || chunkIndex < 0
                || chunkIndex >= transfer.TotalChunks)
            {
                return;
            }

            byte[] seen = transfer.SeenChunks;
            if (seen == null)
            {
                seen = new byte[(transfer.TotalChunks + 7) >> 3];
                transfer.SeenChunks = seen;
            }

            int slot = chunkIndex >> 3;
            byte bit = (byte)(1 << (chunkIndex & 7));
            if ((seen[slot] & bit) != 0)
            {
                return;
            }
            seen[slot] |= bit;
            transfer.ChunksSeen++;
            if (transfer.FullyShared)
            {
                transfer.SeenChunks = null;
            }
        }

        /// <summary>
        /// Owner-only, like the model cache's despawn: if anyone's despawn cleared the entry, anyone
        /// could clear a sharer's id and re-register it as their own.
        /// </summary>
        private static void ForgetDespawned(ushort senderId, Guid id)
        {
            lock (Sync)
            {
                if (!Transfers.TryGetValue(id, out Transfer transfer) || transfer.OwnerId != senderId)
                {
                    return;
                }
                Transfers.Remove(id);
                if (PerOwner.TryGetValue(senderId, out int tracked))
                {
                    if (tracked <= 1)
                    {
                        PerOwner.Remove(senderId);
                    }
                    else
                    {
                        PerOwner[senderId] = tracked - 1;
                    }
                }
            }
        }

        /// <summary>
        /// Logs every refusal the throttle lets through and, for the lock only, tells the sharer -
        /// the same split as the prop path, which messages for the lock and only logs a missing
        /// permission.
        /// </summary>
        private static void Notify(NetPeer sender, BasisModelSpawnVerdict verdict)
        {
            if (!TryTakeNotice((ushort)sender.Id))
            {
                return;
            }

            BNL.Log($"Model share from peer {sender.Id} refused: {verdict}.");
            if (verdict == BasisModelSpawnVerdict.PropsLocked)
            {
                BasisPlayerModeration.SendBackMessage(sender, LockedNotice);
            }
        }

        /// <summary>At most one notice per sender per interval. Two racing refusals may both pass; that is harmless.</summary>
        private static bool TryTakeNotice(ushort senderId)
        {
            long now = DateTime.UtcNow.Ticks;
            if (LastNoticeTicks.TryGetValue(senderId, out long last) && now - last < NoticeIntervalTicks)
            {
                return false;
            }
            LastNoticeTicks[senderId] = now;
            return true;
        }

        /// <summary>At most one fault line per notice interval, across all senders. Two racing faults may both log; that is harmless.</summary>
        private static bool TryTakeFaultLog()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastFaultLogTicks);
            if (last != 0 && now - last < NoticeIntervalTicks)
            {
                return false;
            }
            Interlocked.Exchange(ref _lastFaultLogTicks, now);
            return true;
        }

        private static Guid ReadGuid(byte[] payload)
        {
            return new Guid(new ReadOnlySpan<byte>(payload, OpcodeBytes, GuidBytes));
        }
    }
}
