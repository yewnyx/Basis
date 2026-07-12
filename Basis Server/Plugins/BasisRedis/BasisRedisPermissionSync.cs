#if !UNITY_2017_1_OR_NEWER
using BasisNetworkServer.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// Permission, ban and allowlist management over Redis, binding
    /// <see cref="IBasisModerationControl"/> to "perm/…" commands and
    /// "perm/changed" / "perm/snapshot" stream events on the
    /// <see cref="BasisRedisApiHandler"/>. A near-verbatim copy of the MQTT
    /// plugin's permission sync — deliberately: this branch measures what a
    /// second binding costs without a shared bus abstraction.
    ///
    /// The protocol is push-state with reconciliation rather than remote
    /// enforcement: mutations apply to the server's local stores (which keep
    /// enforcing through Redis outages), and observers track a monotonic
    /// revision counter. Every change — from Redis, the in-game admin channel
    /// or the console — bumps the revision and appends "perm/changed";
    /// command acks carry the revision their mutation produced. A manager that
    /// sees a revision it cannot attribute sends "perm/snapshot" (or reads the
    /// snapshot appended on every (re)connect) and diffs. The stream is
    /// durable, so unlike MQTT a disconnected manager can also replay.
    /// </summary>
    public sealed class BasisRedisPermissionSync : IDisposable
    {
        /// <summary>Major version of the payload schema, carried as "v" in events and snapshots.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Events queued while Redis is unreachable beyond this count drop oldest-first.</summary>
        public const int QueueCapacity = 256;

        private readonly BasisRedisApiHandler _handler;
        private readonly IBasisModerationControl _control;
        private readonly Channel<(string Topic, string Json)> _queue;
        private readonly CancellationTokenSource _cts = new();
        private long _rev;
        private int _disposed;

        /// <summary>Takes ownership of <paramref name="control"/> (or of the default implementation when null) and disposes it with this instance.</summary>
        public BasisRedisPermissionSync(BasisRedisApiHandler handler, IBasisModerationControl control = null)
        {
            _handler = handler;
            _control = control ?? new BasisModerationControl();
            _queue = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

            RegisterCommands(handler);

            _control.OnPermissionsChanged += HandlePermissionsChanged;
            _control.OnBansChanged += HandleBansChanged;
            _control.OnAllowlistChanged += HandleAllowlistChanged;
            _handler.Connected += HandleConnected;

            _ = Task.Run(() => PumpAsync(_cts.Token));

            // The handler may have connected before we subscribed to Connected;
            // publish the boot snapshot now. If the broker isn't reachable yet
            // this drops and the Connected raise covers it.
            EnqueueSnapshot();
        }

        private void RegisterCommands(BasisRedisApiHandler handler)
        {
            handler.RegisterCommandHandler("perm/user/add-node", b => MutatePair(b, "uuid", "node", _control.AddUserNode));
            handler.RegisterCommandHandler("perm/user/remove-node", b => MutatePair(b, "uuid", "node", _control.RemoveUserNode));
            handler.RegisterCommandHandler("perm/user/add-group", b => MutatePair(b, "uuid", "group", _control.AddUserToGroup));
            handler.RegisterCommandHandler("perm/user/remove-group", b => MutatePair(b, "uuid", "group", _control.RemoveUserFromGroup));
            handler.RegisterCommandHandler("perm/group/add-node", b => MutatePair(b, "group", "node", _control.AddGroupNode));
            handler.RegisterCommandHandler("perm/group/remove-node", b => MutatePair(b, "group", "node", _control.RemoveGroupNode));
            handler.RegisterCommandHandler("perm/group/add-parent", b => MutatePair(b, "group", "parent", _control.AddGroupParent));
            handler.RegisterCommandHandler("perm/group/remove-parent", b => MutatePair(b, "group", "parent", _control.RemoveGroupParent));
            handler.RegisterCommandHandler("perm/group/create", b => MutateSingle(b, "group", _control.CreateGroup));
            handler.RegisterCommandHandler("perm/group/delete", DeleteGroup);
            handler.RegisterCommandHandler("perm/ban", b => Moderate(b, _control.Ban));
            handler.RegisterCommandHandler("perm/ipban", b => Moderate(b, _control.IpBan));
            handler.RegisterCommandHandler("perm/kick", b => Moderate(b, _control.Kick));
            handler.RegisterCommandHandler("perm/unban", b => ModerateSingle(b, "uuid", _control.Unban));
            handler.RegisterCommandHandler("perm/unban-ip", b => ModerateSingle(b, "ip", _control.UnbanIp));
            handler.RegisterCommandHandler("perm/allowlist/add", b => ModerateSingle(b, "uuid", _control.AddToAllowlist));
            handler.RegisterCommandHandler("perm/allowlist/remove", b => ModerateSingle(b, "uuid", _control.RemoveFromAllowlist));
            handler.RegisterCommandHandler("perm/allowlist/set", SetAllowlist);
            handler.RegisterCommandHandler("perm/snapshot", Snapshot);
        }

        // ── Command handlers ───────────────────────────────────────────────────

        private string MutatePair(JsonElement body, string firstField, string secondField, Action<string, string> mutate)
        {
            if (!TryGetString(body, firstField, out string first, out string error)) return Fail(error);
            if (!TryGetString(body, secondField, out string second, out error)) return Fail(error);
            mutate(first, second);
            return Ack();
        }

        private string MutateSingle(JsonElement body, string field, Action<string> mutate)
        {
            if (!TryGetString(body, field, out string value, out string error)) return Fail(error);
            mutate(value);
            return Ack();
        }

        private string DeleteGroup(JsonElement body)
        {
            if (!TryGetString(body, "group", out string group, out string error)) return Fail(error);
            if (!_control.DeleteGroup(group)) return Fail("group not found");
            return Ack();
        }

        private string Moderate(JsonElement body, Func<string, string, ModerationResult> op)
        {
            if (!TryGetString(body, "uuid", out string uuid, out string error)) return Fail(error);
            if (!TryGetString(body, "reason", out string reason, out error)) return Fail(error);
            return Finish(op(uuid, reason));
        }

        private string ModerateSingle(JsonElement body, string field, Func<string, ModerationResult> op)
        {
            if (!TryGetString(body, field, out string value, out string error)) return Fail(error);
            return Finish(op(value));
        }

        private string SetAllowlist(JsonElement body)
        {
            if (!body.TryGetProperty("uuids", out JsonElement uuidsProperty) || uuidsProperty.ValueKind != JsonValueKind.Array)
                return Fail("missing uuids array");

            var uuids = new List<string>(uuidsProperty.GetArrayLength());
            foreach (JsonElement entry in uuidsProperty.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                    return Fail("uuids must be non-empty strings");
                uuids.Add(entry.GetString()!);
            }
            return Finish(_control.SetAllowlist(uuids));
        }

        private string Snapshot(JsonElement _) => BuildSnapshotJson(includeOk: true);

        // ── Change events → evt/perm/changed ───────────────────────────────────

        private void HandlePermissionsChanged(string uuid) => EnqueueChanged("permissions", uuid);
        private void HandleBansChanged() => EnqueueChanged("bans", null);
        private void HandleAllowlistChanged() => EnqueueChanged("allowlist", null);
        private void HandleConnected() => EnqueueSnapshot();

        private void EnqueueChanged(string scope, string uuid)
        {
            long rev = Interlocked.Increment(ref _rev);
            _queue.Writer.TryWrite(("perm/changed",
                $$"""{"v":{{SchemaVersion}},"rev":{{rev}},"scope":{{JsonSerializer.Serialize(scope)}},"uuid":{{JsonSerializer.Serialize(uuid)}}}"""));
        }

        private void EnqueueSnapshot()
        {
            try
            {
                _queue.Writer.TryWrite(("perm/snapshot", BuildSnapshotJson(includeOk: false)));
            }
            catch (Exception e)
            {
                BNL.LogWarning($"[Redis] Failed to build permission snapshot: {e.Message}");
            }
        }

        private async Task PumpAsync(CancellationToken token)
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var item))
                    {
                        try
                        {
                            await _handler.PublishEventAsync(item.Topic, item.Json).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // The reconnect snapshot reconciles anything dropped here.
                            BNL.LogWarning($"[Redis] Dropped permission event on '{item.Topic}': {e.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ── Payloads ───────────────────────────────────────────────────────────

        private string Ack() =>
            $$"""{"ok":true,"rev":{{Interlocked.Read(ref _rev)}}}""";

        private string Finish(ModerationResult result) =>
            result.Ok
                ? $$"""{"ok":true,"rev":{{Interlocked.Read(ref _rev)}},"message":{{JsonSerializer.Serialize(result.Message)}}}"""
                : Fail(result.Message);

        private static string Fail(string error) =>
            $$"""{"ok":false,"error":{{JsonSerializer.Serialize(error)}}}""";

        private string BuildSnapshotJson(bool includeOk)
        {
            var store = _control.SnapshotPermissions();

            var users = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var user in store.Users.Values)
                users[user.Uuid] = new { nodes = user.Nodes.ToArray(), groups = user.Groups.ToArray() };

            var groups = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var group in store.Groups.Values)
                groups[group.Name] = new { nodes = group.Nodes.ToArray(), parents = group.Parents.ToArray() };

            var bans = _control.ListBans().Select(ban => new
            {
                uuid = ban.UUID,
                reason = ban.Reason,
                ip = ban.HasBannedIp ? ban.BannedIp : null,
                time = ban.TimeOfBan,
            }).ToArray();

            var payload = new Dictionary<string, object>(StringComparer.Ordinal);
            if (includeOk) payload["ok"] = true;
            payload["v"] = SchemaVersion;
            payload["rev"] = Interlocked.Read(ref _rev);
            payload["users"] = users;
            payload["groups"] = groups;
            payload["allowlist"] = _control.ListAllowlist();
            payload["bans"] = bans;
            return JsonSerializer.Serialize(payload);
        }

        private static bool TryGetString(JsonElement body, string name, out string value, out string error)
        {
            value = "";
            error = "";
            if (!body.TryGetProperty(name, out JsonElement property))
            {
                error = $"missing {name}";
                return false;
            }
            if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            {
                error = $"{name} must be a non-empty string";
                return false;
            }
            value = property.GetString()!;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _control.OnPermissionsChanged -= HandlePermissionsChanged;
            _control.OnBansChanged -= HandleBansChanged;
            _control.OnAllowlistChanged -= HandleAllowlistChanged;
            _handler.Connected -= HandleConnected;
            _cts.Cancel();
            _queue.Writer.TryComplete();
            _control.Dispose();
            _cts.Dispose();
        }
    }
}
#endif
