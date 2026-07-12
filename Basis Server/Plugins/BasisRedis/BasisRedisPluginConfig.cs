#if !UNITY_2017_1_OR_NEWER
using System;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// The Redis plugin's sidecar configuration, persisted at
    /// "config/plugins/redis.xml" via <see cref="Basis.Network.Core.BasisPluginConfigFile"/>.
    /// Field names double as environment-variable overrides
    /// (e.g. <c>RedisHost</c>), mirroring the MQTT plugin's sidecar.
    /// </summary>
    [Serializable]
    public class BasisRedisPluginConfig
    {
        /// <summary>Master switch. Requires a reachable Redis 5+ (streams) server.</summary>
        public bool RedisEnabled = false;
        /// <summary>Hostname or IP of the Redis server. Empty string disables the API even if RedisEnabled is true.</summary>
        public string RedisHost = "";
        public ushort RedisPort = 6379;
        /// <summary>Connect over TLS. Plain Redis deployments typically run inside a trusted network; enable for rediss:// style hosts.</summary>
        public bool RedisUseTls = false;
        public string RedisUsername = "";
        public string RedisPassword = "";
        /// <summary>Logical database index.</summary>
        public int RedisDatabase = 0;
        /// <summary>First segment of every key/channel this server uses; lets one Redis serve unrelated deployments.</summary>
        public string RedisKeyPrefix = "basis";
        /// <summary>Second key segment identifying this server (keys are "{RedisKeyPrefix}:{RedisServerId}:…"). Empty derives from the machine name.</summary>
        public string RedisServerId = "";
        /// <summary>Seconds between status-key refreshes. 0 disables the periodic timer.</summary>
        public int RedisStatusIntervalSeconds = 30;
        /// <summary>
        /// TTL on the status key — the dead-man's switch: if the server dies
        /// uncleanly the key expires and its absence reads as offline.
        /// 0 derives 3× the status interval (minimum 90s).
        /// </summary>
        public int RedisStatusTtlSeconds = 0;
        /// <summary>Approximate MAXLEN trim on the event stream.</summary>
        public int RedisEventStreamMaxLength = 10000;
        /// <summary>Expose permission, ban and allowlist management (perm/… commands and events).</summary>
        public bool RedisPermissionSyncEnabled = false;
    }
}
#endif
