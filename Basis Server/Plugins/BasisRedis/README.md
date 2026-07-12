# Basis Redis Management API (plugin) — EXPERIMENT

**Status: experiment branch, not for upstream.** A second transport
binding of the management protocol, built to answer one question: *what
does transport #2 actually cost, with and without a shared abstraction?*
The MQTT plugin remains the reference binding. This branch may be mined
for learnings and deleted.

## What it is

The same management surface as `Plugins/BasisMqtt` — commands over
`IServerControl`, lifecycle events, status, and (optionally) the full
permission/ban/allowlist sync — bound to Redis primitives instead of
MQTT topics, following the Redis sketch in the management-protocol
spec:

| Protocol concept | Redis primitive |
|---|---|
| Command intake | list `{prefix}:{serverId}:cmd` — manager `RPUSH`es a JSON envelope, then `PUBLISH`es `{prefix}:{serverId}:cmd:wake`; the server LPOPs FIFO (wake + 1s fallback poll ⇒ at-least-once) |
| Command envelope | `{"cmd":"worlds/load","payload":{…},"cid":"…","reply":"<list>"}` |
| Correlated reply | `{"cid":"…","reply":{…}}` RPUSHed to the envelope's `reply` list, 60s TTL; no `reply` = fire-and-forget |
| Events | stream `{prefix}:{serverId}:evt` (`XADD`, fields `type` + `json`, approximate MAXLEN trim) — durable and replayable, unlike MQTT QoS 1 |
| Status slot | string `{prefix}:{serverId}:status` with a TTL dead-man's switch (no last-will in Redis: unclean death ⇒ key expires ⇒ absence reads offline); clean shutdown persists `{"online":false}` |

Payload validation, command names, reply shapes and the perm-sync
rev/reconcile semantics are identical to the MQTT binding — that is the
point of the protocol contract.

## Configuration

Sidecar `config/plugins/redis.xml` (self-creates on first boot), fields
double as env-var overrides: `RedisEnabled`, `RedisHost`, `RedisPort`,
`RedisUseTls`, `RedisUsername`/`RedisPassword`, `RedisDatabase`,
`RedisKeyPrefix`, `RedisServerId`, `RedisStatusIntervalSeconds`,
`RedisStatusTtlSeconds` (0 ⇒ 3× interval, min 90s),
`RedisEventStreamMaxLength`, `RedisPermissionSyncEnabled`.

```sh
dotnet publish "Plugins/BasisRedis" -c Release -o <server>/plugins/redis
```

## Findings (the reason this branch exists)

1. **The seams held.** Zero core changes. `IServerControl`,
   `BasisServerEvents` (incl. `player/rejected`) and
   `IBasisModerationControl` bound to a completely different transport
   untouched — layers 1–3 of the plan are doing their job.
2. **The `RegisterCommandHandler(string, Func<JsonElement,string>)`
   surface is the real contract.** `BasisRedisApiRoutes` and
   `BasisRedisPermissionSync` are near-verbatim copies of the MQTT
   sources — a namespace, a handler type, and three publish call-sites
   changed. That's the measured cost of "no shared bus abstraction":
   ~470 duplicated lines that will drift. If a second transport ever
   ships for real, extract exactly this pair (register + publish) and
   nothing more; the connection management should stay per-transport.
3. **Transport-native beats lowest-common-denominator.** Redis wanted
   different idioms in every slot (queue list vs subscription, TTL vs
   last-will, durable stream vs fire-and-forget topic) — an
   `IManagementBus` that hid those would have fought all three. The
   spec's concept-level contract (command/event/status/snapshot) was
   the right altitude.
4. **StackExchange.Redis deleted the reconnect loop.** The multiplexer
   (AbortOnConnectFail=false) retries internally; the plugin only
   forwards ConnectionRestored into the snapshot/status republish hook.
   ~40 lines of MQTT connection choreography gone.

## Verified

- 10 tests green against an in-memory `IRedisConnection` fake: the
  envelope/reply cycle (cid, unknown/missing cmd, fire-and-forget,
  reply TTL), route-validation transplant, stream events incl.
  `player/rejected`, status TTL + clean-shutdown offline, perm/ban
  end-to-end with rev + changed event, reconnect snapshot.
- **Not smoke-tested against a live Redis server**, and no manager
  speaks this binding yet (Minos's `IFleetTransport` would need a
  `RedisFleetService` counterpart). Both are deliberate — the branch
  answers a design question, not a deployment need.
