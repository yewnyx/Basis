using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.EventDriver;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Unity.Collections;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Per-client model pickup service. A dropped .glb or .gltf is validated and rebuilt into a canonical GLB,
    /// loaded through glTFast into a grabbable pickup, and sent to the players in range who announced they can
    /// load models. Receivers validate and rebuild it again against their own device limits before glTFast sees a
    /// byte. A server that keeps a model cache holds a copy for late joiners while the sharer is connected.
    ///
    /// Every model in every state is in one table from the moment its placeholder appears; the work on it runs as
    /// a polled job (<see cref="BasisModelJob"/>) whose every stage completion re-checks that the placeholder is
    /// still the tracked one, the connection is the same, and, for local drops, that props are not locked.
    ///
    /// Anyone may delete any model for everyone. The server cache only forgets a model when its owner says so, so
    /// an owner told by someone else repeats the despawn itself.
    /// </summary>
    public static partial class BasisModelPickupManager
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;

        // Test seams for the network edge. With a sink set the manager acts connected, sends there, and takes
        // LocalIdOverride as its own id. Production never sets them.
        public static IBasisModelPacketSink SinkOverride;
        public static ushort LocalIdOverride = BasisModelShareNet.UnownedPlayerId;

        /// <summary>Headless clients cannot load models, so they say nothing, receive nothing, and only follow moves and deletes.</summary>
        public static bool IsHeadless = BasisEventDriver.IsHeadlessClient;

        /// <summary>A model arriving in chunks; it owns its reservation until the bytes move to a job.</summary>
        private sealed class InboundModel : BasisModelInboundAssembly
        {
            public BasisModelSpawnTail Tail;
            public BasisModelPickupObject Pickup;
        }

        /// <summary>
        /// Every model in every state, placeholders included: the identity table. Change it only through
        /// <see cref="TrackModel"/> and <see cref="RemoveModel"/>, which keep <see cref="ModelArray"/>, the follow pass
        /// and the manager's other tables in step with it.
        /// </summary>
        public static readonly Dictionary<Guid, BasisModelPickupObject> Models = new Dictionary<Guid, BasisModelPickupObject>();

        /// <summary>
        /// <see cref="Models"/> as a dense array for the per-frame passes, in no particular order. The first
        /// <see cref="ModelCount"/> slots are live, each pickup's <see cref="BasisModelPickupObject.ManagerIndex"/> is its
        /// slot, and slot i's root is root i of the follow pass.
        /// </summary>
        public static BasisModelPickupObject[] ModelArray = new BasisModelPickupObject[16];

        /// <summary>Live slots in <see cref="ModelArray"/>; always <see cref="Models"/>.Count.</summary>
        public static int ModelCount;

        /// <summary>Every job still listed, in queue order: the first <see cref="JobCount"/> slots are live.</summary>
        public static BasisModelJob[] Jobs = new BasisModelJob[16];

        public static int JobCount;

        // Kept private: these tables only stay consistent with Models when changed together, through
        // TrackModel, RemoveModel and the handlers in the partial files.
        private static readonly Dictionary<Guid, OwnedModel> _owned = new Dictionary<Guid, OwnedModel>();
        private static readonly Dictionary<Guid, InboundModel> _inbound = new Dictionary<Guid, InboundModel>();
        private static readonly List<Guid> _scratchIds = new List<Guid>();
        private static readonly BasisModelWorkBudget _work = new BasisModelWorkBudget(1, 1, 0);
        private static readonly BasisModelFollowPass _follow = new BasisModelFollowPass();

        // Declared in this order on purpose: the cache client reads the range reporter, so it must exist first.

        /// <summary>The model manager's network id and connection: <c>HasNetworkID</c>, <c>NetworkID</c>, the generation.</summary>
        public static readonly BasisModelNetworkIdentity Identity = new BasisModelNetworkIdentity(
            BasisModelWire.FixedNetworkIdentifier,
            "Model pickup manager",
            LogTag,
            OnDirectNetworkMessage
        )
        {
            Armed = OnIdentityArmed,
            Released = OnIdentityReleased,
        };
        private static readonly BasisModelRangeReporter _range = new BasisModelRangeReporter("Model pickup", "model", LogTag);

        /// <summary>The client half of the server's model cache. Tests give it an id and a viewer and run a pass now.</summary>
        public static readonly BasisModelServerCacheClient ServerCache =
            new BasisModelServerCacheClient(
                RoutedSink.Instance,
                _range,
                IsKnownLocally,
                BasisModelPickupSettings.OfferRangeCheckSeconds,
                BasisModelPickupSettings.MaxPendingOffers,
                "Model pickup",
                "model(s)",
                LogTag
            )
            {
                ParseOffer = ParseOffer,
                CanRequest = CanRequestOffer,
                OfferPoseLimits = BasisModelSizing.RemoteLimits,
            };
        private static readonly BasisModelOutboundQueue<OutboundModelTransfer> _outbound = new BasisModelOutboundQueue<OutboundModelTransfer>();
        private static readonly BasisModelSpawnRateLimiter _rate = BasisModelAdmission.CreateRateLimiter();
        private static readonly BasisModelProgressGizmos _gizmos = new BasisModelProgressGizmos("ModelPickup_Progress_");
        private static readonly BasisModelBackPanelSync _backPanels = new BasisModelBackPanelSync();
        private static readonly BasisModelCapablePeers _capable = new BasisModelCapablePeers();

        private static bool _initialized;
        private static bool _destroying;
        private static bool _tickFailureLogged;
        private static float _nextRangeRefreshTime;

        private static BasisModelTierLimits Limits => BasisModelPickupSettings.Limits;

        /// <summary>Connected, or standing in for a connection under test.</summary>
        private static bool IsOnline => SinkOverride != null || Identity.HasNetworkID;

        private static IBasisModelPacketSink Sink => SinkOverride ?? Identity;

        private static readonly ushort[] _selfRecipient = new ushort[1];

        private static ushort LocalId()
        {
            return SinkOverride != null ? LocalIdOverride : BasisModelShareNet.LocalPlayerId();
        }

        /// <summary>Arms the model pickup service. Safe to call more than once.</summary>
        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            _destroying = false;
            Identity.Enabled = true;

            BasisModelPickupSettings.ResolveLimits();
            BasisModelTierLimits limits = Limits;
            _work.Configure(limits.MaxConcurrentWorkerJobs, limits.MaxConcurrentMainThreadJobs, limits.WorkingSetBytes);
            BasisModelGltfLoader.DeferAgent = new BasisModelDeferAgent(limits.DeferBudgetMilliseconds);
            BasisModelSizeDialog.Resolved = OnSizeBatchResolved;

            BasisModelUplink.ResetForNewConnection();
            ServerCache.ResetTimers();
            _range.Reset();
            _nextRangeRefreshTime = 0f;

            BasisEventDriver.OnUpdate += SimulateUpdate;
            BasisNetworkPlayer.OnLocalPlayerJoined += HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnPlayerJoined += OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft += OnPlayerLeft;
            Application.quitting += Shutdown;

            if (BasisNetworkConnection.LocalPlayerIsConnected)
                Identity.Resolve();
        }

        /// <summary>
        /// Drops every model and every transfer and unsubscribes the service. Idempotent, and safe without a prior
        /// <see cref="Initialize"/>, so tests reset with it. Work still running is orphaned: its import is abandoned
        /// and cleans itself up when it lands.
        /// </summary>
        public static void Shutdown()
        {
            Identity.Enabled = false;
            _initialized = false;
            _destroying = true;

            BasisEventDriver.OnUpdate -= SimulateUpdate;
            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnPlayerJoined -= OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft -= OnPlayerLeft;
            Application.quitting -= Shutdown;

            Identity.Release();
            BasisModelSizeDialog.CancelAll();
            BasisModelSizeDialog.Resolved = null;

            int jobCount = JobCount;
            for (int i = 0; i < jobCount; i++)
            {
                BasisModelJob job = Jobs[i];
                KillJob(job);
                Task running = job.RunningTask;
                bool idle = running == null || running.IsCompleted;
                // An import that landed after the last poll owns a holder and a GltfImport nobody else will free;
                // one still running sees its abandoned ticket and frees its own.
                DisposeUnclaimedResult(job);
                // A task still running may yet read its token, so only an idle job's source is disposed.
                if (idle)
                    job.Cancel?.Dispose();
                job.Cancel = null;
            }
            ClearJobs();

            RemoveAllModels();
            // Every root left the pass with its model; the native buffers come back with the next tracked model.
            _follow.Dispose();
            foreach (InboundModel transfer in _inbound.Values)
                transfer.ReleaseReservation();
            _inbound.Clear();
            _outbound.Clear();
            _owned.Clear();

            ServerCache.ClearHeld();
            ServerCache.ClearOffers();
            ClearOfferTracking();
            ServerCache.ReleaseResources();
            ServerCache.ResetTimers();
            _range.Reset();
            _rate.Clear();
            _capable.Clear();
            ClearSenderState();

            // Every holder has released above; this only mops up anything a stray path left behind.
            BasisModelInboundReservations.ReleaseAll();
            _work.Reset();
            _gizmos.Shutdown();
            _backPanels.Reset();
            BasisModelUplink.ResetForNewConnection();
            _nextRangeRefreshTime = 0f;
            _tickFailureLogged = false;
            _destroying = false;
        }

        /// <summary>Removes a model for everyone. Any client may call this for any model.</summary>
        public static void RequestDespawn(Guid id)
        {
            if (IsOnline)
            {
                if (Models.TryGetValue(id, out BasisModelPickupObject pickup) && pickup != null && pickup.IsOwner)
                    SendOwnerDespawn(id);
                else
                    Sink.SendIdMessage(BasisModelShareWire.OpDespawn, id, null);
            }
            MarkDespawned(id, Time.unscaledTime);
            RemoveModel(id);
        }

        /// <summary>
        /// The server cache drops a model only on its owner's despawn, and a broadcast to a room where every peer is
        /// P2P-connected never reaches the server, so the owner also addresses a copy to itself, which always goes
        /// through the relay. The echo back to us finds nothing left to remove.
        /// </summary>
        private static void SendOwnerDespawn(Guid id)
        {
            Sink.SendIdMessage(BasisModelShareWire.OpDespawn, id, null);
            _selfRecipient[0] = LocalId();
            Sink.SendIdMessage(BasisModelShareWire.OpDespawn, id, _selfRecipient);
        }

        /// <summary>Takes movement authority when this client grabs a model, demoting whoever held it.</summary>
        public static void ClaimControl(Guid id)
        {
            if (!Models.TryGetValue(id, out BasisModelPickupObject pickup) || pickup == null || pickup.IsController)
                return;
            pickup.SetController(true);
            if (IsOnline)
                Sink.SendIdMessage(BasisModelShareWire.OpClaim, id, null);
        }

        /// <summary>A pickup destroyed by something other than the manager (a scene unload, say) leaves the tables.</summary>
        public static void OnPickupDestroyed(BasisModelPickupObject pickup)
        {
            if (_destroying || ReferenceEquals(pickup, null))
                return;
            if (!Models.TryGetValue(pickup.ModelId, out BasisModelPickupObject tracked) || !ReferenceEquals(tracked, pickup))
                return;
            RemoveDestroyedModel(pickup.ModelId);
        }

        /// <summary>
        /// A pickup destroyed from outside. If it is ours and peers may have it, they are told: nobody else can evict
        /// it from the server cache, and their copies would outlive it.
        /// </summary>
        private static void RemoveDestroyedModel(Guid id)
        {
            if (IsOnline && IsReplicable(id))
                SendOwnerDespawn(id);
            RemoveModel(id, false);
        }

        public static bool TryGetModel(Guid id, out BasisModelPickupObject pickup)
        {
            return Models.TryGetValue(id, out pickup) && pickup != null;
        }

        /// <summary>Receiving was switched back on: say so, so owners start sending again.</summary>
        public static void OnReceiveEnabled()
        {
            if (!_initialized || IsHeadless || !IsOnline)
                return;
            SendHello(false, null);
        }

        public static void OnDirectNetworkMessage(ushort senderId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer == null || buffer.Length == 0)
                return;

            try
            {
                if (IsHeadless)
                {
                    switch (buffer[0])
                    {
                        case BasisModelShareWire.OpTransform:
                            HandleTransform(senderId, buffer);
                            break;
                        case BasisModelShareWire.OpDespawn:
                            HandleDespawn(senderId, buffer);
                            break;
                        case BasisModelShareWire.OpClaim:
                            HandleClaim(senderId, buffer);
                            break;
                    }
                    return;
                }

                switch (buffer[0])
                {
                    case BasisModelShareWire.OpSpawn:
                        HandleSpawn(senderId, buffer);
                        break;
                    case BasisModelShareWire.OpChunk:
                        HandleChunk(senderId, buffer);
                        break;
                    case BasisModelShareWire.OpTransform:
                        HandleTransform(senderId, buffer);
                        break;
                    case BasisModelShareWire.OpDespawn:
                        HandleDespawn(senderId, buffer);
                        break;
                    case BasisModelShareWire.OpClaim:
                        HandleClaim(senderId, buffer);
                        break;
                    case BasisModelShareWire.OpServerCacheState:
                        ServerCache.HandleState(senderId, buffer);
                        break;
                    case BasisModelShareWire.OpServerCacheOffer:
                        ServerCache.HandleOffer(senderId, buffer);
                        break;
                    case BasisModelWire.OpHello:
                        HandleHello(senderId, buffer);
                        break;
                }
            }
            catch (Exception e)
            {
                // Throttled per sender: a peer whose messages keep throwing must not become a log stream.
                if (TakeHandlerLog(senderId, Time.unscaledTime))
                    BasisDebug.LogWarning($"Model pickup ignored a message from {senderId}: {e.Message}", LogTag);
            }
        }

        private static void HandleLocalPlayerJoined(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
        {
            Identity.Resolve();
        }

        /// <summary>
        /// Armed per join, so the leave handler exists only while there is a connection to leave. Models dropped
        /// offline were stamped with no owner; they are ours on this connection now. Then say we can load models.
        /// </summary>
        public static void OnIdentityArmed()
        {
            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += HandleLocalPlayerLeft;

            ushort localId = LocalId();
            int modelCount = ModelCount;
            for (int i = 0; i < modelCount; i++)
            {
                BasisModelPickupObject pickup = ModelArray[i];
                if (pickup != null && pickup.IsOwner)
                    pickup.OwnerId = localId;
            }
            _nextRangeRefreshTime = 0f;

            if (!IsHeadless && BasisModelPickupSettings.ReceiveEnabled)
                SendHello(false, null);
        }

        /// <summary>The next server's answers about what it holds, and who can load models there, are its own.</summary>
        private static void OnIdentityReleased()
        {
            ServerCache.ClearHeld();
            _capable.Clear();
        }

        /// <summary>
        /// Drops every model on leaving the instance: pickups outlive scene loads by design, so nothing else would
        /// clear them. Each reservation is released by its own holder; work still running stays listed, dead, until
        /// its task lands, so its budget charge is returned exactly once.
        /// </summary>
        public static void HandleLocalPlayerLeft(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
        {
            // First, so a resolve or message still in flight belongs to a connection that is already gone.
            Identity.Release();

            BasisModelSizeDialog.CancelAll();
            int modelCount = ModelCount;
            RemoveAllModels();

            int jobCount = JobCount;
            for (int i = 0; i < jobCount; i++)
                KillJob(Jobs[i]);
            foreach (InboundModel transfer in _inbound.Values)
                transfer.ReleaseReservation();
            _inbound.Clear();
            _outbound.Clear();
            _owned.Clear();

            ServerCache.ClearOffers();
            ClearOfferTracking();
            ServerCache.ReleaseResources();
            _rate.Clear();
            ClearSenderState();
            _gizmos.Shutdown();
            BasisModelUplink.ResetForNewConnection();

            if (modelCount > 0)
                BasisDebug.Log($"Model pickup cleared {modelCount:N0} model(s) on leaving the instance.", LogTag);
        }

        private static void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (player == null)
                return;
            // Also raised for the local player: confirms the id we hold came from the server we are on.
            Identity.Ensure();
            HandlePlayerJoined(player.playerId);
        }

        /// <summary>
        /// Greets each joiner directly. The hello sent on arming reaches only the players this client knew then, and
        /// remote players are created frames after the local one, so a pair could otherwise miss each other for the
        /// session. A joiner not listening yet defers the message and replays it once it registers; a duplicate
        /// hello changes nothing.
        /// </summary>
        public static void HandlePlayerJoined(ushort playerId)
        {
            if (_owned.Count > 0)
                _nextRangeRefreshTime = 0f;
            if (playerId == LocalId() || IsHeadless || !BasisModelPickupSettings.ReceiveEnabled || !IsOnline)
                return;
            _helloRecipient[0] = playerId;
            SendHello(false, _helloRecipient);
        }

        private static void OnPlayerLeft(BasisNetworkPlayer player)
        {
            if (player == null)
                return;
            HandlePlayerLeft(player.playerId);
        }

        public static void HandlePlayerLeft(ushort left)
        {
            float now = Time.unscaledTime;
            _scratchIds.Clear();
            int modelCount = ModelCount;
            for (int i = 0; i < modelCount; i++)
            {
                BasisModelPickupObject pickup = ModelArray[i];
                if (pickup != null && pickup.OwnerId == left)
                    _scratchIds.Add(pickup.ModelId);
            }
            int removed = _scratchIds.Count;
            if (removed > 0)
                BasisDebug.Log($"Model pickup removed {removed:N0} model(s) because their owner ({left}) left.", LogTag);
            for (int i = 0; i < removed; i++)
            {
                MarkDespawned(_scratchIds[i], now);
                RemoveModel(_scratchIds[i]);
            }
            _scratchIds.Clear();

            foreach (KeyValuePair<Guid, InboundModel> entry in _inbound)
            {
                if (entry.Value.Sender == left)
                    _scratchIds.Add(entry.Key);
            }
            // A replay the server had already queued for one of theirs can still arrive after they are gone.
            foreach (KeyValuePair<Guid, RequestedOffer> entry in _requestedOffers)
            {
                if (entry.Value.Offer.ClaimedOwnerId == left)
                    _scratchIds.Add(entry.Key);
            }
            int transfers = _scratchIds.Count;
            for (int i = 0; i < transfers; i++)
            {
                MarkDespawned(_scratchIds[i], now);
                RemoveModel(_scratchIds[i]);
            }
            _scratchIds.Clear();

            // The server never withdraws an offer, so theirs would otherwise wait here for good and fill the cap.
            foreach (KeyValuePair<Guid, ushort> entry in _offerOwners)
            {
                if (entry.Value == left)
                    _scratchIds.Add(entry.Key);
            }
            int offers = _scratchIds.Count;
            for (int i = 0; i < offers; i++)
            {
                ServerCache.RemoveOffer(_scratchIds[i]);
                _offerOwners.Remove(_scratchIds[i]);
            }
            _scratchIds.Clear();

            foreach (OwnedModel owned in _owned.Values)
                owned.SentRecipients.Remove(left);
            _outbound.RemoveRecipient(left);
            _capable.Remove(left);
            _rate.Remove(left);
            ForgetSender(left);
        }

        /// <summary>
        /// The <see cref="BasisEventDriver.OnUpdate"/> tick. One try/catch contains a failure anywhere in it; the
        /// error is logged once per session, and its message (with the stack trace) is only built when it is.
        /// </summary>
        public static void SimulateUpdate()
        {
            try
            {
                SimulateUpdateBody();
            }
            catch (Exception exception)
            {
                if (!_tickFailureLogged)
                {
                    _tickFailureLogged = true;
                    BasisDebug.LogError(
                        $"Model pickup manager tick failed with {ModelCount:N0} models and {JobCount:N0} jobs: {exception}",
                        LogTag
                    );
                }
            }
        }

        private static void SimulateUpdateBody()
        {
            // First, before any early out: the uplink refills and the link is sampled every frame, busy or idle.
            BasisModelUplink.BeginFrame();
            BasisModelGltfLoader.DeferAgent?.BeginFrame();

            if (
                ModelCount == 0
                && JobCount == 0
                && _inbound.Count == 0
                && _outbound.Count == 0
                && ServerCache.PendingOfferCount == 0
                && !BasisModelSizeDialog.IsOpen
            )
            {
                _gizmos.Shutdown();
                return;
            }

            _backPanels.Update(ModelArray, ModelCount, BasisModelPickupSettings.MaxBackPanelUpdatesPerFrame);
            float now = Time.unscaledTime;
            BasisModelSizeDialog.Tick(now);
            AdvanceJobs(now);

            bool transmit = IsOnline;
            if (transmit)
                ServerCache.ScheduleRangeCheck(now);

            SimulateModels(now, transmit);

            if (!transmit)
            {
                _gizmos.Shutdown();
                return;
            }

            RefreshRangeRecipients(now);
            _outbound.Process(BasisModelPickupSettings.MaxNetworkChunksPerFrame, Sink, ModelOutboundSource.Instance);
            CheckInboundExpiry(now);
            ServerCache.CompleteRangeCheck();
#if !UNITY_SERVER
            if (!IsHeadless)
                UpdateTransferGizmos(now);
#endif
        }

        /// <summary>
        /// One pass over every model. Followers still on their way and controllers due to send are staged into the
        /// follow pass, which eases the first and samples the second in one Burst job over every root, so the main
        /// thread makes no transform call per pickup; a settled follower is left out until its next target. Then
        /// what moved is sent, and pickups destroyed from outside are swept. An owned model is not announced until
        /// its size is decided, so it sends nothing before then; its spawn header carries the pose it ends up at.
        /// </summary>
        private static void SimulateModels(float now, bool transmit)
        {
            float interval = 1f / BasisModelShareSettings.TransmitTransformHz;
            int count = ModelCount;
            BasisModelPickupObject[] models = ModelArray;
            NativeArray<BasisModelFollowSlot> slots = _follow.Slots;
            bool staged = false;

            _scratchIds.Clear();
            for (int i = 0; i < count; i++)
            {
                BasisModelPickupObject pickup = models[i];
                BasisModelFollowSlot slot = default;
                if (pickup == null)
                {
                    // Destroyed from outside. ModelId is a plain field, still readable on the dead object.
                    _scratchIds.Add(pickup.ModelId);
                }
                else
                {
                    BasisModelTransformSync sync = pickup.Sync;
                    if (sync.NeedsFollow)
                    {
                        slot.Mode = BasisModelFollowMode.Follow;
                        slot.TargetPosition = sync.TargetPosition;
                        slot.TargetRotation = sync.TargetRotation;
                        slot.TargetScale = sync.TargetScale;
                        staged = true;
                    }
                    else if (
                        transmit
                        && sync.CanSendAt(now, interval)
                        && (!pickup.IsOwner || IsReplicable(pickup.ModelId))
                    )
                    {
                        slot.Mode = BasisModelFollowMode.Sample;
                        staged = true;
                    }
                }
                slots[i] = slot;
            }

            if (staged)
            {
                _follow.Run(BasisModelMath.RemoteTransformLerpFactor(Time.deltaTime));
                IBasisModelPacketSink sink = transmit ? Sink : null;
                for (int i = 0; i < count; i++)
                {
                    BasisModelFollowSlot slot = slots[i];
                    if (slot.Mode == BasisModelFollowMode.Follow)
                    {
                        if (slot.Settled != 0)
                            models[i].Sync.Settled = true;
                        continue;
                    }
                    if (slot.Mode != BasisModelFollowMode.Sample)
                        continue;
                    BasisModelPickupObject pickup = models[i];
                    if (pickup.Sync.TryTakeSend(slot.Position, slot.Rotation, slot.Scale, now, interval))
                        sink.SendTransform(pickup.ModelId, slot.Position, slot.Rotation, slot.Scale, null);
                }
            }

            int destroyed = _scratchIds.Count;
            if (destroyed > 0)
            {
                BasisDebug.LogWarning(
                    $"Model pickup dropped {destroyed:N0} model(s) destroyed from outside the manager.",
                    LogTag
                );
            }
            for (int i = 0; i < destroyed; i++)
                RemoveDestroyedModel(_scratchIds[i]);
            _scratchIds.Clear();
        }

        /// <summary>
        /// Takes a model out of every table. Safe for unknown ids, and cheap for them: a despawn reaches the whole
        /// room, so most name a model this client never had. Its jobs die here (their reservations are released
        /// now; their budget charges when their tasks land), and its Library entry and progress label go with it.
        /// </summary>
        private static void RemoveModel(Guid id, bool destroyPickup = true)
        {
            // Tracked whether or not the pickup is still alive: a destroyed one still has its slot and its jobs.
            bool tracked = Models.TryGetValue(id, out BasisModelPickupObject pickup);
            if (tracked)
            {
                Models.Remove(id);
                RemoveFromModelArray(pickup);

                int jobCount = JobCount;
                for (int i = 0; i < jobCount; i++)
                {
                    if (Jobs[i].Id == id)
                        KillJob(Jobs[i]);
                }
            }

            // Only owned models are ever queued outbound, so the queue is only rotated for one of them.
            if (_owned.TryGetValue(id, out OwnedModel owned))
            {
                _outbound.Remove(id);
                _owned.Remove(id);
                if (owned.Batch != null)
                {
                    BasisModelSizeBatch batch = owned.Batch;
                    owned.Batch = null;
                    BasisModelSizeDialog.NotifyMemberRemoved(batch);
                }
            }
            ServerCache.Forget(id);
            _requestedOffers.Remove(id);
            _offerOwners.Remove(id);
            if (_inbound.TryGetValue(id, out InboundModel transfer))
            {
                _inbound.Remove(id);
                transfer.ReleaseReservation();
            }
            if (tracked)
            {
                // Labels and Library entries only ever exist for tracked models.
                _gizmos.Remove(id);
                BasisModelShareables.Unregister(id);
            }

            if (destroyPickup && pickup != null)
                BasisModelGltfLoader.DestroyUnityObject(pickup.gameObject);
        }

        private static void RemoveAllModels()
        {
            _scratchIds.Clear();
            foreach (Guid id in Models.Keys)
                _scratchIds.Add(id);
            int count = _scratchIds.Count;
            for (int i = 0; i < count; i++)
                RemoveModel(_scratchIds[i]);
            _scratchIds.Clear();
        }

        /// <summary>
        /// Tracks a pickup, gives it a dense slot and its root a place in the follow pass, and flags the back-panel
        /// sync: panels are built a few per frame, never inline.
        /// </summary>
        private static void TrackModel(Guid id, BasisModelPickupObject pickup)
        {
            if (Models.TryGetValue(id, out BasisModelPickupObject previous))
                RemoveFromModelArray(previous);
            Models[id] = pickup;

            if (ModelCount == ModelArray.Length)
                Array.Resize(ref ModelArray, ModelArray.Length * 2);
            pickup.ManagerIndex = ModelCount;
            ModelArray[ModelCount] = pickup;
            ModelCount++;
            _follow.Add(pickup.Root);
            _backPanels.MarkPending();
        }

        /// <summary>Swap-removes a pickup's dense slot and its follow-pass root together. A no-op for one without a slot.</summary>
        private static void RemoveFromModelArray(BasisModelPickupObject pickup)
        {
            if (ReferenceEquals(pickup, null))
                return;
            int index = pickup.ManagerIndex;
            pickup.ManagerIndex = -1;
            if (index < 0 || index >= ModelCount || !ReferenceEquals(ModelArray[index], pickup))
                return;

            int last = ModelCount - 1;
            BasisModelPickupObject moved = ModelArray[last];
            ModelArray[index] = moved;
            ModelArray[last] = null;
            ModelCount = last;
            if (index != last)
                moved.ManagerIndex = index;
            _follow.RemoveAtSwapBack(index);
        }

        private static void AddJob(BasisModelJob job)
        {
            if (JobCount == Jobs.Length)
                Array.Resize(ref Jobs, Jobs.Length * 2);
            Jobs[JobCount] = job;
            JobCount++;
        }

        /// <summary>Removes by shifting, not swapping: jobs start in queue order.</summary>
        private static void RemoveJobAt(int index)
        {
            int last = JobCount - 1;
            if (index < last)
                Array.Copy(Jobs, index + 1, Jobs, index, last - index);
            Jobs[last] = null;
            JobCount = last;
        }

        private static void ClearJobs()
        {
            Array.Clear(Jobs, 0, JobCount);
            JobCount = 0;
        }

        /// <summary>Forwards a pickup's requests to the static manager.</summary>
        public sealed class PickupHost : IBasisModelPickupHost
        {
            public static PickupHost Instance = new PickupHost();

            public void OnPickupDestroyed(BasisModelPickupObject pickup)
            {
                BasisModelPickupManager.OnPickupDestroyed(pickup);
            }

            public void RequestDespawn(Guid id)
            {
                BasisModelPickupManager.RequestDespawn(id);
            }

            public void ClaimControl(Guid id)
            {
                BasisModelPickupManager.ClaimControl(id);
            }
        }
    }
}
