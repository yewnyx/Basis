using Basis.Scripts.Networking;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The props lock and share permission for model drops. Shared 3D models count as props, as the server's
    /// props gate does: the lock is lifted by the server's prop bypass node, and sharing at all needs its
    /// prop-load node. These gate local drops only; the server gate is what holds for relayed copies.
    /// </summary>
    public static class BasisModelShareLocks
    {
        public static bool IsLocked()
        {
            return BasisNetworkModeration.GlobalPropsLocked;
        }

        /// <summary>
        /// The server's prop bypass node. LocalPlayerHasNode also honours '*' and parent wildcards such as
        /// basis.resource.*, as the server does.
        /// </summary>
        public static bool LocalPlayerCanBypass()
        {
            return BasisNetworkModeration.LocalPlayerHasNode(BasisPermissions.PermNodes.ResourceLockBypassProp);
        }

        /// <summary>
        /// Whether the local player may share models at all, lock or no lock: the server's prop-load node, but
        /// only once the server has said what we hold. Offline, or before permissions arrive, nobody is refused.
        /// </summary>
        public static bool LocalPlayerMayShare()
        {
            return !BasisNetworkConnection.LocalPlayerIsConnected
                || BasisNetworkManagement.LocalPermissions == null
                || BasisNetworkModeration.LocalPlayerHasNode(BasisPermissions.PermNodes.ResourceLoadProp);
        }

        public static bool IsBlockedLocally()
        {
            return IsLocked() && !LocalPlayerCanBypass();
        }
    }
}
