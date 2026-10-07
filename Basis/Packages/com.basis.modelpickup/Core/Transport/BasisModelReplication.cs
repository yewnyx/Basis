using System;
using System.Collections.Generic;

namespace Basis.ModelPickup
{
    /// <summary>One player's replication anchor, sampled once per range pass.</summary>
    public struct BasisModelReplicationCandidate
    {
        public ushort PlayerId;
        public BasisModelVec3 Position;
    }

    /// <summary>
    /// Who an owned pickup still owes a copy to. A sharer sends only to players within the server-advertised
    /// range; the server cache offers the item to everyone else, who decide for themselves when to ask.
    /// Recipient lists are sorted snapshots, so two cohorts with the same membership compare equal.
    /// </summary>
    public static class BasisModelReplication
    {
        /// <summary>Inclusive radius. A range of zero or less means unlimited.</summary>
        public static bool IsWithinRange(in BasisModelVec3 item, in BasisModelVec3 player, float rangeMeters)
        {
            if (rangeMeters <= 0f)
                return true;
            float rangeSq = rangeMeters * rangeMeters;
            return BasisModelVec3.DistanceSquared(item, player) <= rangeSq;
        }

        public static bool SnapshotsMatch(ushort[] left, ushort[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// A copy of <paramref name="recipients"/> without <paramref name="recipient"/>. Returns the same
        /// instance when it is absent, and an empty array for null or when the last one leaves.
        /// </summary>
        public static ushort[] RemoveRecipient(ushort[] recipients, ushort recipient)
        {
            if (recipients == null)
                return Array.Empty<ushort>();
            if (recipients.Length == 0)
                return recipients;
            int index = Array.IndexOf(recipients, recipient);
            if (index < 0)
                return recipients;
            if (recipients.Length == 1)
                return Array.Empty<ushort>();

            ushort[] reduced = new ushort[recipients.Length - 1];
            if (index > 0)
                Array.Copy(recipients, 0, reduced, 0, index);
            if (index < recipients.Length - 1)
                Array.Copy(recipients, index + 1, reduced, index, recipients.Length - index - 1);
            return reduced;
        }

        /// <summary>
        /// Picks the players an item still owes a copy to, sorted. Reads <paramref name="alreadySent"/> and
        /// never writes it, so a cohort that fails to queue leaves nothing behind claiming those players
        /// already hold the item; <see cref="MarkSent"/> commits once it is queued.
        /// </summary>
        public static void SelectEligible(
            List<BasisModelReplicationCandidate> candidates,
            in BasisModelVec3 itemPosition,
            float rangeMeters,
            HashSet<ushort> alreadySent,
            List<ushort> results
        )
        {
            if (results == null)
                throw new ArgumentNullException(nameof(results));
            results.Clear();
            if (candidates == null)
                return;
            int candidateCount = candidates.Count;
            for (int i = 0; i < candidateCount; i++)
            {
                BasisModelReplicationCandidate candidate = candidates[i];
                if (!IsWithinRange(itemPosition, candidate.Position, rangeMeters))
                    continue;
                if (alreadySent != null && alreadySent.Contains(candidate.PlayerId))
                    continue;
                results.Add(candidate.PlayerId);
            }
            results.Sort();
        }

        /// <summary>
        /// <see cref="SelectEligible"/> into <paramref name="scratch"/>, returned as a new array (one allocation). An
        /// empty cohort, the usual answer once everyone in range has the item, is the shared empty array.
        /// </summary>
        public static ushort[] SnapshotEligible(
            List<BasisModelReplicationCandidate> candidates,
            in BasisModelVec3 itemPosition,
            float rangeMeters,
            HashSet<ushort> alreadySent,
            List<ushort> scratch
        )
        {
            SelectEligible(candidates, itemPosition, rangeMeters, alreadySent, scratch);
            return scratch.Count == 0 ? Array.Empty<ushort>() : scratch.ToArray();
        }

        public static void MarkSent(HashSet<ushort> sent, ushort[] recipients)
        {
            if (sent == null || recipients == null)
                return;
            int recipientCount = recipients.Length;
            for (int i = 0; i < recipientCount; i++)
                sent.Add(recipients[i]);
        }
    }
}
