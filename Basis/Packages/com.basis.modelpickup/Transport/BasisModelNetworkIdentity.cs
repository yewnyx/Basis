using System;
using Basis.Network.Core;
using Basis.Scripts.Networking;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The model pickup's fixed network identity: resolves its string identifier to the per-server message
    /// index, registers the direct-message handler, and sends through it.
    ///
    /// The index is per server, not per client: servers hand indices out from a counter that restarts on an
    /// empty instance, so the same identifier is a different number on every server. An index kept past a
    /// disconnect leaves the manager listening on something the new server uses for something else, so every
    /// leave opens a new <see cref="ConnectionGeneration"/> and a resolve still in flight from the old
    /// connection drops its answer.
    /// </summary>
    public sealed class BasisModelNetworkIdentity : IBasisModelPacketSink
    {
        public readonly string FixedIdentifier;

        /// <summary>
        /// Set by the owning manager: true in <c>Initialize</c>, false first thing in <c>Shutdown</c>, so a resolve
        /// that lands after shutdown does not arm a dead manager.
        /// </summary>
        public bool Enabled;

        /// <summary>Runs once the handler is registered (the manager arms its local-leave handler and says hello here).</summary>
        public Action Armed;

        /// <summary>Runs at the end of <see cref="Release"/> (the manager forgets what the server held for it).</summary>
        public Action Released;

        private readonly string _logLabel;
        private readonly BasisDebug.LogTag _logTag;
        private readonly Action<ushort, byte[], DeliveryMethod> _handler;

        /// <summary>
        /// The generation a resolve is in flight for, or -1. Doubles as the in-flight token so several joins
        /// arriving before the first answer do not each start a resolve.
        /// </summary>
        private int _resolveGeneration = -1;

        public BasisModelNetworkIdentity(
            string fixedIdentifier,
            string logLabel,
            BasisDebug.LogTag logTag,
            Action<ushort, byte[], DeliveryMethod> handler
        )
        {
            if (string.IsNullOrEmpty(fixedIdentifier))
                throw new ArgumentException("A network identity needs a fixed identifier.", nameof(fixedIdentifier));
            FixedIdentifier = fixedIdentifier;
            _logLabel = logLabel ?? fixedIdentifier;
            _logTag = logTag;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>Set by <see cref="Resolve"/>, cleared by <see cref="Release"/>.</summary>
        public bool HasNetworkID;

        public ushort NetworkID;

        /// <summary>Capture before an await and compare after: a change means the connection went away meanwhile.</summary>
        public int ConnectionGeneration;

        public async void Resolve()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                BasisDebug.LogError($"{_logLabel} cannot start; the local player is not connected.", _logTag);
                return;
            }
            int generation = ConnectionGeneration;
            if (HasNetworkID || _resolveGeneration == generation)
                return;
            _resolveGeneration = generation;

            BasisIdResolutionResult resolution;
            try
            {
                resolution = await BasisNetworkIdResolver.ResolveAsync(FixedIdentifier);
            }
            catch (Exception exception)
            {
                // Only our own token is cleared; a newer connection's resolve may already be in flight.
                if (_resolveGeneration == generation)
                    _resolveGeneration = -1;
                BasisDebug.LogError(
                    $"{_logLabel} could not resolve the network identifier '{FixedIdentifier}': {exception.Message}",
                    _logTag
                );
                return;
            }

            // The id belongs to whichever connection answered. If that connection is gone the answer is the
            // previous server's index, and arming with it leaves us listening on nothing.
            if (!Enabled || HasNetworkID || generation != ConnectionGeneration)
                return;
            if (!resolution.Success)
            {
                _resolveGeneration = -1;
                BasisDebug.LogError(
                    $"{_logLabel} could not resolve the network identifier '{FixedIdentifier}'.",
                    _logTag
                );
                return;
            }

            NetworkID = resolution.Id;
            HasNetworkID = true;
            BasisNetworkGenericMessages.RegisterDirectHandler(NetworkID, _handler);
            try
            {
                Armed?.Invoke();
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"{_logLabel} failed while arming: {exception}", _logTag);
            }
            BasisDebug.Log($"{_logLabel} ready (network id {NetworkID}).", _logTag);
        }

        /// <summary>
        /// Confirms the id we hold was issued by the connection we are on, and resolves a new one if it was not.
        /// A connection that died hard never raises the local-leave event, but
        /// <c>BasisNetworkIdResolver.KnownIdMap</c> is emptied on every teardown and refilled on join, so an id
        /// it does not confirm is one from a room we already left. Managers call this from
        /// <c>OnPlayerJoined</c>, which also fires for the local player.
        /// </summary>
        public void Ensure()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
                return;

            if (HasNetworkID)
            {
                if (BasisNetworkIdResolver.KnownIdMap.TryGetValue(FixedIdentifier, out ushort issued) && issued == NetworkID)
                    return;
                Release();
            }

            Resolve();
        }

        /// <summary>Drops the id and its handler and opens a new connection generation.</summary>
        public void Release()
        {
            ConnectionGeneration++;
            _resolveGeneration = -1;
            if (HasNetworkID)
                BasisNetworkGenericMessages.UnregisterDirectHandler(NetworkID);
            HasNetworkID = false;
            NetworkID = 0;
            Released?.Invoke();
        }

        /// <summary>
        /// Sends over direct peer-to-peer links, falling back to the server relay for recipients with no direct
        /// connection. A null <paramref name="recipients"/> is everyone.
        /// </summary>
        public void Send(byte[] buffer, DeliveryMethod deliveryMethod, ushort[] recipients)
        {
            if (!HasNetworkID)
            {
                BasisDebug.LogError($"{_logLabel} has no network id assigned yet.", _logTag);
                return;
            }
            BasisNetworkGenericMessages.OnNetworkMessageSendDirect(NetworkID, buffer, deliveryMethod, recipients);
        }
    }
}
