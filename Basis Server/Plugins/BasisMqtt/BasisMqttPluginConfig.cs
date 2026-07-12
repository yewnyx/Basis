#if !UNITY_2017_1_OR_NEWER
using System;

namespace Basis.Network.Server.Mqtt
{
    /// <summary>
    /// The MQTT plugin's sidecar configuration, persisted at
    /// "config/plugins/mqtt.xml" via <see cref="Basis.Network.Core.BasisPluginConfigFile"/>.
    /// Field names double as environment-variable overrides
    /// (e.g. <c>MqttBrokerHost</c>), mirroring how the core configuration
    /// behaves in Docker deployments.
    /// </summary>
    [Serializable]
    public class BasisMqttPluginConfig
    {
        /// <summary>Master switch. Requires an MQTT v5 broker.</summary>
        public bool MqttEnabled = false;
        /// <summary>Hostname or IP of the MQTT broker. Empty string disables the API even if MqttEnabled is true.</summary>
        public string MqttBrokerHost = "";
        public ushort MqttBrokerPort = 8883;
        /// <summary>Connect to the broker over TLS. Disable only for loopback or otherwise-trusted networks.</summary>
        public bool MqttUseTls = true;
        public string MqttUsername = "";
        public string MqttPassword = "";
        /// <summary>MQTT client id presented to the broker. Empty derives "basis-{MqttServerId}".</summary>
        public string MqttClientId = "";
        /// <summary>First segment of every topic this server uses; lets one broker serve unrelated deployments.</summary>
        public string MqttTopicPrefix = "basis";
        /// <summary>Second topic segment identifying this server on the broker (topics are "{MqttTopicPrefix}/{MqttServerId}/…"). Empty derives from the machine name.</summary>
        public string MqttServerId = "";
        /// <summary>Quality-of-service level (0–2) used for command subscriptions and event publishes.</summary>
        public int MqttQoS = 1;
        /// <summary>Seconds between retained status publishes. 0 disables the periodic timer; the broker's last-will still reports the server offline.</summary>
        public int MqttStatusIntervalSeconds = 30;
    }
}
#endif
