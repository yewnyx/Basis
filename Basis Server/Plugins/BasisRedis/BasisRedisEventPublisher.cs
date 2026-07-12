#if !UNITY_2017_1_OR_NEWER
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// Publishes server lifecycle events to the Redis stream and maintains the
    /// status key. Subscribers to <see cref="BasisServerEvents"/> run on
    /// LiteNetLib's network threads, so raises only enqueue onto a bounded
    /// channel; a single pump task does the actual writes.
    ///
    /// Status is the spec's "last-known slot": a string at "{base}:status",
    /// refreshed on connect and on an interval, written with a TTL as a
    /// dead-man's switch — if the server dies uncleanly the key expires and
    /// its absence reads as offline (Redis has no last-will). Clean shutdown
    /// overwrites it with a persistent {"online":false}.
    /// </summary>
    public sealed class BasisRedisEventPublisher : IDisposable
    {
        /// <summary>Events queued while Redis is unreachable beyond this count drop oldest-first.</summary>
        public const int QueueCapacity = 1024;

        private readonly BasisRedisApiHandler _handler;
        private readonly IServerControl _control;
        private readonly Channel<(string Type, string Json)> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _statusIntervalSeconds;
        private readonly TimeSpan _statusTtl;
        private readonly string _serverName;
        private int _disposed;

        public BasisRedisEventPublisher(BasisRedisApiHandler handler, BasisRedisPluginConfig config, string serverName, IServerControl control = null)
        {
            _handler = handler;
            _control = control ?? new BasisServerControl();
            _statusIntervalSeconds = config.RedisStatusIntervalSeconds;
            _statusTtl = TimeSpan.FromSeconds(config.RedisStatusTtlSeconds > 0
                ? config.RedisStatusTtlSeconds
                : Math.Max(3 * Math.Max(_statusIntervalSeconds, 1), 90));
            _serverName = serverName;
            _queue = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

            BasisServerEvents.OnPlayerJoined += HandlePlayerJoined;
            BasisServerEvents.OnPlayerLeft += HandlePlayerLeft;
            BasisServerEvents.OnWorldLoaded += HandleWorldLoaded;
            BasisServerEvents.OnWorldUnloaded += HandleWorldUnloaded;
            BasisServerEvents.OnPlayerRejected += HandlePlayerRejected;
            _handler.Connected += HandleConnected;

            _ = Task.Run(() => PumpAsync(_cts.Token));
            if (_statusIntervalSeconds > 0)
                _ = Task.Run(() => StatusLoopAsync(_cts.Token));

            // The connection may have come up before we subscribed to
            // Connected; write status once now. If Redis isn't reachable yet
            // this drops and the Connected raise covers it.
            _ = WriteStatusAsync();
        }

        private void HandlePlayerJoined(int netId, string uuid, string displayName) =>
            Enqueue("player/joined",
                $$"""{"netId":{{netId}},"uuid":{{JsonSerializer.Serialize(uuid)}},"displayName":{{JsonSerializer.Serialize(displayName)}}}""");

        private void HandlePlayerLeft(int netId, string uuid) =>
            Enqueue("player/left",
                $$"""{"netId":{{netId}},"uuid":{{JsonSerializer.Serialize(uuid)}}}""");

        private void HandleWorldLoaded(string netId, string url, bool persistent, byte loadStrategy) =>
            Enqueue("world/loaded",
                $$"""{"netId":{{JsonSerializer.Serialize(netId)}},"url":{{JsonSerializer.Serialize(url)}},"persistent":{{(persistent ? "true" : "false")}},"strategy":{{loadStrategy}}}""");

        private void HandleWorldUnloaded(string netId) =>
            Enqueue("world/unloaded",
                $$"""{"netId":{{JsonSerializer.Serialize(netId)}}}""");

        private void HandlePlayerRejected(string uuid, string reason) =>
            Enqueue("player/rejected",
                $$"""{"uuid":{{JsonSerializer.Serialize(uuid)}},"reason":{{JsonSerializer.Serialize(reason)}}}""");

        private void HandleConnected() => _ = WriteStatusAsync();

        private void Enqueue(string type, string json) =>
            _queue.Writer.TryWrite((type, json));

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
                            await _handler.PublishEventAsync(item.Type, item.Json).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // Events describe moments; replaying them late is worse
                            // than dropping. The TTL'd status heals on the interval.
                            BNL.LogWarning($"[Redis] Dropped event '{item.Type}': {e.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task StatusLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_statusIntervalSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await WriteStatusAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task WriteStatusAsync()
        {
            try
            {
                await _handler.SetStatusAsync(BuildStatusJson(), _statusTtl).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                BNL.LogWarning($"[Redis] Failed to write status: {e.Message}");
            }
        }

        private string BuildStatusJson()
        {
            int players = 0, worlds = 0;
            try
            {
                players = _control.ListPlayers().Count;
                worlds = _control.ListWorlds().Count;
            }
            catch (Exception e)
            {
                BNL.LogWarning($"[Redis] Failed to gather status counts: {e.Message}");
            }
            return $$"""{"online":true,"serverName":{{JsonSerializer.Serialize(_serverName)}},"players":{{players}},"worlds":{{worlds}}}""";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            BasisServerEvents.OnPlayerJoined -= HandlePlayerJoined;
            BasisServerEvents.OnPlayerLeft -= HandlePlayerLeft;
            BasisServerEvents.OnWorldLoaded -= HandleWorldLoaded;
            BasisServerEvents.OnWorldUnloaded -= HandleWorldUnloaded;
            BasisServerEvents.OnPlayerRejected -= HandlePlayerRejected;
            _handler.Connected -= HandleConnected;
            _cts.Cancel();
            _queue.Writer.TryComplete();
            // Clean shutdown: persist the offline document (no TTL) so a late
            // manager reads a definite offline rather than a missing key.
            try { _handler.SetStatusAsync("""{"online":false}""", null).Wait(TimeSpan.FromSeconds(2)); }
            catch { }
            _cts.Dispose();
        }
    }
}
#endif
