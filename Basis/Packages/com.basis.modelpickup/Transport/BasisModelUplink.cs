using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The per-frame entry point to the model's uplink budget. The manager calls <see cref="BeginFrame"/> first
    /// thing in its tick, before any early return, so the budget refills and the link is sampled even while
    /// nothing is sending; a second call in the same frame does nothing.
    /// </summary>
    public static class BasisModelUplink
    {
        public static void BeginFrame()
        {
            int frame = Time.frameCount;
            if (BasisModelBandwidth.HasBegunFrame(frame))
                return;
            RefreshServerRelayBudget();
            BasisModelLinkProbe.Tick(Time.unscaledTime);
            BasisModelBandwidth.BeginFrame(frame, Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Forgets the link and the budget a previous server advertised. Call on local leave and in
        /// <c>Shutdown</c>. The probe resets first because the buckets' capacity is read from it.
        /// </summary>
        public static void ResetForNewConnection()
        {
            BasisModelLinkProbe.Reset();
            BasisModelBandwidth.Reset();
        }

        /// <summary>
        /// Books one bulk packet. Headers, transforms, claims, despawns and cache requests stay unmetered: they
        /// are a few dozen bytes and carry the interactive feel of a pickup.
        /// </summary>
        public static bool TryReserveSendBandwidth(int payloadBytes, ushort[] recipients)
        {
            CountRecipients(recipients, out int directCount, out int relayCount);
            return TryReserveSendBandwidth(
                payloadBytes + BasisNetworkGenericMessages.SceneDataFramingBytes(recipients),
                directCount,
                relayCount
            );
        }

        /// <summary>
        /// <see cref="TryReserveSendBandwidth(int,ushort[])"/> for a cohort already split by <see cref="CountRecipients"/>,
        /// with <paramref name="wireBytes"/> already including its scene-data framing. The outbound queue splits a
        /// transfer's cohort once per frame instead of once per chunk.
        /// </summary>
        public static bool TryReserveSendBandwidth(int wireBytes, int directCount, int relayCount)
        {
            return BasisModelBandwidth.TryConsume(wireBytes, directCount, relayCount);
        }

        /// <summary>
        /// Splits one cohort between direct links and the relay. Peers on a connected P2P session are reached
        /// directly and cost the server nothing; everyone else is forwarded, once per recipient.
        /// </summary>
        public static void CountRecipients(ushort[] recipients, out int directCount, out int relayCount)
        {
            directCount = 0;
            relayCount = 0;
            if (recipients == null)
                return;
            int recipientCount = recipients.Length;
            for (int i = 0; i < recipientCount; i++)
            {
                if (BasisP2PManager.GetSessionState(recipients[i]) == BasisP2PManager.P2PSessionState.Connected)
                    directCount++;
                else
                    relayCount++;
            }
        }

        /// <summary>
        /// Picks up the egress budget the server advertised, in megabits per second as operators configure it.
        /// Read every frame rather than once on join so a budget re-sent later is picked up without an event of
        /// its own. Zero means no server has said anything and the core's fallback stands.
        /// </summary>
        public static void RefreshServerRelayBudget()
        {
            int advertisedMegabits = BasisNetworkManagement.ServerMetaDataMessage.ImageShareEgressMegabitsPerSecond;
            BasisModelBandwidth.ServerRelayBudgetBytesPerSecond =
                advertisedMegabits > 0 ? advertisedMegabits * 125_000L : 0L;
        }
    }
}
