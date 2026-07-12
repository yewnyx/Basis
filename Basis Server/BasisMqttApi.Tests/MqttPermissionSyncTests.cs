using Basis.Network.Server;
using Basis.Network.Server.Mqtt;
using BasisNetworkServer.Security;
using BasisPermissions;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace BasisMqttApi.Tests
{
    [Collection("MqttApi")]
    public class MqttPermissionSyncTests : IAsyncLifetime
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

        private ushort _port;
        private MqttServer _broker = null!;
        private BasisMqttApiHandler _handler = null!;
        private FakeModerationControl _control = null!;
        private BasisMqttPermissionSync? _sync;
        private IMqttClient _client = null!;
        private string _replyTopic = null!;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MqttApplicationMessage>> _pendingReplies = new();

        public async Task InitializeAsync()
        {
            _port = FreePort();
            _broker = await StartBrokerAsync(_port);

            _control = new FakeModerationControl();
            _handler = new BasisMqttApiHandler(BuildConfig(_port), new NullServerControl());

            _client = await ConnectClientAsync();
            _replyTopic = $"test/replies/{Guid.NewGuid():N}";
            _client.ApplicationMessageReceivedAsync += e =>
            {
                if (e.ApplicationMessage.Topic == _replyTopic &&
                    e.ApplicationMessage.CorrelationData is { Length: > 0 } data &&
                    _pendingReplies.TryRemove(Convert.ToBase64String(data), out var tcs))
                {
                    tcs.TrySetResult(e.ApplicationMessage);
                }
                return Task.CompletedTask;
            };
            await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(_replyTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build());

            await WaitForHandlerAsync();
        }

        public async Task DisposeAsync()
        {
            _sync?.Dispose();
            _handler.Dispose();
            if (_client.IsConnected)
                try { await _client.DisconnectAsync(); } catch { }
            _client.Dispose();
            try { await _broker.StopAsync(); } catch { }
            _broker.Dispose();
        }

        /// <summary>The sync registers its command handlers once per handler, so each test creates it exactly once.</summary>
        private BasisMqttPermissionSync CreateSync()
        {
            _sync = new BasisMqttPermissionSync(_handler, _control);
            return _sync;
        }

        // ── Permission-store commands ──────────────────────────────────────────

        [Fact]
        public async Task UserAddNode_DispatchesToControl_AndAcksWithRev()
        {
            CreateSync();
            var doc = await SendCommandAsync("perm/user/add-node", """{"uuid":"did:key:u1","node":"basis.moderation.kick"}""");
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("rev").GetInt64());
            Assert.Equal(("user/add-node", "did:key:u1", "basis.moderation.kick"), Assert.Single(_control.Calls));
        }

        [Fact]
        public async Task UserMutation_PublishesChangedEvent_WithUuidScope()
        {
            CreateSync();
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/perm/changed",
                () => _ = SendCommandAsync("perm/user/add-group", """{"uuid":"did:key:u2","group":"moderator"}"""));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("rev").GetInt64());
            Assert.Equal("permissions", doc.RootElement.GetProperty("scope").GetString());
            Assert.Equal("did:key:u2", doc.RootElement.GetProperty("uuid").GetString());
        }

        [Fact]
        public async Task GroupMutation_PublishesChangedEvent_WithNullUuid()
        {
            CreateSync();
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/perm/changed",
                () => _ = SendCommandAsync("perm/group/add-node", """{"group":"moderator","node":"basis.moderation.ban"}"""));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal("permissions", doc.RootElement.GetProperty("scope").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("uuid").ValueKind);
        }

        [Fact]
        public async Task GroupDelete_UnknownGroup_ReturnsError()
        {
            _control.MissingGroups = true;
            CreateSync();
            var doc = await SendCommandAsync("perm/group/delete", """{"group":"nope"}""");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("group not found", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task MissingField_ReturnsValidationError()
        {
            CreateSync();
            var doc = await SendCommandAsync("perm/user/add-node", """{"node":"basis.command.help"}""");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("missing uuid", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task ExternalMutation_AlsoPublishesChangedEvent()
        {
            CreateSync();
            // A change made by another writer (in-game admin, console) reaches the
            // sync through the control's event, not through an MQTT command.
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/perm/changed",
                () => _control.RaisePermissionsChanged("did:key:external"));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal("did:key:external", doc.RootElement.GetProperty("uuid").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("rev").GetInt64());
        }

        // ── Moderation commands ────────────────────────────────────────────────

        [Fact]
        public async Task Ban_DispatchesToControl_AndPublishesBansScope()
        {
            CreateSync();
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/perm/changed",
                () => _ = SendCommandAsync("perm/ban", """{"uuid":"did:key:bad","reason":"griefing"}"""));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal("bans", doc.RootElement.GetProperty("scope").GetString());
            Assert.Equal(("ban", "did:key:bad", "griefing"), Assert.Single(_control.Calls));
        }

        [Fact]
        public async Task Ban_ProtectedTarget_ReturnsError()
        {
            _control.ProtectedTarget = true;
            CreateSync();
            var doc = await SendCommandAsync("perm/ban", """{"uuid":"did:key:vip","reason":"nope"}""");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("Target is protected", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task Kick_AcksWithMessage_WithoutChangedEvent()
        {
            CreateSync();
            var doc = await SendCommandAsync("perm/kick", """{"uuid":"did:key:afk","reason":"idle"}""");
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("Player did:key:afk kicked.", doc.RootElement.GetProperty("message").GetString());
            // A kick changes nothing in any store, so the revision stays put.
            Assert.Equal(0, doc.RootElement.GetProperty("rev").GetInt64());
        }

        [Fact]
        public async Task Unban_UnknownUuid_ReturnsError()
        {
            CreateSync();
            var doc = await SendCommandAsync("perm/unban", """{"uuid":"did:key:never"}""");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("UUID not banned", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task AllowlistAdd_DispatchesToControl_AndPublishesAllowlistScope()
        {
            CreateSync();
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/perm/changed",
                () => _ = SendCommandAsync("perm/allowlist/add", """{"uuid":"did:key:friend"}"""));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal("allowlist", doc.RootElement.GetProperty("scope").GetString());
            Assert.Contains("did:key:friend", _control.Allowlist);
        }

        // ── Snapshot ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Snapshot_ReturnsStoreContents()
        {
            _control.SeedUser("did:key:u1", nodes: new[] { "basis.command.help" }, groups: new[] { "default" });
            _control.SeedGroup("moderator", nodes: new[] { "basis.moderation.kick" }, parents: new[] { "default" });
            _control.Allowlist.Add("did:key:friend");
            _control.Bans.Add(new BasisPlayerModeration.BannedPlayer
            {
                UUID = "did:key:bad",
                Reason = "griefing",
                HasBannedIp = false,
                BannedIp = string.Empty,
                TimeOfBan = "2026-07-10 00:00:00",
            });
            CreateSync();

            var doc = await SendCommandAsync("perm/snapshot", "");
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(1, root.GetProperty("v").GetInt32());
            Assert.Equal("basis.command.help", root.GetProperty("users").GetProperty("did:key:u1").GetProperty("nodes")[0].GetString());
            Assert.Equal("default", root.GetProperty("groups").GetProperty("moderator").GetProperty("parents")[0].GetString());
            Assert.Equal("did:key:friend", root.GetProperty("allowlist")[0].GetString());
            var ban = root.GetProperty("bans")[0];
            Assert.Equal("did:key:bad", ban.GetProperty("uuid").GetString());
            Assert.Equal(JsonValueKind.Null, ban.GetProperty("ip").ValueKind);
        }

        [Fact]
        public async Task BootSnapshot_IsPublishedOnCreation()
        {
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/perm/snapshot",
                () => CreateSync());
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("rev").GetInt64());
            Assert.False(doc.RootElement.TryGetProperty("ok", out _));
        }

        // ── Handler integration ────────────────────────────────────────────────

        [Fact]
        public async Task MqttPermissionSyncEnabled_RegistersCommandsOnTheHandler()
        {
            // A second handler with the flag on and its own topic base; it builds
            // the default control over the real (empty) stores, so the snapshot
            // command answering proves the wiring without touching server state.
            var config = BuildConfig(_port);
            config.MqttServerId = "test-server-perm";
            config.MqttPermissionSyncEnabled = true;
            using var enabled = new BasisMqttApiHandler(config, new NullServerControl());

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                try
                {
                    var doc = await SendCommandAsync("perm/snapshot", "", TimeSpan.FromSeconds(2), enabled.TopicBase);
                    Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                    return;
                }
                catch (TimeoutException) when (DateTime.UtcNow < deadline)
                {
                }
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static BasisMqttPluginConfig BuildConfig(ushort port) => new()
        {
            MqttEnabled = true,
            MqttBrokerHost = "127.0.0.1",
            MqttBrokerPort = port,
            MqttUseTls = false,
            MqttTopicPrefix = "basis",
            MqttServerId = "test-server",
            MqttQoS = 1,
            MqttStatusIntervalSeconds = 0,
            MqttPermissionSyncEnabled = false,
        };

        private static async Task<MqttServer> StartBrokerAsync(ushort port)
        {
            var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(port)
                .Build());
            await broker.StartAsync();
            return broker;
        }

        private async Task<IMqttClient> ConnectClientAsync()
        {
            var client = new MqttClientFactory().CreateMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder()
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                .WithTcpServer("127.0.0.1", _port)
                .Build());
            return client;
        }

        /// <summary>Sends commands until the handler answers — covers its async initial connect.</summary>
        private async Task<JsonDocument> WaitForHandlerAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                try
                {
                    return await SendCommandAsync("status", "", TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException) when (DateTime.UtcNow < deadline)
                {
                }
            }
        }

        private async Task<JsonDocument> SendCommandAsync(string subtopic, string payload, TimeSpan? timeout = null, string? topicBase = null)
        {
            byte[] correlation = Guid.NewGuid().ToByteArray();
            var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReplies[Convert.ToBase64String(correlation)] = tcs;

            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"{topicBase ?? _handler.TopicBase}/cmd/{subtopic}")
                .WithPayload(payload)
                .WithResponseTopic(_replyTopic)
                .WithCorrelationData(correlation)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());

            var reply = await AwaitAsync(tcs.Task, timeout);
            return JsonDocument.Parse(reply.ConvertPayloadToString());
        }

        /// <summary>Subscribes to <paramref name="topic"/>, runs <paramref name="trigger"/>, returns the first message.</summary>
        private async Task<MqttApplicationMessage> CaptureEventAsync(string topic, Action trigger)
        {
            var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Capture(MqttApplicationMessageReceivedEventArgs e)
            {
                if (e.ApplicationMessage.Topic == topic)
                    tcs.TrySetResult(e.ApplicationMessage);
                return Task.CompletedTask;
            }
            _client.ApplicationMessageReceivedAsync += Capture;
            try
            {
                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build());
                trigger();
                return await AwaitAsync(tcs.Task);
            }
            finally
            {
                _client.ApplicationMessageReceivedAsync -= Capture;
            }
        }

        private static async Task<T> AwaitAsync<T>(Task<T> task, TimeSpan? timeout = null)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout ?? ResponseTimeout));
            if (completed != task)
                throw new TimeoutException("Timed out waiting for MQTT message.");
            return await task;
        }

        private static ushort FreePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        /// <summary>The permission sync never touches world/player control; commands here should fail loudly if it did.</summary>
        private sealed class NullServerControl : IServerControl
        {
            public void AnnounceAll(string message) { }
            public bool AnnouncePlayer(string uuid, string message) => false;
            public string LoadWorld(WorldLoadParams p) => throw new InvalidOperationException();
            public bool UnloadWorld(string netId) => false;
            public int ClearAllWorlds() => 0;
            public IReadOnlyList<WorldInfo> ListWorlds() => Array.Empty<WorldInfo>();
            public IReadOnlyList<PlayerInfo> ListPlayers() => Array.Empty<PlayerInfo>();
            public string SwitchWorld(SwitchWorldParams p, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        }

        /// <summary>
        /// In-memory stand-in honouring the IBasisModerationControl contract:
        /// mutators that change state raise the matching change event.
        /// </summary>
        private sealed class FakeModerationControl : IBasisModerationControl
        {
            public event Action<string>? OnPermissionsChanged;
            public event Action? OnBansChanged;
            public event Action? OnAllowlistChanged;

            public readonly List<(string Op, string A, string? B)> Calls = new();
            public readonly PermissionStore Store = new();
            public readonly List<string> Allowlist = new();
            public readonly List<BasisPlayerModeration.BannedPlayer> Bans = new();
            public bool ProtectedTarget;
            public bool MissingGroups;

            public void RaisePermissionsChanged(string? uuid) => OnPermissionsChanged?.Invoke(uuid!);

            public void SeedUser(string uuid, string[] nodes, string[] groups)
            {
                var user = new PermissionUser { Uuid = uuid };
                foreach (var n in nodes) user.Nodes.Add(n);
                foreach (var g in groups) user.Groups.Add(g);
                Store.Users[uuid] = user;
            }

            public void SeedGroup(string name, string[] nodes, string[] parents)
            {
                var group = new PermissionGroup { Name = name };
                foreach (var n in nodes) group.Nodes.Add(n);
                foreach (var p in parents) group.Parents.Add(p);
                Store.Groups[name] = group;
            }

            private void Permission(string op, string a, string? b, string? changedUuid)
            {
                lock (Calls) Calls.Add((op, a, b));
                OnPermissionsChanged?.Invoke(changedUuid!);
            }

            public void AddUserNode(string uuid, string node) => Permission("user/add-node", uuid, node, uuid);
            public void RemoveUserNode(string uuid, string node) => Permission("user/remove-node", uuid, node, uuid);
            public void AddUserToGroup(string uuid, string group) => Permission("user/add-group", uuid, group, uuid);
            public void RemoveUserFromGroup(string uuid, string group) => Permission("user/remove-group", uuid, group, uuid);
            public void AddGroupNode(string group, string node) => Permission("group/add-node", group, node, null);
            public void RemoveGroupNode(string group, string node) => Permission("group/remove-node", group, node, null);
            public void AddGroupParent(string group, string parent) => Permission("group/add-parent", group, parent, null);
            public void RemoveGroupParent(string group, string parent) => Permission("group/remove-parent", group, parent, null);

            public void CreateGroup(string group)
            {
                lock (Calls) Calls.Add(("group/create", group, null));
            }

            public bool DeleteGroup(string group)
            {
                if (MissingGroups) return false;
                Permission("group/delete", group, null, null);
                return true;
            }

            public PermissionStore SnapshotPermissions() => Store;

            public ModerationResult Ban(string uuid, string reason)
            {
                if (ProtectedTarget) return new(false, "Target is protected");
                lock (Calls) Calls.Add(("ban", uuid, reason));
                OnBansChanged?.Invoke();
                return new(true, $"Player {uuid} banned (offline).");
            }

            public ModerationResult IpBan(string uuid, string reason)
            {
                lock (Calls) Calls.Add(("ipban", uuid, reason));
                OnBansChanged?.Invoke();
                return new(true, $"Player {uuid} and IP 127.0.0.1 banned.");
            }

            public ModerationResult Kick(string uuid, string reason)
            {
                lock (Calls) Calls.Add(("kick", uuid, reason));
                return new(true, $"Player {uuid} kicked.");
            }

            public ModerationResult Unban(string uuid) => new(false, "UUID not banned");
            public ModerationResult UnbanIp(string ip) => new(false, "IP not banned");
            public IReadOnlyList<BasisPlayerModeration.BannedPlayer> ListBans() => Bans;

            public ModerationResult AddToAllowlist(string uuid)
            {
                Allowlist.Add(uuid);
                OnAllowlistChanged?.Invoke();
                return new(true, $"Added {uuid} to allowlist.");
            }

            public ModerationResult RemoveFromAllowlist(string uuid)
            {
                Allowlist.Remove(uuid);
                OnAllowlistChanged?.Invoke();
                return new(true, $"Removed {uuid} from allowlist.");
            }

            public ModerationResult SetAllowlist(IReadOnlyCollection<string> uuids)
            {
                Allowlist.Clear();
                Allowlist.AddRange(uuids);
                OnAllowlistChanged?.Invoke();
                return new(true, $"Allowlist replaced ({uuids.Count} entries).");
            }

            public IReadOnlyList<string> ListAllowlist() => Allowlist;

            public void Dispose() { }
        }
    }
}
