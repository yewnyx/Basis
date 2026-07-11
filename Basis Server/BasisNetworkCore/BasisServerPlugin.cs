using System;

namespace Basis.Network.Core
{
    /// <summary>
    /// An optional extension of the standalone server, loaded at boot from
    /// "plugins/{name}/" next to the server binary. Plugins reference the
    /// server assemblies for the seams they build on (IServerControl,
    /// BasisServerEvents, …) and carry their own third-party dependencies in
    /// their folder, so the stock server ships none of them. The Unity-embedded
    /// server does not load plugins.
    /// </summary>
    public interface IBasisServerPlugin : IDisposable
    {
        /// <summary>Short stable identifier, also the sidecar config file name ("{Id}.xml").</summary>
        string Id { get; }

        /// <summary>
        /// Called once after the network server has started. Throwing here
        /// disables this plugin (with a logged error); it must not prevent the
        /// server or other plugins from running.
        /// </summary>
        void Start(BasisServerPluginContext context);
    }

    /// <summary>What the host hands every plugin at startup.</summary>
    public sealed class BasisServerPluginContext
    {
        /// <summary>The server's core configuration (read-only by convention — plugin settings belong in the plugin's own sidecar file).</summary>
        public Configuration Configuration;

        /// <summary>Absolute directory for plugin sidecar configs; a plugin's file is "{Id}.xml" in here.</summary>
        public string PluginConfigDirectory;
    }
}
