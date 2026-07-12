#if !UNITY_2017_1_OR_NEWER
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Basis.Network.Server.Mqtt
{
    /// <summary>
    /// Publishes server lifecycle events and a retained status document over
    /// MQTT. Subscribers to <see cref="BasisServerEvents"/> run on LiteNetLib's
    /// network threads, so raises only enqueue onto a bounded channel; a single
    /// pump task does the actual publishing. The status document is retained at
    /// "{base}/evt/status" — republished after every (re)connect (overwriting
    /// the connection's last-will), on a configurable interval, and rewritten
    /// to {"online":false} on graceful shutdown.
    /// </summary>
    public sealed class BasisMqttEventPublisher : IDisposable
    {
        /// <summary>Events queued while the broker is unreachable beyond this count drop oldest-first.</summary>
        public const int QueueCapacity = 1024;

        private readonly BasisMqttApiHandler _handler;
        private readonly IServerControl _control;
        private readonly Channel<(string Topic, string Json, bool Retain)> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _statusIntervalSeconds;
        private readonly string _serverName;
        private int _disposed;

        public BasisMqttEventPublisher(BasisMqttApiHandler handler, BasisMqttPluginConfig config, string serverName, IServerControl control = null)
        {
            _handler = handler;
            _control = control ?? new BasisServerControl();
            _statusIntervalSeconds = config.MqttStatusIntervalSeconds;
            _serverName = serverName;
            _queue = Channel.CreateBounded<(string, string, bool)>(new BoundedChannelOptions(QueueCapacity)
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

            // The handler may have connected before we subscribed to Connected;
            // publish once now. If the broker isn't reachable yet this drops and
            // the Connected raise covers it.
            EnqueueStatus();
        }

        private void HandlePlayerJoined(int netId, string uuid, string displayName) =>
            Enqueue("evt/player/joined",
                $$"""{"netId":{{netId}},"uuid":{{JsonSerializer.Serialize(uuid)}},"displayName":{{JsonSerializer.Serialize(displayName)}}}""");

        private void HandlePlayerLeft(int netId, string uuid) =>
            Enqueue("evt/player/left",
                $$"""{"netId":{{netId}},"uuid":{{JsonSerializer.Serialize(uuid)}}}""");

        private void HandleWorldLoaded(string netId, string url, bool persistent, byte loadStrategy) =>
            Enqueue("evt/world/loaded",
                $$"""{"netId":{{JsonSerializer.Serialize(netId)}},"url":{{JsonSerializer.Serialize(url)}},"persistent":{{(persistent ? "true" : "false")}},"strategy":{{loadStrategy}}}""");

        private void HandleWorldUnloaded(string netId) =>
            Enqueue("evt/world/unloaded",
                $$"""{"netId":{{JsonSerializer.Serialize(netId)}}}""");

        private void HandlePlayerRejected(string uuid, string reason) =>
            Enqueue("evt/player/rejected",
                $$"""{"uuid":{{JsonSerializer.Serialize(uuid)}},"reason":{{JsonSerializer.Serialize(reason)}}}""");

        private void HandleConnected() => EnqueueStatus();

        private void EnqueueStatus() => Enqueue("evt/status", BuildStatusJson(), retain: true);

        private void Enqueue(string topic, string json, bool retain = false) =>
            _queue.Writer.TryWrite((topic, json, retain));

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
                            await _handler.PublishAsync(item.Topic, item.Json, item.Retain).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // Events describe moments; replaying them late is worse than
                            // dropping. Retained status heals on reconnect and interval.
                            BNL.LogWarning($"[MQTT] Dropped event on '{item.Topic}': {e.Message}");
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
                    EnqueueStatus();
                }
            }
            catch (OperationCanceledException) { }
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
                BNL.LogWarning($"[MQTT] Failed to gather status counts: {e.Message}");
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
            // Graceful shutdown: overwrite the retained status so watchers don't
            // have to wait for the broker to fire the last-will.
            try { _handler.PublishAsync("evt/status", """{"online":false}""", retain: true).Wait(TimeSpan.FromSeconds(2)); }
            catch { }
            _cts.Dispose();
        }
    }
}
#endif
