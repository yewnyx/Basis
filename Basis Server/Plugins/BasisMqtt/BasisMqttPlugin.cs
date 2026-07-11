#if !UNITY_2017_1_OR_NEWER
using Basis.Network.Core;
using System.IO;

namespace Basis.Network.Server.Mqtt
{
    /// <summary>
    /// Plugin entry point: reads the sidecar config and, when enabled, brings
    /// up the MQTT management API (<see cref="BasisMqttApiHandler"/>) and the
    /// lifecycle-event publisher (<see cref="BasisMqttEventPublisher"/>).
    /// </summary>
    public sealed class BasisMqttPlugin : IBasisServerPlugin
    {
        public string Id => "mqtt";

        private BasisMqttApiHandler _handler;
        private BasisMqttEventPublisher _events;

        public void Start(BasisServerPluginContext context)
        {
            string configPath = Path.Combine(context.PluginConfigDirectory, $"{Id}.xml");
            BasisMqttPluginConfig config = BasisMqttPluginConfig.LoadOrCreate(configPath);
            config.ApplyEnvironmentalOverrides();

            if (!config.MqttEnabled || string.IsNullOrEmpty(config.MqttBrokerHost))
            {
                BNL.Log($"[MQTT] Plugin installed but disabled (set MqttEnabled and MqttBrokerHost in {configPath}).");
                return;
            }

            _handler = new BasisMqttApiHandler(config);
            _events = new BasisMqttEventPublisher(_handler, config, context.Configuration.ServerName);
        }

        public void Dispose()
        {
            _events?.Dispose();
            _handler?.Dispose();
        }
    }
}
#endif
