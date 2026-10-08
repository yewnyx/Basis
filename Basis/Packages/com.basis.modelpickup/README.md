# Basis Model Pickup

Drag a `.glb`, or a self-contained `.gltf`, onto the window to spawn
a networked, grabbable 3D model. Dropping works in the Windows player
and the editor Scene view. Every platform can receive models.

The package is standalone: it can be added or removed without
touching the image pickup or the framework. It carries its own
transport (wire codecs, chunked transfer, uplink budget and link
probe, inbound memory pool, server-cache client, transform sync,
dialogs, back panel), and it works over the standard relay.

## What happens to a model

1. **Read.** The file is read with a hard size cap (64 MiB) checked
   before anything is allocated.
2. **Validate and rebuild.** The validator parses the glTF and
   rebuilds it into one canonical GLB. Animations, morph targets,
   cameras, lights, extensions it does not render, names and other
   metadata are dropped. What was dropped is logged.
3. **Textures.** Each embedded PNG or JPEG is checked from its header
   (at most 4096 px a side, 4096² pixels and 32 MiB), decoded, and
   re-encoded as an 8-bit RGBA PNG of at most 2048 px a side. GIF and
   anything else is refused.
4. **Load.** glTFast loads the canonical GLB, one model at a time on
   the main thread, inside a per-frame time slice. It gets no network
   or file access, and the result is checked against the validated
   stats.
5. **Share.** The canonical GLB goes to players in range who
   announced that they can load models.

Receivers do steps 2 and 4 again themselves, against their own
device's limits, before glTFast sees a byte. They accept only a GLB
with PNG images and no external URIs. The spawn header carries the
sender's claims (counts, bounds, memory), so a receiver can refuse a
model before downloading it. A model whose real contents exceed its
claims is dropped.

A placeholder box appears as soon as a drop or a header arrives. It
already has the model's size, and it can be grabbed, moved and
deleted while the model loads.

## Size

One question per drop: **Fit** scales each model so its largest side
is 0.5 m. **Original size** keeps the authored size, clamped to
between 2 cm and 4 m. Drops made while the question is open join it.
Closing it, ignoring it for 120 s, or dropping while some other
choice prompt is open all mean Fit. Models preview at Fit and are
not shared until the answer is in, because the chosen scale travels
in the spawn header. Grabbing still scales from 10 % to 1000 %.

## Network

- Network id `BasisModelPickupManager`, 16 KiB chunks. The message
  layouts below the model's spawn tail are the image pickup's, so a
  server cache that walks one walks the other.
- **Uplink.** Chunks are metered by the model's own budget, alongside
  the image pickup's: half of the spare uplink its link probe
  measures, and the relay egress the server advertises
  (`ImageShareEgressMegabitsPerSecond`).
- **Hello (opcode 11).** On joining, a client that can load models
  says so, and says it again to each player who joins later. Owners
  send model data only to peers that said hello, so a client
  without this package never collects chunks it cannot handle.
  Headless clients do not say hello. They only follow moves and
  deletes.
- **Late joiners.** Over the standard relay, the owner sends each
  model to whoever comes into range. A server that provides a model
  cache keeps each shared model in RAM, never on disk, while its
  owner is connected, and offers it to late joiners itself. Each
  client asks for the offers within range that its budgets allow,
  counting requests still on their way. Offers from a sharer who
  leaves are dropped.
- **Deleting.** Anyone may delete any model. A server cache forgets a
  model only when its owner says so, so an owner told by someone
  else repeats the delete. So does an owner whose model is
  destroyed by something else. A deleted model's id is refused for
  two minutes, so a copy the server was still sending cannot bring
  it back.

## Moderation

Shared models count as props. While an administrator has props
locked, the client refuses your drops (unless you hold the prop lock
bypass), and a model still loading when the lock lands is dropped.
Sharing also needs the server's prop-load permission. A server with
the model props gate enforces the same rules at the relay. Receivers
do not apply the lock themselves, so a bypass holder's model is
visible to everyone.

A modified client sharing directly over P2P never reaches the
server's check.

## Limits

Per model, as validated. Mid (8 GB or less, or unknown memory) uses
the Desktop column; Mobile is a mobile GPU or 4 GB or less.

| | Desktop | Mobile |
|---|---|---|
| Canonical GLB | 32 MiB | 16 MiB |
| Vertices / triangles | 500 000 | 150 000 |
| Draw calls | 1024 | 256 |
| Estimated decoded memory | 256 MiB | 96 MiB |
| Texture side | 2048 px | 2048 px |

All models on one client together:

| | Desktop | Mid | Mobile |
|---|---|---|---|
| Models | 64 | 48 | 24 |
| Memory (decoded + kept GLB) | 2 GiB | 1 GiB | 384 MiB |
| Inbound transfer memory | 256 MiB | 192 MiB | 96 MiB |

Each player may share 8 models at once. Models still loading count.
Per-sender, render and timing limits are in
`Core/BasisModelTierLimits.cs`. Mobile receivers also discard a
model's GLB once it is loaded (so they cannot Save it) and space one
sender's imports 5 s apart.

## Saving, receiving and the Library

- **Save** on the back panel writes the canonical GLB, never the
  dropped file, as `Model_<id>.glb` to `Documents\Basis\Models` on
  Windows, or `Basis/Models` under persistent data elsewhere.
- **Receiving** can be switched off with the PlayerPrefs key
  `Basis.ModelPickup.ReceiveEnabled` (on by default). Your own drops
  still work.
- **Library.** Each model is listed in the Library's Instantiated tab
  as a shared prop, with a remove button.

## Layout and tests

| Folder | Contents |
|---|---|
| `Core/`, `Validation/` | Engine-free: model wire, admission, budgets, sizing, the transport core (`Core/Transport/`), the glTF validator. |
| `Transport/` | Engine and framework glue: network identity, uplink, outbound queue, cache client, dialogs, back panel, gizmos, the follow pass (one Burst job over every pickup root). |
| `Editor/` | Build step: puts TextMeshPro's "Distance Field" shader, which the back panel's labels look up by name, in Always Included Shaders. |
| `Localization/` | Language tables for 16 languages, loaded through the Basis Localization Addressables group. |
| `Tests/Editor/EngineFree/`, `Tests/Editor/Validation/` | NUnit tests that run under Unity and under `dotnet test`. |

Engine-free files use only `System.*` and compile as netstandard2.1
with C# 9. From this folder:

```
dotnet build "Tests~/DotNet/Core/Basis.ModelPickup.Core.csproj"
dotnet test  "Tests~/DotNet/Tests/Basis.ModelPickup.Tests.csproj"
```

In Unity, run the `Basis.ModelPickup.Tests` assembly in the Test
Runner (EditMode). `BasisModelShareWireCompatibilityTests` pins the
wire against a `BinaryWriter` oracle; if it fails, the protocol
changed.

## Not built yet

- ⏸ glTF animation playback: parked; the canonicaliser strips
  animations.
- ⏸ Morph targets: parked; stripped.
- not built yet: JPEG or KTX2 textures on the wire (PNG only).
- not built yet: Draco and meshopt.
- not built yet: highlighting skinned meshes (they use the box).
- not built yet: a settings toggle for receiving, and backing it up
  with the user's other settings.
- not built yet: a Library kind of its own (models list as props).
- not built yet: one uplink budget shared with the image pickup.
- not built yet: dropping models on Quest.
- not built yet: a defence against a peer squatting another model's
  id.
- not built yet: telling the dropper when a model is over the mobile
  limits (logged only).

## Known issues

- The image pickup and the model pickup meter separately, each taking
  half of what its own probe measures, so sharing images and models
  at once can take all of the spare uplink until the probes back off.
- The relay budget is the image pickup's advertised figure; there is
  no model-specific one.
- In rooms where every peer is P2P-connected, a server cache keeps
  the pose it saw last, so late joiners judge range against, and
  place the model at, a stale spot.
- Turning receiving off and on again does not bring back models
  refused meanwhile. Owners keep sending model data to a peer that
  turned receiving off.
- After a server cache evicts a model, the owner re-sends it to
  peers that already got it from a replay. They ignore the copy.
- When a server's props gate refuses a model, the model pickup is
  not told: the model stays in the sharer's world while the relay
  drops it. Under the props lock the server also sends the sharer an
  admin message (at most once per 10 s); a missing prop-load
  permission is only logged on the server.
- An image pickup notice replaces whatever dialogue is up, so one
  raised while the size question is open closes it, and the batch
  is sized Fit.

## License

[MIT](LICENSE) © BasisVR
