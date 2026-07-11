#if !UNITY_2017_1_OR_NEWER
using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace BasisNetworkConsole
{
    /// <summary>
    /// Loads server plugins from "plugins/{name}/" folders next to the binary.
    /// Each folder is a published plugin project: its entry assembly is the one
    /// with the matching ".deps.json" beside it, and it loads in its own
    /// <see cref="AssemblyLoadContext"/> so third-party dependencies (and their
    /// versions) stay private to the plugin. Assemblies the host already loaded
    /// — the Basis server assemblies and the framework — resolve to the host's
    /// copies so plugin types unify with host types.
    /// </summary>
    public static class BasisServerPluginLoader
    {
        public const string PluginsFolderName = "plugins";

        /// <summary>
        /// Discovers, instantiates and starts every plugin. A plugin that fails
        /// to load or start is logged and skipped; it never takes the server or
        /// other plugins down with it.
        /// </summary>
        public static List<IBasisServerPlugin> StartAll(Configuration config, string baseDirectory)
        {
            var started = new List<IBasisServerPlugin>();
            string pluginsRoot = Path.Combine(baseDirectory, PluginsFolderName);
            if (!Directory.Exists(pluginsRoot))
                return started;

            string pluginConfigDirectory = Path.Combine(baseDirectory, Configuration.ConfigFolderName, PluginsFolderName);
            Directory.CreateDirectory(pluginConfigDirectory);
            var context = new BasisServerPluginContext
            {
                Configuration = config,
                PluginConfigDirectory = pluginConfigDirectory,
            };

            foreach (string directory in Directory.EnumerateDirectories(pluginsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string entryAssemblyPath = FindEntryAssembly(directory);
                if (entryAssemblyPath == null)
                {
                    BNL.LogWarning($"[Plugins] Skipping '{Path.GetFileName(directory)}': no entry assembly found (expected exactly one *.deps.json from 'dotnet publish').");
                    continue;
                }

                foreach (var plugin in InstantiatePlugins(entryAssemblyPath))
                {
                    try
                    {
                        plugin.Start(context);
                        started.Add(plugin);
                        BNL.Log($"[Plugins] Started '{plugin.Id}' from {Path.GetFileName(directory)}/{Path.GetFileName(entryAssemblyPath)}");
                    }
                    catch (Exception e)
                    {
                        BNL.LogError($"[Plugins] Plugin '{plugin.Id}' failed to start and was disabled: {e}");
                        try { plugin.Dispose(); } catch { }
                    }
                }
            }
            return started;
        }

        /// <summary>Disposes started plugins in reverse start order, tolerating failures.</summary>
        public static void DisposeAll(List<IBasisServerPlugin> plugins)
        {
            if (plugins == null) return;
            for (int index = plugins.Count - 1; index >= 0; index--)
            {
                try { plugins[index].Dispose(); }
                catch (Exception e) { BNL.LogError($"[Plugins] Plugin '{plugins[index].Id}' failed to dispose: {e}"); }
            }
        }

        private static string FindEntryAssembly(string directory)
        {
            string[] depsFiles = Directory.GetFiles(directory, "*.deps.json");
            if (depsFiles.Length != 1)
                return null;
            string assemblyPath = Path.ChangeExtension(depsFiles[0], null); // strips ".json"
            assemblyPath = Path.ChangeExtension(assemblyPath, ".dll");      // ".deps" -> ".dll"
            return File.Exists(assemblyPath) ? assemblyPath : null;
        }

        private static IEnumerable<IBasisServerPlugin> InstantiatePlugins(string entryAssemblyPath)
        {
            var plugins = new List<IBasisServerPlugin>();
            try
            {
                var loadContext = new BasisPluginLoadContext(entryAssemblyPath);
                Assembly assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
                foreach (Type type in assembly.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(IBasisServerPlugin).IsAssignableFrom(type))
                        continue;
                    plugins.Add((IBasisServerPlugin)Activator.CreateInstance(type));
                }
                if (plugins.Count == 0)
                    BNL.LogWarning($"[Plugins] {Path.GetFileName(entryAssemblyPath)} exports no IBasisServerPlugin implementation.");
            }
            catch (Exception e)
            {
                BNL.LogError($"[Plugins] Failed to load {entryAssemblyPath}: {e}");
            }
            return plugins;
        }

        private sealed class BasisPluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public BasisPluginLoadContext(string entryAssemblyPath)
                : base(name: Path.GetFileNameWithoutExtension(entryAssemblyPath))
            {
                _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                // Anything the host already has must come from the host, or the
                // plugin's IBasisServerPlugin would be a different type than ours.
                foreach (Assembly loaded in Default.Assemblies)
                {
                    if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }

                string path = _resolver.ResolveAssemblyToPath(assemblyName);
                return path != null ? LoadFromAssemblyPath(path) : null; // null falls back to the default context
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                string path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
            }
        }
    }
}
#endif
