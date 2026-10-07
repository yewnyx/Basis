using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.ModelPickup.Validation;
using UnityEngine;

namespace Basis.ModelPickup
{
    public static partial class BasisModelPickupManager
    {
        /// <summary>Senders already warned that they sent a non-canonical model, so a misbehaving one is named once.</summary>
        private static readonly HashSet<ushort> _nonCanonicalWarned = new HashSet<ushort>();

        private static bool _offerRefusalLogged;
        private static bool _invalidTransformLogged;

        /// <summary>Seconds between two handler warnings about one sender, as the server's share gate spaces its notices.</summary>
        private const float HandlerLogIntervalSeconds = 10f;

        /// <summary>When each sender last had a handler warning logged, for <see cref="TakeHandlerLog"/>.</summary>
        private static readonly Dictionary<ushort, float> _handlerLogAt = new Dictionary<ushort, float>();

        /// <summary>An offer this client asked the server for, whose replay has not arrived.</summary>
        private struct RequestedOffer
        {
            public BasisModelOfferInfo Offer;
            public float ExpiresAt;
        }

        /// <summary>
        /// Requests on their way, counted as if they had arrived. Otherwise one range pass asks for every offer that
        /// fits on its own, and the replays past the budget are refused on arrival for good: the server serves each
        /// model to a player once. One that never arrives stops counting after a while.
        /// </summary>
        private static readonly Dictionary<Guid, RequestedOffer> _requestedOffers = new Dictionary<Guid, RequestedOffer>();

        /// <summary>Each pending offer's claimed sharer: the server never withdraws an offer, so theirs go when they leave.</summary>
        private static readonly Dictionary<Guid, ushort> _offerOwners = new Dictionary<Guid, ushort>();

        /// <summary>
        /// Recently despawned ids, until when. The server paces its replays, so a copy it had queued can land after
        /// the despawn and would bring the model back on this client alone.
        /// </summary>
        private static readonly Dictionary<Guid, float> _despawned = new Dictionary<Guid, float>();

        /// <summary>
        /// Starts a receiver's validation of a downloaded model on a worker. A seam: tests substitute a task they
        /// complete themselves, to stand still mid-validation.
        /// </summary>
        public static Func<byte[], BasisModelLimits, CancellationToken, Task<BasisGlbValidationResult>> StartReceiveValidation =
            RunReceiveValidation;

        private static Task<BasisGlbValidationResult> RunReceiveValidation(byte[] wire, BasisModelLimits limits, CancellationToken token)
        {
            return Task.Run(() => BasisGlbValidator.ValidateReceived(wire, limits, token));
        }

        /// <summary>
        /// A model header. It is judged on its claims before a byte of the model is accepted: header, size, chunk
        /// count, claims against this device's tier, pose, this sender's and everyone's budgets, and how many
        /// transfers the sender already has open. A replay this client asked the server for skips the rate token
        /// and the transfer cap, because it was judged on the same claims before it was requested. Receivers do
        /// not apply the props lock: the sender and the server do.
        /// </summary>
        private static void HandleSpawn(ushort senderId, byte[] buffer)
        {
            if (!BasisModelShareWire.TryReadSpawn(buffer, out BasisModelSpawnHeader header, out BasisModelShareWireError error))
            {
                // A bad owner-name prefix is dropped without a word, as images do.
                if (error == BasisModelShareWireError.Truncated)
                    LogMalformed(senderId, "spawn header is truncated", Time.unscaledTime);
                return;
            }

            Guid id = header.Id;
            if (Models.ContainsKey(id) || _inbound.ContainsKey(id))
                return;
            float now = Time.unscaledTime;
            // Quietly, and before any token is taken: a replay overtaken by its despawn, not a misbehaving sender.
            if (IsDespawned(id, now))
                return;
            // Before RemoveOffer, which forgets that we asked.
            bool requested = ServerCache.WasRequested(id);
            ServerCache.RemoveOffer(id);
            // Arrived: from here it counts in the totals as a placeholder, or not at all.
            _requestedOffers.Remove(id);
            _offerOwners.Remove(id);

            var input = new BasisModelAdmissionInput
            {
                ReceiveEnabled = BasisModelPickupSettings.ReceiveEnabled,
                TotalBytes = header.TotalBytes,
                TotalChunks = header.TotalChunks,
                ActiveTransfersFromSender = CountInboundFrom(senderId),
                WasRequested = requested,
            };
            input.HeaderOk = BasisModelWire.TryReadTail(header, buffer, out input.Tail, out input.HeaderError);
            BasisModelPose pose = header.Pose;
            input.PoseValid = BasisModelPoseValidation.TrySanitizePose(ref pose, BasisModelSizing.RemoteLimits);
            BuildTotals(Guid.Empty, false, senderId, out input.SenderTotals, out input.ResidentTotals);

            // Every warning below goes through the per-sender throttle: refusals arrive at the sender's message
            // rate, the rate limiter's own included, and each string is only built when it will be logged.
            BasisModelAdmissionResult verdict = BasisModelAdmission.Evaluate(input, Limits, out string reason);
            if (verdict != BasisModelAdmissionResult.Accepted)
            {
                if (TakeHandlerLog(senderId, now))
                    BasisDebug.LogWarning($"Model pickup from {senderId} dropped: {reason}.", LogTag);
                return;
            }
            if (!BasisModelWire.IsKnownSizeMode(buffer[header.TailOffset]) && TakeHandlerLog(senderId, now))
                BasisDebug.LogWarning($"Model pickup from {senderId} names an unknown size mode; reading it as Fit.", LogTag);

            int totalBytes = header.TotalBytes;
            if (!BasisModelInboundReservations.TryReserve(totalBytes, Limits.InboundReservationBytes, out reason))
            {
                if (TakeHandlerLog(senderId, now))
                    BasisDebug.LogWarning($"Model pickup from {senderId} dropped: {reason}.", LogTag);
                return;
            }
            if (!BasisModelAdmission.TryTakeRateToken(input, _rate, senderId, now))
            {
                BasisModelInboundReservations.Release(totalBytes);
                if (TakeHandlerLog(senderId, now))
                    BasisDebug.LogWarning($"Model pickup from {senderId} dropped: spawn rate limit.", LogTag);
                return;
            }

            InboundModel transfer;
            try
            {
                transfer = new InboundModel
                {
                    Tail = input.Tail,
                    ZeroChunkTimeoutSeconds = BasisModelPickupSettings.ZeroChunkTimeoutSeconds,
                };
                transfer.Initialize(
                    senderId,
                    id,
                    totalBytes,
                    header.TotalChunks,
                    BasisModelWire.ChunkPayloadBytes,
                    now,
                    BasisModelPickupSettings.InboundTransferTimeoutSeconds
                );
                transfer.ReservedBytes = totalBytes;
            }
            catch
            {
                BasisModelInboundReservations.Release(totalBytes);
                throw;
            }
            _inbound[id] = transfer;

            // The placeholder goes up now, sized from the claims, so moves, claims and deletes apply to it while
            // the bytes arrive instead of the model popping in where the sender no longer has it.
            try
            {
                pose.ToUnity(out Vector3 position, out Quaternion rotation);
                string ownerName = BasisModelShareNet.ResolveOwnerName(senderId);
                BasisModelPickupObject pickup = BasisModelPickupObject.Build(id, senderId, ownerName, false, position, rotation, PickupHost.Instance);
                TrackModel(id, pickup);
                transfer.Pickup = pickup;
                pickup.SetShowSave(Limits.RetainReceivedGlb);
                pickup.Claims = input.Tail.Claims;
                pickup.TotalBytes = totalBytes;
                pickup.SetShape(input.Tail.Claims.Bounds, input.Tail.BaseScale);
                BasisModelShareables.Register(
                    id,
                    BasisLocalization.Get("modelPickup.shareable.loading"),
                    ownerName,
                    () => RequestDespawn(id)
                );
            }
            catch
            {
                RemoveModel(id);
                throw;
            }
        }

        /// <summary>The per-chunk path: no allocation until the last chunk lands.</summary>
        private static void HandleChunk(ushort senderId, byte[] buffer)
        {
            if (!BasisModelShareWire.TryReadChunkHeader(buffer, out Guid id, out int chunkIndex, out int length))
            {
                LogMalformed(senderId, "chunk header is truncated", Time.unscaledTime);
                return;
            }
            if (!_inbound.TryGetValue(id, out InboundModel transfer))
            {
                BasisDebug.LogWarningOnce(
                    "BasisModelPickup.ChunkWithoutTransfer",
                    "Model pickup received a chunk for a model it is not receiving. Expected right after a transfer "
                        + "completes or a model is refused; otherwise the transfer was dropped while the sender was still sending.",
                    LogTag
                );
                return;
            }

            float now = Time.unscaledTime;
            BasisModelChunkStatus status = transfer.Accept(
                senderId,
                buffer,
                chunkIndex,
                length,
                now,
                BasisModelPickupSettings.InboundTransferTimeoutSeconds
            );
            if (status != BasisModelChunkStatus.Accepted && status != BasisModelChunkStatus.Duplicate)
            {
                if (!transfer.RejectionLogged)
                {
                    transfer.RejectionLogged = true;
                    BasisDebug.LogWarning(
                        $"Model pickup rejected chunk {chunkIndex} of {transfer.TotalChunks:N0} from {senderId} because "
                            + transfer.DescribeRejection(status, senderId, chunkIndex, length, buffer.Length, "model")
                            + $". The transfer has {transfer.ReceivedCount:N0} chunks and will be dropped if it stops making progress.",
                        LogTag
                    );
                }
                return;
            }
            if (!transfer.IsComplete)
                return;

            _inbound.Remove(id);
            BasisModelPickupObject pickup = transfer.Pickup;
            if (!Models.TryGetValue(id, out BasisModelPickupObject tracked) || tracked == null || !ReferenceEquals(tracked, pickup))
            {
                transfer.ReleaseReservation();
                return;
            }

            var job = new BasisModelJob
            {
                Kind = BasisModelJobKind.Inbound,
                Stage = BasisModelJobStage.Queued,
                Id = id,
                Pickup = pickup,
                Sender = transfer.Sender,
                Label = "player " + transfer.Sender.ToString(CultureInfo.InvariantCulture),
                Received = transfer.Buffer,
                Tail = transfer.Tail,
                WaitingSince = now,
                Generation = Identity.ConnectionGeneration,
                Cancel = new CancellationTokenSource(),
            };
            // The reservation moves with the bytes: exactly one holder at a time.
            job.ReservedBytes = transfer.ReservedBytes;
            transfer.ReservedBytes = 0;
            AddJob(job);
        }

        private static void CheckInboundExpiry(float now)
        {
            if (_inbound.Count == 0)
                return;
            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, InboundModel> entry in _inbound)
            {
                InboundModel transfer = entry.Value;
                BasisModelTransferHealth health = transfer.CheckExpiry(now, BasisModelPickupSettings.StalledTransferWarningSeconds);
                if (health == BasisModelTransferHealth.StallWarning)
                {
                    BasisDebug.LogWarning(
                        $"Model pickup transfer from {transfer.Sender} has stalled at {transfer.ReceivedCount:N0} of {transfer.TotalChunks:N0} chunks.",
                        LogTag
                    );
                }
                else if (health == BasisModelTransferHealth.Expired)
                {
                    _scratchIds.Add(entry.Key);
                }
            }
            int expired = _scratchIds.Count;
            for (int i = 0; i < expired; i++)
            {
                BasisDebug.LogWarning($"Model pickup transfer {_scratchIds[i]} timed out and was dropped.", LogTag);
                RemoveModel(_scratchIds[i]);
            }
            _scratchIds.Clear();
        }

        private static bool TryStartValidate(BasisModelJob job)
        {
            if (!TryBeginWorker(job, BasisModelWorkBudget.EstimateReceiveBytes(job.Received.Length)))
                return false;
            job.Stage = BasisModelJobStage.Validating;
            job.ValidateTask = StartReceiveValidation(job.Received, Limits.Validation, job.Cancel.Token);
            return true;
        }

        /// <summary>
        /// The receiver's own validation and canonicalisation passed against this device's limits. The sender's
        /// claims must hold for what was actually received, or the model is dropped: they were what admitted it.
        /// </summary>
        private static void OnValidated(BasisModelJob job, float now)
        {
            bool ran = TryGetResult(job.ValidateTask, out BasisGlbValidationResult result, out string failure);
            job.ValidateTask = null;
            job.Received = null;
            if (!ran || !result.Ok)
            {
                BasisDebug.LogWarning($"Model pickup from {job.Sender} failed validation: {(ran ? result.Error : failure)}", LogTag);
                RemoveModel(job.Id);
                return;
            }
            if (!job.Tail.Claims.Verify(result.Stats, out string mismatch))
            {
                BasisDebug.LogWarning($"Model pickup from {job.Sender} does not match its claims: {mismatch}", LogTag);
                RemoveModel(job.Id);
                return;
            }
            if (!result.InputWasCanonical && _nonCanonicalWarned.Add(job.Sender))
            {
                BasisDebug.LogWarning(
                    $"Model pickup from {job.Sender} was not in canonical form and was rebuilt; the sender may run a different version.",
                    LogTag
                );
            }

            job.Stats = result.Stats;
            job.CleanGlb = result.CleanGlb;
            BasisModelPickupObject pickup = job.Pickup;
            pickup.Claims = BasisGlbClaims.FromStats(result.Stats);
            pickup.TotalBytes = result.CleanGlb.Length;
            pickup.SetShape(result.Stats.Bounds, BasisModelSizing.ClampBaseScale(job.Tail.BaseScale, result.Stats.Bounds.MaxExtent));
            BasisModelShareables.SetTitle(job.Id, BasisLocalization.Get("modelPickup.shareable.detail", result.Stats.Triangles));
            job.Stage = BasisModelJobStage.AwaitImport;
            job.WaitingSince = now;
        }

        private static void HandleTransform(ushort senderId, byte[] buffer)
        {
            if (!BasisModelShareWire.TryReadTransform(buffer, out Guid id, out BasisModelPose pose, out float scale))
            {
                LogMalformed(senderId, "transform is truncated", Time.unscaledTime);
                return;
            }
            if (
                !BasisModelPoseValidation.TrySanitizePose(ref pose, BasisModelSizing.RemoteLimits)
                || !BasisModelPoseValidation.TrySanitizeScale(ref scale, BasisModelSizing.RemoteLimits)
            )
            {
                // Said once: a peer sending junk sends a lot of it.
                if (!_invalidTransformLogged)
                {
                    _invalidTransformLogged = true;
                    BasisDebug.LogWarning($"Model pickup ignored an invalid transform from {senderId}.", LogTag);
                }
                return;
            }
            pose.ToUnity(out Vector3 position, out Quaternion rotation);

            // An offer still pending follows the model, so one carried over to us does not stay out of range.
            ServerCache.OnTransform(id, position);
            if (Models.TryGetValue(id, out BasisModelPickupObject pickup) && pickup != null && !pickup.IsController)
                pickup.SetRemoteTarget(position, rotation, scale);
        }

        private static void HandleClaim(ushort senderId, byte[] buffer)
        {
            if (!BasisModelShareWire.TryReadIdMessage(buffer, out Guid id))
            {
                LogMalformed(senderId, "claim is truncated", Time.unscaledTime);
                return;
            }
            if (Models.TryGetValue(id, out BasisModelPickupObject pickup) && pickup != null)
                pickup.SetController(false);
        }

        /// <summary>
        /// Honoured from anyone, as for images. The server cache only drops a model on its owner's own despawn
        /// (otherwise anyone could evict a model and re-share under its id), so the owner repeats it first.
        /// </summary>
        private static void HandleDespawn(ushort senderId, byte[] buffer)
        {
            if (!BasisModelShareWire.TryReadIdMessage(buffer, out Guid id))
            {
                LogMalformed(senderId, "despawn is truncated", Time.unscaledTime);
                return;
            }
            bool known = Models.TryGetValue(id, out BasisModelPickupObject pickup) && pickup != null;
            if (known && pickup.IsOwner && senderId != LocalId() && IsOnline)
                SendOwnerDespawn(id);
            if (known || _inbound.ContainsKey(id))
                BasisDebug.Log($"Model pickup: player {senderId} despawned model {id}.", LogTag);
            MarkDespawned(id, Time.unscaledTime);
            RemoveModel(id);
        }

        /// <summary>
        /// Refuses <paramref name="id"/> for a while. Only ids this client had or asked for: nothing else can be on its
        /// way, and a peer naming random ids does not grow the set. Before <see cref="RemoveModel"/>, which forgets them.
        /// </summary>
        private static void MarkDespawned(Guid id, float now)
        {
            if (Models.ContainsKey(id) || _inbound.ContainsKey(id) || _requestedOffers.ContainsKey(id) || ServerCache.WasRequested(id))
                _despawned[id] = now + BasisModelPickupSettings.DespawnTombstoneSeconds;
        }

        private static bool IsDespawned(Guid id, float now)
        {
            return _despawned.TryGetValue(id, out float until) && now < until;
        }

        /// <summary>Forgets lapsed despawns, and requests the server never answered, so neither holds anything for good.</summary>
        private static void PurgeExpiredOfferState(float now)
        {
            if (_despawned.Count == 0 && _requestedOffers.Count == 0)
                return;
            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, float> entry in _despawned)
            {
                if (now >= entry.Value)
                    _scratchIds.Add(entry.Key);
            }
            int lapsed = _scratchIds.Count;
            for (int i = 0; i < lapsed; i++)
                _despawned.Remove(_scratchIds[i]);
            _scratchIds.Clear();

            foreach (KeyValuePair<Guid, RequestedOffer> entry in _requestedOffers)
            {
                if (now >= entry.Value.ExpiresAt)
                    _scratchIds.Add(entry.Key);
            }
            int unanswered = _scratchIds.Count;
            if (unanswered > 0)
                BasisDebug.Log($"Model pickup stopped waiting for {unanswered:N0} requested model(s) the server never sent.", LogTag);
            for (int i = 0; i < unanswered; i++)
                _requestedOffers.Remove(_scratchIds[i]);
            _scratchIds.Clear();
        }

        private static void ClearOfferTracking()
        {
            _requestedOffers.Clear();
            _offerOwners.Clear();
            _despawned.Clear();
        }

        private static void HandleHello(ushort senderId, byte[] buffer)
        {
            if (!BasisModelWire.TryReadHello(buffer, out byte version, out bool reply))
                return;
            bool localCapable = !IsHeadless && BasisModelPickupSettings.ReceiveEnabled;
            if (!_capable.OnHello(senderId, LocalId(), version, reply, localCapable, out bool sendReply, out bool added))
                return;
            // A newly capable peer may be owed models already in its range.
            if (added)
                _nextRangeRefreshTime = 0f;
            if (sendReply && IsOnline)
            {
                _helloRecipient[0] = senderId;
                SendHello(true, _helloRecipient);
            }
        }

        /// <summary>
        /// Tracks an offer only if a live spawn of it would pass the same header, size, chunk-count, claims and
        /// pose checks, so a request is only ever sent for something this client could accept.
        /// </summary>
        private static bool ParseOffer(in BasisModelSpawnHeader header, ReadOnlySpan<byte> message, out BasisModelOfferInfo offer)
        {
            if (BasisModelAdmission.TryParseOffer(header, message, Limits, out offer, out string reason))
            {
                TrackOfferOwner(header.Id, offer.ClaimedOwnerId);
                return true;
            }
            // The flag first: the message is built only on the one call that logs it.
            if (!_offerRefusalLogged)
            {
                _offerRefusalLogged = true;
                BasisDebug.LogWarning($"Model pickup ignored a server offer: {reason}.", LogTag);
            }
            return false;
        }

        /// <summary>Not for an offer the cache client's cap is about to turn away, which would never be removed.</summary>
        private static void TrackOfferOwner(Guid id, ushort claimedOwner)
        {
            int cap = ServerCache.MaxPendingOffers;
            if (cap > 0 && ServerCache.PendingOfferCount >= cap && !ServerCache.TryGetOffer(id, out _))
                return;
            _offerOwners[id] = claimedOwner;
        }

        /// <summary>
        /// False keeps the offer pending: budgets free up as models are deleted or finish loading, and receiving may be
        /// turned back on. Requests still on their way count as arrived, in the budgets and in inbound memory, which
        /// each replay's header reserves in full. True means the cache client sends the request now, so it is
        /// tracked from here.
        /// </summary>
        private static bool CanRequestOffer(Guid id, BasisModelOfferInfo offer)
        {
            if (IsHeadless || !BasisModelPickupSettings.ReceiveEnabled || Models.ContainsKey(id))
                return false;
            float now = Time.unscaledTime;
            BuildTotals(Guid.Empty, false, offer.ClaimedOwnerId, out BasisModelAggregate sender, out BasisModelAggregate resident);
            long inFlightBytes = AddRequestsInFlight(id, offer.ClaimedOwnerId, now, ref sender, ref resident);
            if (!BasisModelAdmission.OfferFitsBudgets(offer, sender, resident, Limits, out _))
                return false;
            long reserve = inFlightBytes + offer.TotalBytes;
            if (!BasisModelInboundReservations.Fits(BasisModelInboundReservations.Reserved, reserve, Limits.InboundReservationBytes))
                return false;

            _requestedOffers[id] = new RequestedOffer
            {
                Offer = offer,
                ExpiresAt = now + BasisModelPickupSettings.RequestedOfferTimeoutSeconds,
            };
            _offerOwners.Remove(id);
            return true;
        }

        /// <summary>Adds every live request but <paramref name="exclude"/> to the totals, as loading placeholders; returns their bytes.</summary>
        private static long AddRequestsInFlight(Guid exclude, ushort sender, float now,
            ref BasisModelAggregate senderTotals, ref BasisModelAggregate residentTotals)
        {
            long bytes = 0;
            foreach (KeyValuePair<Guid, RequestedOffer> entry in _requestedOffers)
            {
                if (entry.Key == exclude || now >= entry.Value.ExpiresAt)
                    continue;
                BasisModelOfferInfo offer = entry.Value.Offer;
                residentTotals.Add(offer.Tail.Claims, offer.TotalBytes);
                if (offer.ClaimedOwnerId == sender)
                    senderTotals.Add(offer.Tail.Claims, offer.TotalBytes);
                bytes += offer.TotalBytes;
            }
            return bytes;
        }

        /// <summary>An offer for something we have, are receiving, own, or just saw despawned is not worth tracking.</summary>
        private static bool IsKnownLocally(Guid id)
        {
            return Models.ContainsKey(id) || _inbound.ContainsKey(id) || _owned.ContainsKey(id) || IsDespawned(id, Time.unscaledTime);
        }

        /// <summary>
        /// Budget totals over every model here, placeholders included (header claims until validated, own stats
        /// after). The sender's share is the local player's own models, or one remote owner's. A model holds its GLB
        /// while loading, when it is ours, or when this tier keeps received ones.
        /// </summary>
        private static void BuildTotals(Guid exclude, bool localSender, ushort sender,
            out BasisModelAggregate senderTotals, out BasisModelAggregate residentTotals)
        {
            senderTotals = default;
            residentTotals = default;
            int modelCount = ModelCount;
            BasisModelPickupObject[] models = ModelArray;
            for (int i = 0; i < modelCount; i++)
            {
                BasisModelPickupObject pickup = models[i];
                if (pickup == null || pickup.ModelId == exclude)
                    continue;
                bool retainsGlb = pickup.IsOwner || pickup.IsLoading || pickup.CanonicalGlb != null;
                residentTotals.Add(pickup.Claims, pickup.TotalBytes, retainsGlb);
                bool fromSender = localSender ? pickup.IsOwner : !pickup.IsOwner && pickup.OwnerId == sender;
                if (fromSender)
                    senderTotals.Add(pickup.Claims, pickup.TotalBytes, retainsGlb);
            }
        }

        private static int CountInboundFrom(ushort sender)
        {
            int count = 0;
            foreach (InboundModel transfer in _inbound.Values)
            {
                if (transfer.Sender == sender)
                    count++;
            }
            return count;
        }

        private static void LogMalformed(ushort senderId, string reason, float now)
        {
            if (TakeHandlerLog(senderId, now))
                BasisDebug.LogWarning($"Model pickup: malformed message from {senderId} ({reason}).", LogTag);
        }

        /// <summary>
        /// At most one handler warning per sender per <see cref="HandlerLogIntervalSeconds"/>, like the server share
        /// gate's notices: a peer sending junk sends a lot of it, and every refusal used to log. Callers test this
        /// before building their message.
        /// </summary>
        private static bool TakeHandlerLog(ushort senderId, float now)
        {
            if (_handlerLogAt.TryGetValue(senderId, out float last) && now - last < HandlerLogIntervalSeconds)
                return false;
            _handlerLogAt[senderId] = now;
            return true;
        }
    }
}
