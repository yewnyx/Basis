using Basis.Network.Core;
using Basis.Scripts.Networking;

namespace Basis.ModelPickup
{
    public static partial class BasisModelLinkProbe
    {
        /// <summary>
        /// Reads the server link and folds one sample into the estimate. Driven once per frame by
        /// <see cref="BasisModelUplink.BeginFrame"/>; with no peer the sample window restarts, so the
        /// first reading after a reconnect only opens a new window.
        /// </summary>
        public static void Tick(float now)
        {
            NetPeer peer = BasisNetworkManagement.LocalPlayerPeer;
            if (peer == null)
            {
                _lastSampleTime = 0f;
                return;
            }

            int queued = peer.GetPacketsCountInQueue(
                BasisNetworkCommons.DirectSceneServerChannel,
                DeliveryMethod.ReliableOrdered
            );
            Observe(now, peer.RoundTripTime, queued);
        }
    }
}
