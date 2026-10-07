using System;
using System.Collections.Generic;
using Basis.Scripts.Networking;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Reads the model's own data out of an offered spawn (the bytes after the pose). False refuses the offer;
    /// the caller logs.
    /// </summary>
    public delegate bool BasisModelOfferParser(
        in BasisModelSpawnHeader header,
        ReadOnlySpan<byte> message,
        out BasisModelOfferInfo offer
    );

    public delegate bool BasisModelLocalIdProvider(out ushort localId);

    public delegate bool BasisModelViewerProvider(out Vector3 position);

    /// <summary>
    /// The client side of the server's model cache. A server that runs one tells an owner whether it holds the
    /// owner's model (so the owner stops providing it), and offers held models to everyone else as a copy of
    /// the spawn header; a server without one never sends either, and owners keep sending themselves. This
    /// client measures each offer's distance itself and asks for the ones within range: the server never learns
    /// where anybody is, and an offer costs a header rather than the payload.
    ///
    /// Cache messages arrive stamped with the recipient's own player id. The relay never echoes a sender back
    /// to itself, so only the server can produce one, and that check is what makes them unforgeable.
    /// </summary>
    public sealed class BasisModelServerCacheClient
    {
        /// <summary>One pending offer: where the item last was, and what the parser read out of its header.</summary>
        public struct Offer
        {
            public Vector3 Position;
            public BasisModelOfferInfo Data;
        }

        private const int RangeJobBatchSize = 32;
        private const int MinimumRangeCapacity = 8;

        /// <summary>Overridable so tests can run without a connection.</summary>
        public BasisModelLocalIdProvider LocalId = BasisNetworkConnection.TryGetLocalPlayerID;

        public BasisModelViewerProvider Viewer = BasisModelReplicationScan.TryGetLocalViewerPosition;

        /// <summary>Null takes every offer with a default <see cref="BasisModelOfferInfo"/>.</summary>
        public BasisModelOfferParser ParseOffer;

        /// <summary>
        /// Null always requests. False keeps an in-range offer pending instead of losing it, so an offer
        /// refused while receiving is off or a budget is full is asked for once that clears.
        /// </summary>
        public Func<Guid, BasisModelOfferInfo, bool> CanRequest;

        /// <summary>
        /// Bounds an offer's position. The server validates the poses it caches; this keeps a bad one out of
        /// the range pass anyway. The manager tightens it to the model's remote limits.
        /// </summary>
        public BasisModelPoseLimits OfferPoseLimits = new BasisModelPoseLimits(
            BasisModelShareSettings.MaxAbsPositionMeters,
            0f,
            float.MaxValue
        );

        private readonly IBasisModelPacketSink _sink;
        private readonly BasisModelRangeReporter _range;
        private readonly Func<Guid, bool> _isKnownLocally;
        private readonly float _rangeCheckIntervalSeconds;
        private readonly string _logPrefix;
        private readonly string _itemNounPlural;
        private readonly BasisDebug.LogTag _logTag;

        /// <summary>Cap on distinct pending offers; zero or less is unlimited.</summary>
        public readonly int MaxPendingOffers;

        /// <summary>Offers waiting for the player to come in range (or for <see cref="CanRequest"/> to allow them).</summary>
        public readonly Dictionary<Guid, Offer> Offers = new Dictionary<Guid, Offer>();

        /// <summary>Our items the server holds and offers to arrivals itself, so we stop providing them.</summary>
        private readonly HashSet<Guid> _held = new HashSet<Guid>();

        /// <summary>Items we asked the server for and have not seen arrive or go.</summary>
        private readonly HashSet<Guid> _requested = new HashSet<Guid>();

        private readonly ushort[] _selfRecipient = new ushort[1];
        private Guid[] _rangeIds = Array.Empty<Guid>();
        private NativeArray<Vector3> _rangePositions;
        private NativeArray<byte> _rangeResults;
        private NativeArray<float> _rangeDistances;
        private JobHandle _rangeHandle;
        private int _rangeCount;
        private bool _rangeScheduled;
        private float _nextRangeCheckTime;

        // Each said once until ClearOffers: offers and cache messages arrive per message, and a server sending
        // junk sends a lot of it.
        private bool _capWarned;
        private bool _invalidPoseWarned;
        private bool _malformedWarned;

        /// <param name="maxPendingOffers">Zero or less is unlimited.</param>
        public BasisModelServerCacheClient(
            IBasisModelPacketSink sink,
            BasisModelRangeReporter range,
            Func<Guid, bool> isKnownLocally,
            float rangeCheckIntervalSeconds,
            int maxPendingOffers,
            string logPrefix,
            string itemNounPlural,
            BasisDebug.LogTag logTag
        )
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _range = range ?? throw new ArgumentNullException(nameof(range));
            _isKnownLocally = isKnownLocally ?? throw new ArgumentNullException(nameof(isKnownLocally));
            _rangeCheckIntervalSeconds = rangeCheckIntervalSeconds;
            MaxPendingOffers = maxPendingOffers;
            _logPrefix = logPrefix;
            _itemNounPlural = itemNounPlural;
            _logTag = logTag;
        }

        public int PendingOfferCount => Offers.Count;

        public bool IsHeld(Guid id)
        {
            return _held.Contains(id);
        }

        public bool TryGetOffer(Guid id, out BasisModelOfferInfo offer)
        {
            if (Offers.TryGetValue(id, out Offer entry))
            {
                offer = entry.Data;
                return true;
            }
            offer = default;
            return false;
        }

        /// <summary>
        /// True from the moment a request for <paramref name="id"/> is sent until <see cref="RemoveOffer"/> or
        /// <see cref="Forget"/>. The spawn handler reads it before removing the offer, so a replay it asked
        /// for is not refused by limits meant for unsolicited pushes.
        /// </summary>
        public bool WasRequested(Guid id)
        {
            return _requested.Contains(id);
        }

        /// <summary>The server saying whether it holds one of our items.</summary>
        public void HandleState(ushort senderId, byte[] message)
        {
            if (!BasisModelShareWire.TryReadCacheState(message, out Guid id, out bool held))
            {
                LogTruncated(senderId);
                return;
            }
            if (LocalId == null || !LocalId(out ushort localId) || senderId != localId)
                return;

            if (held)
                _held.Add(id);
            else
                _held.Remove(id);
        }

        /// <summary>
        /// The server offering an item we have not got. The body is the sharer's spawn with the opcode swapped,
        /// so it parses like a spawn up to the pose and then stops: an offer is a claim about an item, not the
        /// item.
        /// </summary>
        public void HandleOffer(ushort senderId, byte[] message)
        {
            if (LocalId == null || !LocalId(out ushort localId) || senderId != localId)
                return;

            if (!BasisModelShareWire.TryReadSpawn(message, out BasisModelSpawnHeader header, out BasisModelShareWireError error))
            {
                if (error == BasisModelShareWireError.Truncated)
                    LogTruncated(senderId);
                return;
            }

            Guid id = header.Id;
            if (_isKnownLocally(id))
                return;

            // The range pass would measure against a NaN or far-flung position forever.
            BasisModelPose pose = header.Pose;
            if (!BasisModelPoseValidation.TrySanitizePose(ref pose, OfferPoseLimits))
            {
                if (!_invalidPoseWarned)
                {
                    _invalidPoseWarned = true;
                    BasisDebug.LogWarning($"{_logPrefix} ignored a server offer with an invalid pose.", _logTag);
                }
                return;
            }

            BasisModelOfferInfo data = default;
            if (ParseOffer != null && !ParseOffer(in header, message, out data))
                return;

            if (MaxPendingOffers > 0 && Offers.Count >= MaxPendingOffers && !Offers.ContainsKey(id))
            {
                if (!_capWarned)
                {
                    _capWarned = true;
                    BasisDebug.LogWarning(
                        $"{_logPrefix}: {MaxPendingOffers} offers are already pending; ignoring further offers.",
                        _logTag
                    );
                }
                return;
            }

            Offers[id] = new Offer { Position = pose.Position.ToUnity(), Data = data };
        }

        /// <summary>
        /// Follows a broadcast transform for an item still on offer. Transforms reach the whole room whether or
        /// not you hold the item, so an item carried over to us does not stay out of range forever on the
        /// position its offer carried. Callers validate the pose first.
        /// </summary>
        public void OnTransform(Guid id, Vector3 position)
        {
            if (!Offers.TryGetValue(id, out Offer offer))
                return;
            offer.Position = position;
            Offers[id] = offer;
        }

        /// <summary>The item arrived (or is being admitted): stop tracking its offer and its request.</summary>
        public void RemoveOffer(Guid id)
        {
            Offers.Remove(id);
            _requested.Remove(id);
        }

        /// <summary>The item is gone: forget everything about it.</summary>
        public void Forget(Guid id)
        {
            _held.Remove(id);
            Offers.Remove(id);
            _requested.Remove(id);
        }

        public void ClearHeld()
        {
            _held.Clear();
        }

        public void ClearOffers()
        {
            Offers.Clear();
            _requested.Clear();
            _capWarned = false;
            _invalidPoseWarned = false;
            _malformedWarned = false;
        }

        public void ResetTimers()
        {
            _nextRangeCheckTime = 0f;
        }

        /// <summary>
        /// Starts the distance pass over every pending offer. Buffers are persistent and the ids are copied out,
        /// so a steady state allocates nothing and an offer arriving mid-tick cannot shift the indices the job
        /// reads. A pass left outstanding by an exception earlier in the tick is collected first.
        /// </summary>
        public void ScheduleRangeCheck(float now)
        {
            if (_rangeScheduled)
                CompleteRangeCheck();
            if (Offers.Count == 0)
                return;
            if (now < _nextRangeCheckTime)
                return;
            _nextRangeCheckTime = now + _rangeCheckIntervalSeconds;

            if (Viewer == null || !Viewer(out Vector3 viewer))
                return;

            int count = Offers.Count;
            EnsureRangeCapacity(count);

            int index = 0;
            foreach (KeyValuePair<Guid, Offer> entry in Offers)
            {
                _rangeIds[index] = entry.Key;
                _rangePositions[index] = entry.Value.Position;
                index++;
            }

            float rangeMeters = _range.ServerRangeMeters();
            _rangeCount = count;
            _rangeScheduled = true;
            _rangeHandle = new BasisModelOfferRangeJob
            {
                Positions = _rangePositions,
                Viewer = viewer,
                RangeSquared = rangeMeters * rangeMeters,
                InRange = _rangeResults,
                DistanceSquared = _rangeDistances,
            }.Schedule(count, RangeJobBatchSize);
            // Kick the workers now: the pass is meant to run while the rest of the tick does, not to start only
            // when CompleteRangeCheck joins it.
            JobHandle.ScheduleBatchedJobs();
        }

        /// <summary>
        /// Collects the pass and asks for whatever came out in range. Offers out of range, or held back by
        /// <see cref="CanRequest"/>, stay pending: the player may still walk toward them.
        /// </summary>
        public void CompleteRangeCheck()
        {
            if (!_rangeScheduled)
                return;
            _rangeHandle.Complete();
            _rangeScheduled = false;

            int requested = 0;
            int deferred = 0;
            int heldBack = 0;
            float nearestDeferredSq = float.MaxValue;

            int rangeCount = _rangeCount;
            _rangeCount = 0;
            for (int index = 0; index < rangeCount; index++)
            {
                if (_rangeResults[index] == 0)
                {
                    // Measured by the job from the viewer it was scheduled with; only out-of-range slots are read.
                    deferred++;
                    float distanceSq = _rangeDistances[index];
                    if (distanceSq < nearestDeferredSq)
                        nearestDeferredSq = distanceSq;
                    continue;
                }

                Guid id = _rangeIds[index];
                if (!Offers.TryGetValue(id, out Offer offer))
                    continue;
                if (CanRequest != null && !CanRequest(id, offer.Data))
                {
                    heldBack++;
                    continue;
                }
                Offers.Remove(id);
                requested++;
                SendRequest(id);
            }

            if (requested > 0)
            {
                // Only passes that actually ask for something say so; a player standing still next to a wall of
                // items they have already asked for stays quiet.
                string held = deferred > 0 && nearestDeferredSq < float.MaxValue
                    ? $", still holding {deferred:N0} out of range (nearest {Mathf.Sqrt(nearestDeferredSq):0.#}m)"
                    : deferred > 0
                        ? $", still holding {deferred:N0} out of range"
                        : string.Empty;
                if (heldBack > 0)
                    held += $", holding back {heldBack:N0} not wanted yet";
                BasisDebug.Log(
                    $"{_logPrefix} requested {requested:N0} offered {_itemNounPlural} within "
                        + $"{_range.ServerRangeMeters():0.##}m{held}.",
                    _logTag
                );
            }
        }

        /// <summary>Completes any pass and frees the native buffers. Idempotent.</summary>
        public void ReleaseResources()
        {
            if (_rangeScheduled)
            {
                _rangeHandle.Complete();
                _rangeScheduled = false;
            }
            _rangeCount = 0;
            _rangeIds = Array.Empty<Guid>();
            if (_rangePositions.IsCreated)
                _rangePositions.Dispose();
            if (_rangeResults.IsCreated)
                _rangeResults.Dispose();
            if (_rangeDistances.IsCreated)
                _rangeDistances.Dispose();
        }

        /// <summary>
        /// Asks the server for an offered item. Addressed to ourselves: the relay observes every model message
        /// on its way past, which is how the cache hears it, and a one-entry list keeps it off everyone else's
        /// wire. The echo lands back here and falls through the manager's dispatch unhandled.
        /// </summary>
        private void SendRequest(Guid id)
        {
            if (LocalId == null || !LocalId(out ushort localId))
                return;
            _selfRecipient[0] = localId;
            _sink.SendIdMessage(BasisModelShareWire.OpServerCacheRequest, id, _selfRecipient);
            _requested.Add(id);
        }

        private void EnsureRangeCapacity(int count)
        {
            if (_rangeIds.Length >= count && _rangePositions.IsCreated && _rangePositions.Length >= count)
                return;

            int capacity = Mathf.NextPowerOfTwo(Mathf.Max(count, MinimumRangeCapacity));
            _rangeIds = new Guid[capacity];
            if (_rangePositions.IsCreated)
                _rangePositions.Dispose();
            if (_rangeResults.IsCreated)
                _rangeResults.Dispose();
            if (_rangeDistances.IsCreated)
                _rangeDistances.Dispose();
            _rangePositions = new NativeArray<Vector3>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _rangeResults = new NativeArray<byte>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _rangeDistances = new NativeArray<float>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        private void LogTruncated(ushort senderId)
        {
            if (_malformedWarned)
                return;
            _malformedWarned = true;
            BasisDebug.LogWarning($"{_logPrefix}: malformed message from {senderId} (truncated).", _logTag);
        }
    }
}
