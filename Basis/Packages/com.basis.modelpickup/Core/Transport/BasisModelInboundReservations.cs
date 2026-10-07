using System;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The model pickup's own pool of inbound transfer memory, bounded by the tier's
    /// <see cref="BasisModelTierLimits.InboundReservationBytes"/>. A spawn header reserves its whole claimed
    /// size before a byte arrives, so a peer cannot make this client allocate more than the pool holds.
    ///
    /// Ownership rule: every reservation has exactly one holder, and whoever holds it releases it once.
    /// Moving a reservation (assembly to job) copies the bytes and zeroes the source. <see cref="ReleaseAll"/>
    /// is for <c>Shutdown</c> only, after every holder is gone; calling it while holders remain lets their
    /// later releases eat into fresh reservations. <see cref="Release"/> is clamped to what the pool holds
    /// as a backstop, so a stray release can never drive it negative.
    /// </summary>
    public static class BasisModelInboundReservations
    {
        public const string BudgetReason = "inbound model transfer memory budget";

        /// <summary>
        /// Bytes reserved by every inbound model transfer and job together. A plain field: the ownership
        /// rule above is what keeps it right, and only <see cref="TryReserve"/>, <see cref="Release"/> and
        /// <see cref="ReleaseAll"/> write it.
        /// </summary>
        public static long Reserved;

        /// <summary>
        /// Whether <paramref name="candidate"/> more bytes fit under <paramref name="limit"/> on top of
        /// <paramref name="reserved"/>. Written so no operand can overflow; non-positive candidates never fit.
        /// </summary>
        public static bool Fits(long reserved, long candidate, long limit)
        {
            return reserved >= 0 && candidate > 0 && reserved <= limit && candidate <= limit - reserved;
        }

        public static bool TryReserve(long bytes, long limit, out string reason)
        {
            if (!Fits(Reserved, bytes, limit))
            {
                reason = BudgetReason;
                return false;
            }
            Reserved = checked(Reserved + bytes);
            reason = null;
            return true;
        }

        /// <summary>Returns bytes to the pool, never more than it holds. Non-positive amounts are ignored.</summary>
        public static void Release(long bytes)
        {
            if (bytes <= 0)
                return;
            Reserved = Math.Max(0, Reserved - bytes);
        }

        /// <summary>Empties the pool. <c>Shutdown</c> only; see the ownership rule above.</summary>
        public static void ReleaseAll()
        {
            Reserved = 0;
        }
    }
}
