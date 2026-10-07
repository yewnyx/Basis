using System.Collections.Generic;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Token bucket per sender for inbound spawn headers. A refused spawn is never retried, so the burst is
    /// sized to let a sender replace its whole set at once; the count and byte budgets already bound what a
    /// burst can cost.
    /// </summary>
    public sealed class BasisModelSpawnRateLimiter
    {
        public sealed class SenderState
        {
            public float Tokens;
            public float LastRefillTime;
        }

        /// <summary>Each sender's bucket, created full on its first spawn.</summary>
        public readonly Dictionary<ushort, SenderState> BySender = new Dictionary<ushort, SenderState>();

        /// <summary>Burst a sender starts with and can bank back up to.</summary>
        public readonly float Capacity;

        /// <summary>Seconds to earn one token back. Zero or less disables the limit.</summary>
        public readonly float IntervalSeconds;

        public BasisModelSpawnRateLimiter(float capacity, float intervalSeconds)
        {
            Capacity = capacity;
            IntervalSeconds = intervalSeconds;
        }

        public int TrackedSenders => BySender.Count;

        /// <summary>Spends one token for <paramref name="sender"/> if it has one. A sender seen for the first time starts full.</summary>
        public bool TryConsume(ushort sender, float now)
        {
            float interval = IntervalSeconds;
            if (interval <= 0f)
                return true;

            if (!BySender.TryGetValue(sender, out SenderState state))
            {
                state = new SenderState { Tokens = Capacity, LastRefillTime = now };
                BySender[sender] = state;
            }
            else
            {
                float elapsed = BasisModelMath.Max(0f, now - state.LastRefillTime);
                state.Tokens = BasisModelMath.Min(Capacity, state.Tokens + elapsed / interval);
                state.LastRefillTime = now;
            }

            if (state.Tokens < 1f)
                return false;
            state.Tokens -= 1f;
            return true;
        }

        public void Remove(ushort sender)
        {
            BySender.Remove(sender);
        }

        public void Clear()
        {
            BySender.Clear();
        }
    }
}
