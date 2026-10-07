using System.Collections.Generic;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Peers that announced the model pickup with a hello (opcode 11). Owners push model bytes only to these: a client
    /// without the package has no handler for the model id, and the transport would park every chunk in its deferred
    /// queue until it disconnects. Server-cache offers and requests are not filtered: only a client that handles the
    /// model id ever requests one.
    /// </summary>
    public sealed class BasisModelCapablePeers
    {
        /// <summary>The ids that said hello.</summary>
        public readonly HashSet<ushort> Peers = new HashSet<ushort>();

        public int Count => Peers.Count;

        public bool Contains(ushort playerId)
        {
            return Peers.Contains(playerId);
        }

        /// <summary>
        /// Records a hello from <paramref name="senderId"/>. Returns false, changing nothing, for our own echo or a
        /// version below 1. <paramref name="sendReply"/> asks the caller to answer [sender] with a reply hello: only for
        /// a first-contact hello (replies are never answered, so two peers exchange exactly two messages) and only while
        /// this client can receive. <paramref name="added"/> is true when the peer is new, which is when the owner should
        /// re-run its replication pass.
        /// </summary>
        public bool OnHello(ushort senderId, ushort localId, byte version, bool reply, bool localCapable,
            out bool sendReply, out bool added)
        {
            if (senderId == localId || version < BasisModelWire.HelloVersion)
            {
                sendReply = false;
                added = false;
                return false;
            }
            added = Peers.Add(senderId);
            sendReply = !reply && localCapable;
            return true;
        }

        public bool Remove(ushort playerId)
        {
            return Peers.Remove(playerId);
        }

        public void Clear()
        {
            Peers.Clear();
        }

        /// <summary>Keeps only capable candidates, in their original order, compacting in place without allocating.</summary>
        public void Filter(List<BasisModelReplicationCandidate> candidates)
        {
            if (candidates == null)
                return;
            int count = candidates.Count;
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                BasisModelReplicationCandidate candidate = candidates[i];
                if (!Peers.Contains(candidate.PlayerId))
                    continue;
                if (kept != i)
                    candidates[kept] = candidate;
                kept++;
            }
            if (kept < count)
                candidates.RemoveRange(kept, count - kept);
        }
    }
}
