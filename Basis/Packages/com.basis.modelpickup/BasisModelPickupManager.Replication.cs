using System;
using System.Collections.Generic;
using Basis.ModelPickup.Validation;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.ModelPickup
{
    public static partial class BasisModelPickupManager
    {
        private static readonly List<BasisModelReplicationCandidate> _scratchCandidates = new List<BasisModelReplicationCandidate>(256);
        private static readonly List<ushort> _scratchRecipients = new List<ushort>(256);
        private static readonly byte[] _helloBuffer = new byte[BasisModelWire.HelloBytes];
        private static readonly ushort[] _helloRecipient = new ushort[1];

        /// <summary>A model this client shared and loaded. It keeps its canonical GLB to send to whoever comes in range.</summary>
        private sealed class OwnedModel
        {
            public BasisModelPickupObject Object;
            public byte[] CleanGlb;
            public BasisModelSpawnTail Tail;
            public string OwnerName;
            public string Label;
            public float GroundY;

            /// <summary>The size batch still waiting for its answer; null once decided.</summary>
            public BasisModelSizeBatch Batch;

            /// <summary>The size is decided, so the spawn header can say it: sent to peers and moves broadcast.</summary>
            public bool Replicable;

            public readonly HashSet<ushort> SentRecipients = new HashSet<ushort>();
        }

        /// <summary>One queued send of a model to one cohort; the header carries the spawn tail.</summary>
        public sealed class OutboundModelTransfer : BasisModelOutboundTransfer
        {
            public BasisModelSpawnTail Tail;
        }

        /// <summary>
        /// The model's half of the outbound queue. A transfer goes stale when its model is gone or not yet
        /// replicable, or its bytes were replaced, since a newer transfer then carries the right ones.
        /// </summary>
        public sealed class ModelOutboundSource : IBasisModelOutboundSource<OutboundModelTransfer>
        {
            public static ModelOutboundSource Instance = new ModelOutboundSource();

            public bool TryGetLiveRoot(OutboundModelTransfer transfer, out Transform liveRoot)
            {
                liveRoot = null;
                if (
                    !_owned.TryGetValue(transfer.Id, out OwnedModel owned)
                    || owned.Object == null
                    || !owned.Replicable
                    || !ReferenceEquals(owned.CleanGlb, transfer.Payload)
                )
                    return false;
                liveRoot = owned.Object.Root;
                return true;
            }

            public byte[] EncodeHeader(OutboundModelTransfer transfer, int totalChunks)
            {
                return BasisModelWire.EncodeSpawn(
                    transfer.Id,
                    transfer.OwnerId,
                    transfer.OwnerName,
                    transfer.Payload.Length,
                    totalChunks,
                    BasisModelShareConvert.ToSharePose(transfer.Position, transfer.Rotation),
                    transfer.Tail
                );
            }
        }

        /// <summary>Sends wherever the manager currently sends, so the cache client's requests follow a test's sink too.</summary>
        public sealed class RoutedSink : IBasisModelPacketSink
        {
            public static RoutedSink Instance = new RoutedSink();

            public void Send(byte[] buffer, DeliveryMethod deliveryMethod, ushort[] recipients)
            {
                Sink.Send(buffer, deliveryMethod, recipients);
            }
        }

        public static bool IsReplicable(Guid id)
        {
            return _owned.TryGetValue(id, out OwnedModel owned) && owned.Replicable;
        }

        /// <summary>A local job's import landed: the model is ours to share, as soon as its size is decided.</summary>
        private static void AdoptOwned(BasisModelJob job)
        {
            var owned = new OwnedModel
            {
                Object = job.Pickup,
                CleanGlb = job.CleanGlb,
                Tail = job.Tail,
                OwnerName = job.Pickup.OwnerName,
                Label = job.Label,
                GroundY = job.GroundY,
            };
            _owned[job.Id] = owned;

            // The job's seat in the batch moves to the owned model.
            BasisModelSizeBatch batch = job.Batch;
            job.Batch = null;
            if (batch != null && !batch.Decided)
                owned.Batch = batch;
            else
                MakeReplicable(owned, batch != null ? batch.Mode : job.Tail.SizeMode);
        }

        /// <summary>
        /// Fixes the size receivers will apply and opens the model to replication. The range pass is pulled in to
        /// this tick rather than run per model, so a whole batch resolving at once is gathered into one pass.
        /// </summary>
        private static void MakeReplicable(OwnedModel owned, BasisModelSizeMode mode)
        {
            BasisGlbAabb bounds = owned.Tail.Claims.Bounds;
            float scale = BasisModelSizing.ComputeBaseScale(mode, bounds.MaxExtent);
            owned.Tail.SizeMode = mode;
            owned.Tail.BaseScale = scale;
            owned.Batch = null;
            if (owned.Object != null)
            {
                owned.Object.SetShape(bounds, scale);
                owned.Object.LiftAboveGround(owned.GroundY, BasisModelPickupSettings.GroundClearanceMeters);
            }
            owned.Replicable = true;
            _nextRangeRefreshTime = 0f;
        }

        /// <summary>
        /// Every few seconds (or at once when something changed), queue each replicable model to the capable
        /// players in range who have not had it. Models the server holds are skipped: it offers them itself.
        /// </summary>
        private static void RefreshRangeRecipients(float now)
        {
            if (now < _nextRangeRefreshTime)
                return;
            _nextRangeRefreshTime = now + BasisModelPickupSettings.RecipientRangeRefreshSeconds;
            PurgeExpiredOfferState(now);
            if (_owned.Count == 0)
                return;

            // Never 0 as a sentinel: player 0 is valid.
            ushort ownerId = LocalId();
            if (ownerId == BasisModelShareNet.UnownedPlayerId)
                return;

            BasisModelReplicationScan.GatherCandidates(ownerId, _scratchCandidates);
            // Only peers that said hello have a handler for the model id; anyone else would park every chunk.
            _capable.Filter(_scratchCandidates);
            if (_scratchCandidates.Count == 0)
                return;

            float rangeMeters = _range.ServerRangeMeters();
            foreach (KeyValuePair<Guid, OwnedModel> entry in _owned)
            {
                OwnedModel owned = entry.Value;
                if (!owned.Replicable || owned.Object == null || owned.CleanGlb == null || ServerCache.IsHeld(entry.Key))
                    continue;
                ushort[] recipients = BasisModelReplicationScan.SnapshotEligible(
                    _scratchCandidates,
                    owned.Object.Root.position,
                    rangeMeters,
                    owned.SentRecipients,
                    _scratchRecipients
                );
                if (recipients.Length == 0)
                    continue;
                EnqueueTransfer(entry.Key, owned, ownerId, recipients);
            }
        }

        /// <summary>
        /// Recipients are marked served only once the transfer is queued, so one that never leaves is retried. The
        /// header's pose is not read here: the queue reads it from the live root when the header goes out.
        /// </summary>
        private static bool EnqueueTransfer(Guid id, OwnedModel owned, ushort ownerId, ushort[] recipients)
        {
            var transfer = new OutboundModelTransfer
            {
                Id = id,
                OwnerId = ownerId,
                OwnerName = owned.OwnerName,
                Payload = owned.CleanGlb,
                ChunkPayloadBytes = BasisModelWire.ChunkPayloadBytes,
                Recipients = recipients,
                Tail = owned.Tail,
            };
            if (!_outbound.Enqueue(transfer))
                return false;
            BasisModelReplication.MarkSent(owned.SentRecipients, recipients);
            return true;
        }

        /// <summary>The capability announcement on opcode 11. Null recipients is everyone.</summary>
        private static void SendHello(bool reply, ushort[] recipients)
        {
            BasisModelWire.WriteHello(_helloBuffer, reply);
            Sink.Send(_helloBuffer, DeliveryMethod.ReliableOrdered, recipients);
        }

        private static void UpdateTransferGizmos(float now)
        {
            if (_inbound.Count == 0 && _outbound.Count == 0)
            {
                _gizmos.Shutdown();
                return;
            }

            _gizmos.BeginFrame();
            foreach (KeyValuePair<Guid, InboundModel> entry in _inbound)
            {
                InboundModel transfer = entry.Value;
                transfer.Rate.Sample(now);
                // TotalBytes, not the buffer: the buffer only exists once the first chunk lands.
                ReportTransferProgress(entry.Key, transfer.Rate.Fraction(transfer.TotalBytes), transfer.Rate.BytesPerSecond, false);
            }
            foreach (OutboundModelTransfer transfer in _outbound)
            {
                transfer.Rate.Sample(now);
                ReportTransferProgress(
                    transfer.Id,
                    transfer.Rate.Fraction(transfer.Payload != null ? transfer.Payload.Length : 0),
                    transfer.Rate.BytesPerSecond,
                    true
                );
            }
            _gizmos.EndFrame();
        }

        private static void ReportTransferProgress(Guid id, float progress, float bytesPerSecond, bool outbound)
        {
            if (!Models.TryGetValue(id, out BasisModelPickupObject pickup) || pickup == null || pickup.Hidden)
                return;
            _gizmos.Report(id, pickup.TransferLabelAnchor, progress, bytesPerSecond, outbound);
        }
    }
}
