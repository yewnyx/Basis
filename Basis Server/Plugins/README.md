# Server plugins

Optional extensions of the standalone server. The stock server ships none of
their code or dependencies; dropping a plugin folder next to the binary is
what enables one.

## How loading works

At boot (after the network server starts), the console host scans
`plugins/{name}/` folders beside the binary. Each folder is the published
output of a plugin project:

- The **entry assembly** is the one whose `*.deps.json` sits beside it —
  `dotnet publish` produces exactly one.
- Every exported concrete type implementing `IBasisServerPlugin` (in
  `BasisNetworkCore`) is instantiated and started with a
  `BasisServerPluginContext` (the core `Configuration` plus the sidecar-config
  directory).
- Each plugin loads in its own `AssemblyLoadContext`: third-party dependencies
  ship inside the plugin folder and stay private to it, while assemblies the
  host already loaded (the Basis server assemblies, the framework) resolve to
  the host's copies so plugin types unify with host types.
- A plugin that fails to load or start is logged and disabled; it cannot take
  the server or other plugins down. Plugins dispose in reverse start order on
  shutdown.

Plugin settings live in `config/plugins/{id}.xml`, following the per-transport
sidecar convention — core `config.xml` stays free of fields for components
that may not be installed. The Unity-embedded server does not load plugins.

## Writing a plugin

Reference `BasisNetworkCore` (for the interface) and whichever server
assemblies carry the seams you build on (`IServerControl`,
`BasisServerEvents`, …) with `Private="false"` so your publish output carries
only your own code and third-party dependencies, and set
`<EnableDynamicLoading>true</EnableDynamicLoading>` so publish emits the
`deps.json` the loader keys on. Install with:

```sh
dotnet publish "Plugins/YourPlugin" -c Release -o <server>/plugins/yourplugin
```

`Plugins/BasisMqtt` is the reference plugin.
