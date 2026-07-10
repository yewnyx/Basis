using System;

namespace Basis.Network.Server
{
    /// <summary>
    /// Server lifecycle notifications for out-of-band observers (management APIs,
    /// metrics, logging). The Raise* helpers swallow subscriber exceptions so a
    /// faulty observer can never break the connect/disconnect or resource paths
    /// that raise them; raising is synchronous, so subscribers must return fast
    /// and hand real work to their own queue.
    /// </summary>
    public static class BasisServerEvents
    {
        /// <summary>An authenticated player finished joining: (netId, uuid, displayName).</summary>
        public static event Action<int, string, string> OnPlayerJoined;

        /// <summary>An authenticated player was removed from the server: (netId, uuid).</summary>
        public static event Action<int, string> OnPlayerLeft;

        /// <summary>A world entered the resource database: (netId, url, persistent, loadStrategy).</summary>
        public static event Action<string, string, bool, byte> OnWorldLoaded;

        /// <summary>A world left the resource database: (netId).</summary>
        public static event Action<string> OnWorldUnloaded;

        public static void RaisePlayerJoined(int netId, string uuid, string displayName)
        {
            try { OnPlayerJoined?.Invoke(netId, uuid, displayName); }
            catch (Exception e) { BNL.LogError($"[ServerEvents] OnPlayerJoined subscriber threw: {e}"); }
        }

        public static void RaisePlayerLeft(int netId, string uuid)
        {
            try { OnPlayerLeft?.Invoke(netId, uuid); }
            catch (Exception e) { BNL.LogError($"[ServerEvents] OnPlayerLeft subscriber threw: {e}"); }
        }

        public static void RaiseWorldLoaded(string netId, string url, bool persistent, byte loadStrategy)
        {
            try { OnWorldLoaded?.Invoke(netId, url, persistent, loadStrategy); }
            catch (Exception e) { BNL.LogError($"[ServerEvents] OnWorldLoaded subscriber threw: {e}"); }
        }

        public static void RaiseWorldUnloaded(string netId)
        {
            try { OnWorldUnloaded?.Invoke(netId); }
            catch (Exception e) { BNL.LogError($"[ServerEvents] OnWorldUnloaded subscriber threw: {e}"); }
        }
    }
}
