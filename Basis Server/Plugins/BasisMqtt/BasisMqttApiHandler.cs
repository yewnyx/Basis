#if !UNITY_2017_1_OR_NEWER
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server.Mqtt
{
    /// <summary>
    /// MQTT binding for the management API. Owns the broker connection
    /// (TLS, credentials, last-will, reconnect with backoff), subscribes to
    /// "{prefix}/{serverId}/cmd/#", and dispatches commands registered by
    /// <see cref="BasisMqttApiRoutes"/> against <see cref="IServerControl"/> —
    /// the same seam the REST API binds to. Replies use the MQTT 5
    /// request/response pattern: a command's ResponseTopic receives the JSON
    /// result with CorrelationData echoed; commands without a ResponseTopic
    /// execute fire-and-forget and log their outcome.
    /// </summary>
    public sealed class BasisMqttApiHandler : IDisposable
    {
        /// <summary>Commands above this size are rejected — mirrors the REST API's body cap.</summary>
        public const int MaxPayloadBytes = 1 << 20;

        private static readonly JsonElement EmptyObject;
        private readonly IMqttClient _client;
        private readonly MqttClientOptions _options;
        private readonly MqttClientSubscribeOptions _subscription;
        private readonly ConcurrentDictionary<string, Func<JsonElement, string>> _handlers = new(StringComparer.Ordinal);
        private readonly BasisMqttPermissionSync _permissionSync;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _commandBase;
        private readonly MqttQualityOfServiceLevel _qos;
        private readonly string _brokerDescription;
        private int _disposed;

        /// <summary>Root of every topic this server uses: "{MqttTopicPrefix}/{MqttServerId}".</summary>
        public string TopicBase { get; }

        /// <summary>Retained status topic; also the connection's last-will target.</summary>
        public string StatusTopic { get; }

        /// <summary>Raised after every (re)connect once the command subscription is re-established.</summary>
        public event Action Connected;

        static BasisMqttApiHandler()
        {
            using var d = JsonDocument.Parse("{}");
            EmptyObject = d.RootElement.Clone();
        }

        public BasisMqttApiHandler(BasisMqttPluginConfig config, IServerControl control = null)
        {
            string serverId = SanitizeTopicSegment(string.IsNullOrWhiteSpace(config.MqttServerId)
                ? Environment.MachineName
                : config.MqttServerId);
            string prefix = SanitizeTopicSegment(string.IsNullOrWhiteSpace(config.MqttTopicPrefix)
                ? "basis"
                : config.MqttTopicPrefix);
            TopicBase = $"{prefix}/{serverId}";
            StatusTopic = $"{TopicBase}/evt/status";
            _commandBase = $"{TopicBase}/cmd/";
            _qos = (MqttQualityOfServiceLevel)Math.Clamp(config.MqttQoS, 0, 2);
            _brokerDescription = $"{config.MqttBrokerHost}:{config.MqttBrokerPort}";

            new BasisMqttApiRoutes(control ?? new BasisServerControl()).Register(this);

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithTcpServer(config.MqttBrokerHost, config.MqttBrokerPort)
                .WithClientId(string.IsNullOrWhiteSpace(config.MqttClientId) ? $"basis-{serverId}" : config.MqttClientId)
                .WithCleanStart(true)
                .WithWillTopic(StatusTopic)
                .WithWillPayload("""{"online":false}""")
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(_qos);
            if (!string.IsNullOrEmpty(config.MqttUsername))
                optionsBuilder.WithCredentials(config.MqttUsername, config.MqttPassword);
            if (config.MqttUseTls)
                optionsBuilder.WithTlsOptions(o => o.UseTls());
            _options = optionsBuilder.Build();

            _subscription = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic($"{_commandBase}#").WithQualityOfServiceLevel(_qos))
                .Build();

            _client = new MqttClientFactory().CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceived;
            _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));

            if (config.MqttPermissionSyncEnabled)
                _permissionSync = new BasisMqttPermissionSync(this);
        }

        /// <summary>
        /// Bind a command subtopic (e.g. "worlds/load") to a handler taking the
        /// parsed JSON payload and returning the JSON reply.
        /// </summary>
        public void RegisterCommandHandler(string subtopic, Func<JsonElement, string> handler)
        {
            if (!_handlers.TryAdd(subtopic, handler))
                throw new InvalidOperationException($"Command handler already registered for '{subtopic}'.");
        }

        /// <summary>
        /// Publish <paramref name="json"/> under "{TopicBase}/{relativeTopic}".
        /// Throws when the broker is unreachable — callers that publish
        /// best-effort events should catch and retry on <see cref="Connected"/>.
        /// </summary>
        public Task PublishAsync(string relativeTopic, string json, bool retain = false) =>
            PublishAbsoluteAsync($"{TopicBase}/{relativeTopic}", json, retain);

        private Task PublishAbsoluteAsync(string topic, string json, bool retain = false, byte[] correlationData = null)
        {
            var builder = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(json)
                .WithQualityOfServiceLevel(_qos)
                .WithRetainFlag(retain);
            if (correlationData is { Length: > 0 })
                builder.WithCorrelationData(correlationData);
            return _client.PublishAsync(builder.Build(), _cts.Token);
        }

        /// <summary>
        /// Keeps the broker connection alive: connects, (re)subscribes to the
        /// command filter, then watches for drops and retries with exponential
        /// backoff capped at one minute.
        /// </summary>
        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            int backoffSeconds = 1;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_client.IsConnected)
                    {
                        await _client.ConnectAsync(_options, token).ConfigureAwait(false);
                        await _client.SubscribeAsync(_subscription, token).ConfigureAwait(false);
                        backoffSeconds = 1;
                        BNL.Log($"[MQTT] Connected to {_brokerDescription}; commands at {_commandBase}#");
                        try { Connected?.Invoke(); }
                        catch (Exception e) { BNL.LogError($"[MQTT] Connected subscriber threw: {e}"); }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    BNL.LogWarning($"[MQTT] Connection to {_brokerDescription} failed: {e.Message}; retrying in {backoffSeconds}s");
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    backoffSeconds = Math.Min(backoffSeconds * 2, 60);
                }
            }
        }

        private Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var message = e.ApplicationMessage;
            if (!message.Topic.StartsWith(_commandBase, StringComparison.Ordinal))
                return Task.CompletedTask;
            // Handlers reach into server internals; keep the client's receive
            // pipeline free by dispatching on the pool.
            _ = Task.Run(() => DispatchAsync(message));
            return Task.CompletedTask;
        }

        private async Task DispatchAsync(MqttApplicationMessage message)
        {
            string subtopic = message.Topic.Substring(_commandBase.Length);
            string response;
            try
            {
                response = Execute(subtopic, message);
            }
            catch (Exception e)
            {
                BNL.LogError($"[MQTT] Command '{subtopic}' failed: {e}");
                response = """{"ok":false,"error":"internal server error"}""";
            }

            if (string.IsNullOrEmpty(message.ResponseTopic))
            {
                BNL.Log($"[MQTT] {subtopic} (fire-and-forget): {response}");
                return;
            }

            try
            {
                await PublishAbsoluteAsync(message.ResponseTopic, response, retain: false, message.CorrelationData).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                BNL.LogWarning($"[MQTT] Failed to publish response for '{subtopic}': {e.Message}");
            }
        }

        private string Execute(string subtopic, MqttApplicationMessage message)
        {
            if (!_handlers.TryGetValue(subtopic, out var handler))
                return $$"""{"ok":false,"error":{{JsonSerializer.Serialize($"unknown command '{subtopic}'")}}}""";
            if (message.Payload.Length > MaxPayloadBytes)
                return """{"ok":false,"error":"payload too large"}""";

            string json = message.ConvertPayloadToString();
            JsonElement body;
            if (string.IsNullOrWhiteSpace(json))
            {
                body = EmptyObject;
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    body = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return """{"ok":false,"error":"invalid JSON payload"}""";
                }
            }
            return handler(body);
        }

        // MQTT treats '/' as the level separator and '+'/'#' as wildcards, so a
        // configured id containing them would corrupt the topic scheme.
        private static string SanitizeTopicSegment(string segment) =>
            segment.Trim().Replace('/', '-').Replace('+', '-').Replace('#', '-');

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _permissionSync?.Dispose();
            _cts.Cancel();
            try
            {
                if (_client.IsConnected)
                    _client.DisconnectAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            _client.Dispose();
            _cts.Dispose();
        }
    }
}
#endif
