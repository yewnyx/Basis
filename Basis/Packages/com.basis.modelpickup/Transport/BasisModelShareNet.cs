using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;

namespace Basis.ModelPickup
{
    /// <summary>Who the local player is and what to call other players.</summary>
    public static class BasisModelShareNet
    {
        /// <summary>
        /// Stands in for "no player" where an owner id is required but none exists. Cannot be 0: peer ids are
        /// handed out from zero up, so 0 is the first player to join.
        /// </summary>
        public const ushort UnownedPlayerId = ushort.MaxValue;

        /// <summary>
        /// The local player's net id, or <see cref="UnownedPlayerId"/> when there is no local player yet.
        /// Falling back to 0 would stamp a pickup as belonging to whoever joined first, and leaving players take
        /// their pickups with them.
        /// </summary>
        public static ushort LocalPlayerId()
        {
            BasisNetworkPlayer local = BasisNetworkPlayer.LocalPlayer;
            return local != null ? local.playerId : UnownedPlayerId;
        }

        /// <summary>The local display name, trimmed to the wire's owner-name cap.</summary>
        public static string LocalOwnerName()
        {
            return BasisModelShareWire.NormalizeOwnerName(
                BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.SafeDisplayName : "Unknown"
            );
        }

        /// <summary>
        /// The name to show for a remote owner. Receivers name owners from the transport sender, never from the
        /// name a peer wrote on the wire. Only the tag-stripped safe name is used: a name made only of tags
        /// strips to empty, and the raw one would carry those tags onto a label.
        /// </summary>
        public static string ResolveOwnerName(ushort playerId)
        {
            if (BasisNetworkPlayer.GetPlayerById(playerId, out BasisNetworkPlayer player) && player != null)
            {
                string name = player.SafeDisplayName;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return $"Player {playerId}";
        }
    }
}
