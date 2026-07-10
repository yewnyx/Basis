#if !UNITY_2017_1_OR_NEWER
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Collections.Generic;
using static BasisPermissions.PermissionManager;

namespace Basis.Network.Server
{
    /// <summary>Outcome of a moderation operation: whether it took effect, plus a human-readable message.</summary>
    public record ModerationResult(bool Ok, string Message);

    /// <summary>
    /// Seam over the permission store, moderation bans and the connect allowlist
    /// for external management bindings, mirroring how <see cref="IServerControl"/>
    /// fronts world/player control. Implementations raise the change events after
    /// any mutation that altered state — including mutations made by other writers
    /// (in-game admins, console commands) — so a remote manager can observe every
    /// origin, not just its own writes.
    /// </summary>
    public interface IBasisModerationControl : IDisposable
    {
        /// <summary>Argument is the affected UUID, or null when a group change affects all users.</summary>
        event Action<string> OnPermissionsChanged;
        event Action OnBansChanged;
        event Action OnAllowlistChanged;

        void AddUserNode(string uuid, string node);
        void RemoveUserNode(string uuid, string node);
        void AddUserToGroup(string uuid, string group);
        void RemoveUserFromGroup(string uuid, string group);
        void AddGroupNode(string group, string node);
        void RemoveGroupNode(string group, string node);
        void AddGroupParent(string group, string parent);
        void RemoveGroupParent(string group, string parent);
        void CreateGroup(string group);
        bool DeleteGroup(string group);
        PermissionStore SnapshotPermissions();

        /// <summary>Ban a UUID whether or not the player is connected; disconnects them first when online.</summary>
        ModerationResult Ban(string uuid, string reason);
        /// <summary>Ban a UUID and its current IP. Requires the player online (the IP comes from the live peer).</summary>
        ModerationResult IpBan(string uuid, string reason);
        ModerationResult Kick(string uuid, string reason);
        ModerationResult Unban(string uuid);
        ModerationResult UnbanIp(string ip);
        IReadOnlyList<BasisPlayerModeration.BannedPlayer> ListBans();

        ModerationResult AddToAllowlist(string uuid);
        ModerationResult RemoveFromAllowlist(string uuid);
        IReadOnlyList<string> ListAllowlist();
    }

    /// <summary>
    /// Default implementation backed by <see cref="PermissionIntegration.Manager"/>,
    /// <see cref="BasisPlayerModeration"/> and <see cref="NetworkServer.AllowList"/>.
    /// Construct after <see cref="NetworkServer.StartServer"/> so the allowlist exists.
    /// </summary>
    public sealed class BasisModerationControl : IBasisModerationControl
    {
        public event Action<string> OnPermissionsChanged;
        public event Action OnBansChanged;
        public event Action OnAllowlistChanged;

        private readonly Action<string> _forwardPermissions;
        private readonly Action _forwardBans;
        private readonly Action _forwardAllowlist;
        private readonly BasisAllowList _allowList;

        public BasisModerationControl()
        {
            _forwardPermissions = uuid => OnPermissionsChanged?.Invoke(uuid);
            _forwardBans = () => OnBansChanged?.Invoke();
            _forwardAllowlist = () => OnAllowlistChanged?.Invoke();

            PermissionIntegration.Manager.OnPermissionsChanged += _forwardPermissions;
            BasisPlayerModeration.OnBansChanged += _forwardBans;
            _allowList = NetworkServer.AllowList;
            if (_allowList != null)
                _allowList.OnChanged += _forwardAllowlist;
        }

        public void AddUserNode(string uuid, string node) => PermissionIntegration.Manager.AddUserNode(uuid, node);
        public void RemoveUserNode(string uuid, string node) => PermissionIntegration.Manager.RemoveUserNode(uuid, node);
        public void AddUserToGroup(string uuid, string group) => PermissionIntegration.Manager.AddUserToGroup(uuid, group);
        public void RemoveUserFromGroup(string uuid, string group) => PermissionIntegration.Manager.RemoveUserFromGroup(uuid, group);
        public void AddGroupNode(string group, string node) => PermissionIntegration.Manager.AddGroupNode(group, node);
        public void RemoveGroupNode(string group, string node) => PermissionIntegration.Manager.RemoveGroupNode(group, node);
        public void AddGroupParent(string group, string parent) => PermissionIntegration.Manager.AddGroupParent(group, parent);
        public void RemoveGroupParent(string group, string parent) => PermissionIntegration.Manager.RemoveGroupParent(group, parent);
        public void CreateGroup(string group) => PermissionIntegration.Manager.GetOrCreateGroup(group);
        public bool DeleteGroup(string group) => PermissionIntegration.Manager.DeleteGroup(group);
        public PermissionStore SnapshotPermissions() => PermissionIntegration.Manager.Snapshot();

        public ModerationResult Ban(string uuid, string reason) =>
            new(BasisPlayerModeration.TryBanUuid(uuid, reason, out string message), message);

        public ModerationResult IpBan(string uuid, string reason) =>
            new(BasisPlayerModeration.TryIpBan(uuid, reason, out string message), message);

        public ModerationResult Kick(string uuid, string reason) =>
            new(BasisPlayerModeration.TryKick(uuid, reason, out string message), message);

        public ModerationResult Unban(string uuid) =>
            BasisPlayerModeration.Unban(uuid)
                ? new(true, $"Player {uuid} unbanned.")
                : new(false, "UUID not banned");

        public ModerationResult UnbanIp(string ip) =>
            BasisPlayerModeration.UnbanIp(ip)
                ? new(true, $"IP {ip} unbanned.")
                : new(false, "IP not banned");

        public IReadOnlyList<BasisPlayerModeration.BannedPlayer> ListBans() => BasisPlayerModeration.ListBanned();

        public ModerationResult AddToAllowlist(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return new(false, "UUID was empty");
            if (_allowList == null) return new(false, "AllowList not initialized");
            // Fire-and-forget matches the in-game admin path: the in-memory set is
            // updated synchronously inside the call; only the file append is deferred.
            _ = _allowList.AddToAllowlistAsync(uuid);
            return new(true, $"Added {uuid} to allowlist.");
        }

        public ModerationResult RemoveFromAllowlist(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return new(false, "UUID was empty");
            if (_allowList == null) return new(false, "AllowList not initialized");
            _ = _allowList.RemoveFromAllowlistAsync(uuid);
            return new(true, $"Removed {uuid} from allowlist.");
        }

        public IReadOnlyList<string> ListAllowlist() =>
            _allowList == null ? Array.Empty<string>() : _allowList.ListAllowed();

        public void Dispose()
        {
            PermissionIntegration.Manager.OnPermissionsChanged -= _forwardPermissions;
            BasisPlayerModeration.OnBansChanged -= _forwardBans;
            if (_allowList != null)
                _allowList.OnChanged -= _forwardAllowlist;
        }
    }
}
#endif
