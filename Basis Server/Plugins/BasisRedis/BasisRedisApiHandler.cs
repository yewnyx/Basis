#if !UNITY_2017_1_OR_NEWER
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// Redis binding for the management API — the same command surface as the
    /// MQTT plugin, mapped onto Redis primitives per the management-protocol
    /// spec's Redis sketch:
    ///
    ///  - Commands arrive as JSON envelopes on the list "{base}:cmd"
    ///    (manager RPUSHes, we LPOP — FIFO). A PUBLISH on "{base}:cmd:wake"
    ///    tells us to drain immediately; a slow fallback poll catches wakes
    ///    lost while disconnected, so the queue is at-least-once end to end.
    ///  - The envelope is {"cmd":"worlds/load","payload":{…},"cid":"…","reply":"…"}.
    ///    With "reply" set, the answer lands as {"cid":…,"reply":{…}} RPUSHed
    ///    to that list (60s TTL — replies must not outlive a dead manager);
    ///    without it the command is fire-and-forget.
    ///  - Events are XADDed to the stream "{base}:evt" (fields: type, json).
    ///  - Status is a TTL'd string at "{base}:status" (see the event publisher).
    /// </summary>
    public sealed class BasisRedisApiHandler : IDisposable
    {
        /// <summary>Envelopes above this size are rejected — mirrors the REST/MQTT cap.</summary>
        public const int MaxPayloadBytes = 1 << 20;

        /// <summary>How long a reply list lingers for its manager to collect.</summary>
        public static readonly TimeSpan ReplyTtl = TimeSpan.FromSeconds(60);

        /// <summary>Fallback drain period when no wake arrives.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        private static readonly JsonElement EmptyObject;
        private readonly IRedisConnection _connection;
        private readonly ConcurrentDictionary<string, Func<JsonElement, string>> _handlers = new(StringComparer.Ordinal);
        private readonly BasisRedisPermissionSync _permissionSync;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _drainSignal = new(0, 1);
        private readonly int _streamMaxLength;
        private int _disposed;

        /// <summary>Root of every key this server uses: "{RedisKeyPrefix}:{RedisServerId}".</summary>
        public string KeyBase { get; }

        public string CommandListKey { get; }
        public string CommandWakeChannel { get; }
        public string EventStreamKey { get; }
        public string StatusKey { get; }

        /// <summary>Raised on connect and every reconnect (forwarded from the connection).</summary>
        public event Action Connected;

        static BasisRedisApiHandler()
        {
            using var d = JsonDocument.Parse("{}");
            EmptyObject = d.RootElement.Clone();
        }

        public BasisRedisApiHandler(BasisRedisPluginConfig config, IRedisConnection connection = null, IServerControl control = null)
        {
            string serverId = SanitizeKeySegment(string.IsNullOrWhiteSpace(config.RedisServerId)
                ? Environment.MachineName
                : config.RedisServerId);
            string prefix = SanitizeKeySegment(string.IsNullOrWhiteSpace(config.RedisKeyPrefix)
                ? "basis"
                : config.RedisKeyPrefix);
            KeyBase = $"{prefix}:{serverId}";
            CommandListKey = $"{KeyBase}:cmd";
            CommandWakeChannel = $"{KeyBase}:cmd:wake";
            EventStreamKey = $"{KeyBase}:evt";
            StatusKey = $"{KeyBase}:status";
            _streamMaxLength = Math.Max(config.RedisEventStreamMaxLength, 100);

            new BasisRedisApiRoutes(control ?? new BasisServerControl()).Register(this);

            _connection = connection ?? new RedisConnection(config);
            _connection.Connected += HandleConnected;
            _connection.Subscribe(CommandWakeChannel, _ => SignalDrain());

            _ = Task.Run(() => DrainLoopAsync(_cts.Token));

            if (config.RedisPermissionSyncEnabled)
                _permissionSync = new BasisRedisPermissionSync(this);
        }

        /// <summary>
        /// Bind a command name (e.g. "worlds/load") to a handler taking the
        /// parsed JSON payload and returning the JSON reply — the same
        /// signature the MQTT binding uses, which is what lets the routes and
        /// permission-sync sources be near-verbatim copies.
        /// </summary>
        public void RegisterCommandHandler(string command, Func<JsonElement, string> handler)
        {
            if (!_handlers.TryAdd(command, handler))
                throw new InvalidOperationException($"Command handler already registered for '{command}'.");
        }

        /// <summary>Append one event to the server's stream.</summary>
        public Task PublishEventAsync(string type, string json) =>
            _connection.StreamAddAsync(EventStreamKey, type, json, _streamMaxLength);

        /// <summary>Write the status document; a TTL of null persists it (the clean-shutdown offline write).</summary>
        public Task SetStatusAsync(string json, TimeSpan? ttl) =>
            _connection.StringSetAsync(StatusKey, json, ttl);

        private void HandleConnected()
        {
            SignalDrain();
            try { Connected?.Invoke(); }
            catch (Exception e) { BNL.LogError($"[Redis] Connected subscriber threw: {e}"); }
        }

        private void SignalDrain()
        {
            // Binary semaphore: coalesce any number of wakes into one drain.
            try { _drainSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private async Task DrainLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _drainSignal.WaitAsync(PollInterval, token).ConfigureAwait(false);
                    string envelope;
                    while ((envelope = await _connection.ListLeftPopAsync(CommandListKey).ConfigureAwait(false)) != null)
                    {
                        await DispatchAsync(envelope).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    BNL.LogWarning($"[Redis] Command drain failed: {e.Message}");
                }
            }
        }

        private async Task DispatchAsync(string envelopeJson)
        {
            string command = null, cid = null, reply = null;
            string response;
            try
            {
                if (envelopeJson.Length > MaxPayloadBytes)
                {
                    response = """{"ok":false,"error":"payload too large"}""";
                }
                else
                {
                    using var doc = JsonDocument.Parse(envelopeJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("cmd", out var commandProperty) && commandProperty.ValueKind == JsonValueKind.String)
                        command = commandProperty.GetString();
                    if (root.TryGetProperty("cid", out var cidProperty) && cidProperty.ValueKind == JsonValueKind.String)
                        cid = cidProperty.GetString();
                    if (root.TryGetProperty("reply", out var replyProperty) && replyProperty.ValueKind == JsonValueKind.String)
                        reply = replyProperty.GetString();

                    JsonElement payload = root.TryGetProperty("payload", out var payloadProperty)
                        ? payloadProperty.Clone()
                        : EmptyObject;

                    response = Execute(command, payload);
                }
            }
            catch (JsonException)
            {
                response = """{"ok":false,"error":"invalid JSON envelope"}""";
            }
            catch (Exception e)
            {
                BNL.LogError($"[Redis] Command '{command}' failed: {e}");
                response = """{"ok":false,"error":"internal server error"}""";
            }

            if (string.IsNullOrEmpty(reply))
            {
                BNL.Log($"[Redis] {command ?? "<malformed>"} (fire-and-forget): {response}");
                return;
            }

            try
            {
                string answer = $$"""{"cid":{{JsonSerializer.Serialize(cid)}},"reply":{{response}}}""";
                await _connection.ListRightPushAsync(reply, answer, ReplyTtl).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                BNL.LogWarning($"[Redis] Failed to push reply for '{command}': {e.Message}");
            }
        }

        private string Execute(string command, JsonElement payload)
        {
            if (string.IsNullOrEmpty(command))
                return """{"ok":false,"error":"missing cmd"}""";
            if (!_handlers.TryGetValue(command, out var handler))
                return $$"""{"ok":false,"error":{{JsonSerializer.Serialize($"unknown command '{command}'")}}}""";
            return handler(payload);
        }

        // ':' is the conventional Redis key separator; a configured id
        // containing one would corrupt the key scheme.
        private static string SanitizeKeySegment(string segment) =>
            segment.Trim().Replace(':', '-').Replace(' ', '-');

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _permissionSync?.Dispose();
            _cts.Cancel();
            _connection.Connected -= HandleConnected;
            _connection.Dispose();
            _cts.Dispose();
            _drainSignal.Dispose();
        }
    }
}
#endif
