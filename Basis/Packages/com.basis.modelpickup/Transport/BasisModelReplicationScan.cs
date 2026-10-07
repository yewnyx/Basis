using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>Reads player positions into the engine-free replication candidates.</summary>
    public static class BasisModelReplicationScan
    {
        /// <summary>
        /// Every other player's anchor, read once per pass rather than once per item: eight owned items in a
        /// forty-player instance is forty transform reads this way and three hundred and twenty without.
        ///
        /// Walks the framework's reusable remote-player snapshot rather than enumerating the concurrent player
        /// table, which allocates an enumerator and takes its bucket locks. The snapshot is republished each frame a
        /// player joins or leaves, so a joiner can be missing for that frame; only peers that have said hello are
        /// sent anything, and a hello always lands later than the join and pulls in a fresh pass.
        /// </summary>
        public static void GatherCandidates(ushort localId, List<BasisModelReplicationCandidate> results)
        {
            results.Clear();
            BasisNetworkReceiver[] receivers = BasisNetworkPlayers.ReceiversSnapshot;
            int receiverCount = BasisNetworkPlayers.ReceiverCount;
            if (receiverCount > receivers.Length)
                receiverCount = receivers.Length;
            for (int i = 0; i < receiverCount; i++)
            {
                BasisNetworkReceiver receiver = receivers[i];
                if (receiver == null || receiver.playerId == localId)
                    continue;
                if (!receiver.TryGetPlayer(out IBasisPlayer basisPlayer))
                    continue;
                if (!TryGetPlayerPosition(basisPlayer, out Vector3 playerPosition))
                    continue;
                results.Add(new BasisModelReplicationCandidate { PlayerId = receiver.playerId, Position = playerPosition.ToShare() });
            }
        }

        public static bool TryGetPlayerPosition(IBasisPlayer player, out Vector3 position)
        {
            position = default;
            if (player == null || player.IsDestroyed)
                return false;

            // Unity's null is an overloaded operator rather than a reference test, so ?? would hand back a
            // destroyed transform and skip the fallbacks that exist for exactly that case.
            Transform anchor = player.Transform;
            if (anchor == null)
                anchor = player.AvatarTransform;
            if (anchor == null)
                anchor = player.PlayerSelf;
            if (anchor == null)
                return false;

            position = anchor.position;
            return true;
        }

        /// <summary>Where the local player stands, for judging server-cache offers.</summary>
        public static bool TryGetLocalViewerPosition(out Vector3 position)
        {
            position = default;
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            if (local == null)
                return false;
            position = local.transform.position;
            return true;
        }

        /// <summary>The sorted cohort an item at <paramref name="itemPosition"/> still owes a copy to (one allocation, none when empty).</summary>
        public static ushort[] SnapshotEligible(
            List<BasisModelReplicationCandidate> candidates,
            Vector3 itemPosition,
            float rangeMeters,
            HashSet<ushort> alreadySent,
            List<ushort> scratch
        )
        {
            return BasisModelReplication.SnapshotEligible(candidates, itemPosition.ToShare(), rangeMeters, alreadySent, scratch);
        }
    }
}
