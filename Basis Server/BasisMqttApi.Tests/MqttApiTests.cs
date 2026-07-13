using Basis.Network.Server;
using Basis.Network.Server.Mqtt;
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
    public class MqttApiTests : IAsyncLifetime
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

        private ushort _port;
        private MqttServer _broker = null!;
        private BasisMqttApiHandler _handler = null!;
        private FakeControl _control = null!;
        private IMqttClient _client = null!;
        private string _replyTopic = null!;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MqttApplicationMessage>> _pendingReplies = new();

        public async Task InitializeAsync()
        {
            _port = FreePort();
            _broker = await StartBrokerAsync(_port);

            _control = new FakeControl();
            _handler = new BasisMqttApiHandler(BuildConfig(_port), _control);

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
            _handler.Dispose();
            if (_client.IsConnected)
                try { await _client.DisconnectAsync(); } catch { }
            _client.Dispose();
            try { await _broker.StopAsync(); } catch { }
            _broker.Dispose();
        }

        // ── Command dispatch and correlation ──────────────────────────────────

        [Fact]
        public async Task Announce_DispatchesToControl_AndRepliesOk()
        {
            var doc = await SendCommandAsync("announce", """{"message":"hello"}""");
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("hello", Assert.Single(_control.Announcements));
        }

        [Fact]
        public async Task Response_EchoesCorrelationData()
        {
            byte[] correlation = Guid.NewGuid().ToByteArray();
            var reply = await SendCommandRawAsync("status", "", correlation);
            Assert.Equal(correlation, reply.CorrelationData);
        }

        [Fact]
        public async Task UnknownCommand_ReturnsError()
        {
            var doc = await SendCommandAsync("does/not/exist", "{}");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("unknown command", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task MalformedJson_ReturnsError()
        {
            var doc = await SendCommandAsync("announce", "{not json");
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("invalid JSON payload", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task OversizePayload_ReturnsError()
        {
            var payload = new byte[BasisMqttApiHandler.MaxPayloadBytes + 1];
            Array.Fill(payload, (byte)'a');
            var reply = await SendCommandRawAsync("announce", payload, Guid.NewGuid().ToByteArray());
            var doc = JsonDocument.Parse(reply.ConvertPayloadToString());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("payload too large", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task MissingResponseTopic_ExecutesFireAndForget()
        {
            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"{_handler.TopicBase}/cmd/announce")
                .WithPayload("""{"message":"quiet"}""")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());

            string announced = await AwaitAsync(_control.AnnounceSignal.Task);
            Assert.Equal("quiet", announced);
        }

        // ── Command payload validation (mirrors the REST rules) ───────────────

        [Fact]
        public async Task AnnounceMissingMessage_ReturnsError()
        {
            var doc = await SendCommandAsync("announce", "{}");
            Assert.Equal("missing message", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task WorldsLoad_MissingUrl_ReturnsError()
        {
            var doc = await SendCommandAsync("worlds/load", """{"password":"pw"}""");
            Assert.Equal("missing url", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task WorldsLoad_NonHttpsUrl_ReturnsError()
        {
            var doc = await SendCommandAsync("worlds/load", """{"url":"http://example.com/w.bee","password":"pw"}""");
            Assert.Equal("url must use https://", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task WorldsLoad_FragmentPassword_ReachesControl()
        {
            string pw64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("secret"));
            var doc = await SendCommandAsync("worlds/load", $$"""{"url":"https://example.com/w.bee#{{pw64}}"}""");
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("net-1", doc.RootElement.GetProperty("netId").GetString());
            Assert.NotNull(_control.LastLoad);
            Assert.Equal("https://example.com/w.bee", _control.LastLoad!.Url);
            Assert.Equal("secret", _control.LastLoad!.Password);
        }

        [Fact]
        public async Task WorldsUnload_UnknownWorld_ReturnsError()
        {
            var doc = await SendCommandAsync("worlds/unload", """{"netId":"nope"}""");
            Assert.Equal("world not found", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task WorldsList_ReturnsWorlds()
        {
            _control.Worlds = new[] { new WorldInfo("w1", "https://example.com/w.bee", true, false, LoadStrategy.Immediate) };
            var doc = await SendCommandAsync("worlds/list", "");
            var worlds = doc.RootElement.GetProperty("worlds");
            Assert.Equal(1, worlds.GetArrayLength());
            Assert.Equal("w1", worlds[0].GetProperty("netId").GetString());
            Assert.True(worlds[0].GetProperty("persistent").GetBoolean());
        }

        [Fact]
        public async Task Status_ReturnsCounts()
        {
            _control.Players = new[] { new PlayerInfo(1, "uuid-1", "Alice", "pc") };
            _control.Worlds = new[] { new WorldInfo("w1", "https://example.com/w.bee", false, false, LoadStrategy.Immediate) };
            var doc = await SendCommandAsync("status", "");
            Assert.True(doc.RootElement.GetProperty("online").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("players").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("worlds").GetInt32());
        }

        // ── Event publication ──────────────────────────────────────────────────

        [Fact]
        public async Task PlayerJoined_PublishesEvent()
        {
            using var publisher = new BasisMqttEventPublisher(_handler, BuildConfig(_port), "Basis Server", _control);
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/player/joined",
                () => BasisServerEvents.RaisePlayerJoined(7, "uuid-7", "Bob"));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal(7, doc.RootElement.GetProperty("netId").GetInt32());
            Assert.Equal("uuid-7", doc.RootElement.GetProperty("uuid").GetString());
            Assert.Equal("Bob", doc.RootElement.GetProperty("displayName").GetString());
        }

        [Fact]
        public async Task WorldUnloaded_PublishesEvent()
        {
            using var publisher = new BasisMqttEventPublisher(_handler, BuildConfig(_port), "Basis Server", _control);
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/world/unloaded",
                () => BasisServerEvents.RaiseWorldUnloaded("w9"));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal("w9", doc.RootElement.GetProperty("netId").GetString());
        }

        [Fact]
        public async Task PlayerRejected_PublishesEvent()
        {
            using var publisher = new BasisMqttEventPublisher(_handler, BuildConfig(_port), "Basis Server", _control);
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/player/rejected",
                () => BasisServerEvents.RaisePlayerRejected("uuid-9", "You are not on the allowlist."));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal("uuid-9", doc.RootElement.GetProperty("uuid").GetString());
            Assert.Equal("You are not on the allowlist.", doc.RootElement.GetProperty("reason").GetString());
        }

        [Fact]
        public async Task PlayerRejected_PreIdentity_PublishesNullUuid()
        {
            using var publisher = new BasisMqttEventPublisher(_handler, BuildConfig(_port), "Basis Server", _control);
            var received = await CaptureEventAsync($"{_handler.TopicBase}/evt/player/rejected",
                () => BasisServerEvents.RaisePlayerRejected(null, "Banned IP"));
            var doc = JsonDocument.Parse(received.ConvertPayloadToString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("uuid").ValueKind);
            Assert.Equal("Banned IP", doc.RootElement.GetProperty("reason").GetString());
        }

        [Fact]
        public async Task Status_IsRetained_ForLateSubscribers()
        {
            using var publisher = new BasisMqttEventPublisher(_handler, BuildConfig(_port), "Basis Server", _control);
            // The publisher only hears Connected on the handler's next (re)connect,
            // so poll until the retained document lands.
            var retained = await PollRetainedStatusAsync(expectOnline: true);
            Assert.True(retained.RootElement.GetProperty("online").GetBoolean());
            Assert.Equal("Basis Server", retained.RootElement.GetProperty("serverName").GetString());
        }

        [Fact]
        public async Task PublisherDispose_RewritesRetainedStatusOffline()
        {
            var publisher = new BasisMqttEventPublisher(_handler, BuildConfig(_port), "Basis Server", _control);
            await PollRetainedStatusAsync(expectOnline: true);
            publisher.Dispose();
            var retained = await PollRetainedStatusAsync(expectOnline: false);
            Assert.False(retained.RootElement.GetProperty("online").GetBoolean());
        }

        // ── Reconnect ──────────────────────────────────────────────────────────

        [Fact]
        public async Task BrokerRestart_HandlerResubscribesCommands()
        {
            await _broker.StopAsync();
            _broker.Dispose();
            _broker = await StartBrokerAsync(_port);

            // The test client dropped too; reconnect it and its reply subscription.
            _client.Dispose();
            _client = await ConnectClientAsync();
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

            var doc = await WaitForHandlerAsync();
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
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

        /// <summary>Sends commands until the handler answers — covers its async initial connect and reconnect backoff.</summary>
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

        private async Task<JsonDocument> SendCommandAsync(string subtopic, string payload, TimeSpan? timeout = null)
        {
            var reply = await SendCommandRawAsync(subtopic, payload, Guid.NewGuid().ToByteArray(), timeout);
            return JsonDocument.Parse(reply.ConvertPayloadToString());
        }

        private Task<MqttApplicationMessage> SendCommandRawAsync(string subtopic, string payload, byte[] correlation, TimeSpan? timeout = null) =>
            SendCommandRawAsync(subtopic, System.Text.Encoding.UTF8.GetBytes(payload), correlation, timeout);

        private async Task<MqttApplicationMessage> SendCommandRawAsync(string subtopic, byte[] payload, byte[] correlation, TimeSpan? timeout = null)
        {
            var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReplies[Convert.ToBase64String(correlation)] = tcs;

            await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"{_handler.TopicBase}/cmd/{subtopic}")
                .WithPayload(payload)
                .WithResponseTopic(_replyTopic)
                .WithCorrelationData(correlation)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());

            return await AwaitAsync(tcs.Task, timeout);
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

        /// <summary>Connects a fresh subscriber until the retained status document reports the wanted state.</summary>
        private async Task<JsonDocument> PollRetainedStatusAsync(bool expectOnline)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                using var probe = await ConnectClientAsync();
                var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                probe.ApplicationMessageReceivedAsync += e =>
                {
                    if (e.ApplicationMessage.Retain)
                        tcs.TrySetResult(e.ApplicationMessage);
                    return Task.CompletedTask;
                };
                await probe.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(_handler.StatusTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build());

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
                await probe.DisconnectAsync();
                if (completed == tcs.Task)
                {
                    var doc = JsonDocument.Parse(tcs.Task.Result.ConvertPayloadToString());
                    if (doc.RootElement.GetProperty("online").GetBoolean() == expectOnline)
                        return doc;
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Retained status never reported online={expectOnline}.");
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

        private sealed class FakeControl : IServerControl
        {
            private readonly object _lock = new();
            public readonly List<string> Announcements = new();
            public WorldLoadParams? LastLoad;
            public IReadOnlyList<WorldInfo> Worlds = Array.Empty<WorldInfo>();
            public IReadOnlyList<PlayerInfo> Players = Array.Empty<PlayerInfo>();
            public readonly TaskCompletionSource<string> AnnounceSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void AnnounceAll(string message)
            {
                lock (_lock) Announcements.Add(message);
                AnnounceSignal.TrySetResult(message);
            }

            public bool AnnouncePlayer(string uuid, string message) => false;

            public string LoadWorld(WorldLoadParams p)
            {
                lock (_lock) LastLoad = p;
                return "net-1";
            }

            public bool UnloadWorld(string netId) => false;
            public int ClearAllWorlds() => 0;
            public IReadOnlyList<WorldInfo> ListWorlds() => Worlds;
            public IReadOnlyList<PlayerInfo> ListPlayers() => Players;
            public string SwitchWorld(SwitchWorldParams p, CancellationToken cancellationToken = default) => "net-2";
        }
    }
}
