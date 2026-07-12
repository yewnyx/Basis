#if !UNITY_2017_1_OR_NEWER
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// <see cref="IRedisConnection"/> over StackExchange.Redis. Unlike the MQTT
    /// binding there is no hand-rolled reconnect loop: the multiplexer is
    /// created with AbortOnConnectFail=false and retries internally, surfacing
    /// ConnectionRestored — we only translate that into the Connected event
    /// the snapshot/status re-publish logic keys on.
    /// </summary>
    public sealed class RedisConnection : IRedisConnection
    {
        private readonly ConnectionMultiplexer _multiplexer;
        private readonly IDatabase _db;
        private readonly ISubscriber _subscriber;

        public event Action Connected;

        public RedisConnection(BasisRedisPluginConfig config)
        {
            var options = new ConfigurationOptions
            {
                AbortOnConnectFail = false,
                Ssl = config.RedisUseTls,
                DefaultDatabase = config.RedisDatabase,
            };
            options.EndPoints.Add(config.RedisHost, config.RedisPort);
            if (!string.IsNullOrEmpty(config.RedisUsername)) options.User = config.RedisUsername;
            if (!string.IsNullOrEmpty(config.RedisPassword)) options.Password = config.RedisPassword;

            _multiplexer = ConnectionMultiplexer.Connect(options);
            _db = _multiplexer.GetDatabase();
            _subscriber = _multiplexer.GetSubscriber();

            _multiplexer.ConnectionRestored += (_, _) => RaiseConnected();
            if (_multiplexer.IsConnected) RaiseConnected();
        }

        private void RaiseConnected()
        {
            try { Connected?.Invoke(); }
            catch (Exception e) { BNL.LogError($"[Redis] Connected subscriber threw: {e}"); }
        }

        public bool IsConnected => _multiplexer.IsConnected;

        public async Task<string> ListLeftPopAsync(string key)
        {
            RedisValue value = await _db.ListLeftPopAsync(key).ConfigureAwait(false);
            return value.IsNull ? null : value.ToString();
        }

        public async Task ListRightPushAsync(string key, string value, TimeSpan expiry)
        {
            await _db.ListRightPushAsync(key, value).ConfigureAwait(false);
            await _db.KeyExpireAsync(key, expiry).ConfigureAwait(false);
        }

        public Task PublishAsync(string channel, string message) =>
            _subscriber.PublishAsync(RedisChannel.Literal(channel), message);

        public void Subscribe(string channel, Action<string> onMessage) =>
            _subscriber.Subscribe(RedisChannel.Literal(channel), (_, message) => onMessage(message.ToString()));

        public Task StringSetAsync(string key, string value, TimeSpan? expiry) =>
            _db.StringSetAsync(key, value, expiry);

        public Task StreamAddAsync(string key, string type, string json, int maxLength) =>
            _db.StreamAddAsync(key,
                [new NameValueEntry("type", type), new NameValueEntry("json", json)],
                maxLength: maxLength, useApproximateMaxLength: true);

        public void Dispose() => _multiplexer.Dispose();
    }
}
#endif
