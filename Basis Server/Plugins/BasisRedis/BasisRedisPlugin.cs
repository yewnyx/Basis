#if !UNITY_2017_1_OR_NEWER
using Basis.Network.Core;
using System.IO;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// Plugin entry point: reads the sidecar config and, when enabled, brings
    /// up the Redis management API (<see cref="BasisRedisApiHandler"/>) and the
    /// lifecycle-event publisher (<see cref="BasisRedisEventPublisher"/>).
    /// </summary>
    public sealed class BasisRedisPlugin : IBasisServerPlugin
    {
        public string Id => "redis";

        private BasisRedisApiHandler _handler;
        private BasisRedisEventPublisher _events;

        public void Start(BasisServerPluginContext context)
        {
            string configPath = Path.Combine(context.PluginConfigDirectory, $"{Id}.xml");
            BasisRedisPluginConfig config = BasisPluginConfigFile.LoadOrCreate<BasisRedisPluginConfig>(configPath);

            if (!config.RedisEnabled || string.IsNullOrEmpty(config.RedisHost))
            {
                BNL.Log($"[Redis] Plugin installed but disabled (set RedisEnabled and RedisHost in {configPath}).");
                return;
            }

            _handler = new BasisRedisApiHandler(config);
            _events = new BasisRedisEventPublisher(_handler, config, context.Configuration.ServerName);
        }

        public void Dispose()
        {
            _events?.Dispose();
            _handler?.Dispose();
        }
    }
}
#endif
