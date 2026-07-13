# Basis MQTT Management API (plugin)

An MQTT binding for the same management surface the REST API exposes
(`IServerControl`), plus lifecycle events the poll-based REST API cannot
offer. The server acts as an MQTT *client*: it connects out to a broker you
operate, so many servers behind NAT can be managed from one place.

This is an optional **server plugin** (see `Plugins/README.md`): the stock
server ships no MQTT code or dependencies. Install it by publishing this
project into the server's `plugins/` folder —

```sh
dotnet publish "Plugins/BasisMqtt" -c Release -o <server>/plugins/mqtt
```

— then enable it in the sidecar config below. The bundled Docker image
already carries the plugin folder; it stays inert until enabled.

## Broker requirements

- **MQTT version 5 is required.** Command replies use the MQTT 5
  request/response pattern (Response Topic + Correlation Data); an MQTT 3.1.1
  broker will silently strip these properties. Mosquitto ≥ 2.0, EMQX, HiveMQ
  and NanoMQ all qualify.
- Retained messages must be permitted for the status document.
- The connection registers a **last will** that rewrites the retained status
  to `{"online":false}` if the server drops without a clean shutdown.

## Configuration

Set in the plugin's sidecar file `config/plugins/mqtt.xml` — written with
defaults on first boot with the plugin installed — or override any field with
an environment variable of the same name (core `config.xml` is not involved):

| Field | Default | Meaning |
|---|---|---|
| `MqttEnabled` | `false` | Master switch. |
| `MqttBrokerHost` | `""` | Broker hostname/IP. Empty disables the API. |
| `MqttBrokerPort` | `8883` | Broker port. |
| `MqttUseTls` | `true` | Disable only for loopback or otherwise-trusted networks. |
| `MqttUsername` / `MqttPassword` | `""` | Broker credentials. |
| `MqttClientId` | `""` | Empty derives `basis-{MqttServerId}`. |
| `MqttTopicPrefix` | `basis` | First topic segment. |
| `MqttServerId` | `""` | Second topic segment. Empty derives from the machine name. |
| `MqttQoS` | `1` | QoS for command subscriptions and event publishes. |
| `MqttStatusIntervalSeconds` | `30` | Retained-status refresh period; `0` disables the timer. |

## Topic scheme

Every topic lives under `{MqttTopicPrefix}/{MqttServerId}` ("`{base}`").

### Commands — `{base}/cmd/…`

Publish a JSON payload (may be empty for parameterless commands). To get an
answer, set a **Response Topic**; the reply lands there with your
**Correlation Data** echoed. Without a response topic the command executes
fire-and-forget and logs its outcome. Payloads over 1 MiB are rejected.

| Topic | Payload | Reply |
|---|---|---|
| `cmd/announce` | `{"message":"…"}` (≤ 512 chars) | `{"ok":true}` |
| `cmd/announce-player` | `{"uuid":"…","message":"…"}` | `{"ok":true}` |
| `cmd/players/list` | — | `{"ok":true,"players":[{"netId","uuid","displayName","platform","position"}]}` |
| `cmd/status` | — | `{"ok":true,"online":true,"players":N,"worlds":N}` |
| `cmd/worlds/list` | — | `{"ok":true,"worlds":[{"netId","url","persistent","adminLocked","strategy"}]}` |
| `cmd/worlds/load` | `{"url":"https://…","password":"…","persistent":false,"strategy":"immediate"\|"synchronized"\|"predownload"}` | `{"ok":true,"netId":"…"}` |
| `cmd/worlds/unload` | `{"netId":"…"}` | `{"ok":true}` |
| `cmd/worlds/clear` | — | `{"ok":true,"unloaded":N}` |
| `cmd/worlds/switch` | `{"url":"…","password":"…","persistent":false,"announceMessage":"…","delay":0–300}` | `{"ok":true,"netId":"…"}` |

Field names, validation rules and limits match the REST API; world URLs must
be `https://` and the password may ride the URL fragment
(`…/world.bee#base64pw`). Errors reply `{"ok":false,"error":"…"}`.

### Events — `{base}/evt/…`

| Topic | Payload |
|---|---|
| `evt/player/joined` | `{"netId":N,"uuid":"…","displayName":"…"}` |
| `evt/player/left` | `{"netId":N,"uuid":"…"}` |
| `evt/world/loaded` | `{"netId":"…","url":"…","persistent":bool,"strategy":N}` |
| `evt/world/unloaded` | `{"netId":"…"}` |
| `evt/player/rejected` | `{"uuid":"…"\|null,"reason":"…"}` |
| `evt/status` | **retained** `{"online":true,"serverName":"…","players":N,"worlds":N}` |

`evt/status` is refreshed on every (re)connect and on the configured
interval; graceful shutdown rewrites it to `{"online":false}` and the
last will covers crashes. Other events are moments in time: if the broker
is unreachable they are dropped, not replayed late.

`evt/player/rejected` carries the UUID when the refusal happened at a gate
that knows who knocked (allowlist, banlist, rejoin lock, auth); refusals
before an identity exists (banned IP, malformed payload, version mismatch)
carry `null`.

## Broker ACL guidance

The MQTT API has no application-level authentication — treating the broker
as the security boundary is the design. Lock it down there:

- Give each server its own credentials, allowed to **publish and subscribe
  only under `{prefix}/{serverId}/#`** (plus publish to the response topics
  your tooling uses, if those live elsewhere).
- Give management tooling publish rights on `{prefix}/+/cmd/#` and subscribe
  rights on `{prefix}/+/evt/#` and its own response-topic subtree.
- Anyone who can publish to a server's `cmd/` subtree can load worlds and
  message players — do not run the production topic tree on a broker with
  anonymous write access.
- Keep `MqttUseTls` on for anything that leaves the machine.

## Trying it out

With Mosquitto:

```sh
# watch everything the server does
mosquitto_sub -V 5 -t 'basis/my-server/#' -v

# load a world and read the reply
mosquitto_sub -V 5 -t 'replies/cli' -v &
mosquitto_pub -V 5 -t 'basis/my-server/cmd/worlds/load' \
  -D publish response-topic 'replies/cli' \
  -m '{"url":"https://example.com/world.bee","password":"pw"}'
```
