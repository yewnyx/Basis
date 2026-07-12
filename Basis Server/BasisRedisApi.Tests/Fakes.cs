using Basis.Network.Server.Redis;
using BasisNetworkServer.Security;
using BasisPermissions;

namespace Basis.Network.Server.RedisTests;

/// <summary>
/// In-memory <see cref="IRedisConnection"/>: dictionaries stand in for lists,
/// strings, streams and pub/sub channels. Implements only the seven
/// operations the binding uses, plus test helpers to inject commands and
/// await writes.
/// </summary>
public sealed class FakeRedisConnection : IRedisConnection
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<string>> _lists = new();
    private readonly Dictionary<string, List<Action<string>>> _subscriptions = new();
    private readonly Dictionary<string, (string Value, TimeSpan? Ttl)> _strings = new();
    private readonly Dictionary<string, List<(string Type, string Json)>> _streams = new();

    public readonly Dictionary<string, TimeSpan> ListExpiries = new();

    public event Action? Connected;

    public bool IsConnected => true;

    public void RaiseConnected() => Connected?.Invoke();

    // ── IRedisConnection ────────────────────────────────────────────────────

    public Task<string> ListLeftPopAsync(string key)
    {
        lock (_lock)
        {
            if (_lists.TryGetValue(key, out var list) && list.Count > 0)
            {
                string value = list[0];
                list.RemoveAt(0);
                return Task.FromResult(value);
            }
        }
        return Task.FromResult<string>(null!);
    }

    public Task ListRightPushAsync(string key, string value, TimeSpan expiry)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(key, out var list)) _lists[key] = list = new List<string>();
            list.Add(value);
            ListExpiries[key] = expiry;
        }
        return Task.CompletedTask;
    }

    public Task PublishAsync(string channel, string message)
    {
        Action<string>[] handlers;
        lock (_lock)
        {
            handlers = _subscriptions.TryGetValue(channel, out var list) ? list.ToArray() : [];
        }
        foreach (var handler in handlers) handler(message);
        return Task.CompletedTask;
    }

    public void Subscribe(string channel, Action<string> onMessage)
    {
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(channel, out var list)) _subscriptions[channel] = list = new List<Action<string>>();
            list.Add(onMessage);
        }
    }

    public Task StringSetAsync(string key, string value, TimeSpan? expiry)
    {
        lock (_lock) _strings[key] = (value, expiry);
        return Task.CompletedTask;
    }

    public Task StreamAddAsync(string key, string type, string json, int maxLength)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(key, out var stream)) _streams[key] = stream = new List<(string, string)>();
            stream.Add((type, json));
        }
        return Task.CompletedTask;
    }

    public void Dispose() { }

    // ── Test helpers ────────────────────────────────────────────────────────

    /// <summary>What a manager does: RPUSH the envelope, then PUBLISH the wake.</summary>
    public void PushCommand(string listKey, string wakeChannel, string envelope)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(listKey, out var list)) _lists[listKey] = list = new List<string>();
            list.Add(envelope);
        }
        PublishAsync(wakeChannel, "1").GetAwaiter().GetResult();
    }

    public string? TryTakeFromList(string key)
    {
        lock (_lock)
        {
            if (_lists.TryGetValue(key, out var list) && list.Count > 0)
            {
                string value = list[0];
                list.RemoveAt(0);
                return value;
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<(string Type, string Json)>> AwaitStreamAsync(
        string key, int count, Func<(string Type, string Json), bool>? filter = null, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue(key, out var stream))
                {
                    var matches = filter is null ? stream.ToList() : stream.Where(filter).ToList();
                    if (matches.Count >= count) return matches;
                }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Stream '{key}' never reached {count} matching entries.");
    }

    public async Task<(string Value, TimeSpan? Ttl)> AwaitStringAsync(
        string key, Func<string, bool>? predicate = null, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_strings.TryGetValue(key, out var entry) && (predicate is null || predicate(entry.Value)))
                    return entry;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"String '{key}' was never written{(predicate is null ? "" : " with a matching value")}.");
    }
}

/// <summary>Recording <see cref="IServerControl"/> stand-in.</summary>
public sealed class FakeControl : IServerControl
{
    private readonly object _lock = new();
    public readonly List<string> Announcements = new();
    public readonly TaskCompletionSource<string> AnnounceSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void AnnounceAll(string message)
    {
        lock (_lock) Announcements.Add(message);
        AnnounceSignal.TrySetResult(message);
    }

    public bool AnnouncePlayer(string uuid, string message) => false;
    public string LoadWorld(WorldLoadParams p) => "net-1";
    public bool UnloadWorld(string netId) => false;
    public int ClearAllWorlds() => 0;
    public IReadOnlyList<WorldInfo> ListWorlds() => Array.Empty<WorldInfo>();
    public IReadOnlyList<PlayerInfo> ListPlayers() => Array.Empty<PlayerInfo>();
    public string SwitchWorld(SwitchWorldParams p, CancellationToken cancellationToken = default) => "net-2";
}

/// <summary>
/// In-memory stand-in honouring the IBasisModerationControl contract:
/// mutators that change state raise the matching change event. Trimmed copy
/// of the MQTT test fake.
/// </summary>
public sealed class FakeModerationControl : IBasisModerationControl
{
    public event Action<string>? OnPermissionsChanged;
    public event Action? OnBansChanged;
    public event Action? OnAllowlistChanged;

    public readonly List<(string Op, string A, string? B)> Calls = new();
    public readonly PermissionStore Store = new();
    public readonly List<string> Allowlist = new();
    public readonly List<BasisPlayerModeration.BannedPlayer> Bans = new();

    private void Permission(string op, string a, string? b, string? changedUuid)
    {
        lock (Calls) Calls.Add((op, a, b));
        OnPermissionsChanged?.Invoke(changedUuid!);
    }

    public void AddUserNode(string uuid, string node) => Permission("user/add-node", uuid, node, uuid);
    public void RemoveUserNode(string uuid, string node) => Permission("user/remove-node", uuid, node, uuid);
    public void AddUserToGroup(string uuid, string group) => Permission("user/add-group", uuid, group, uuid);
    public void RemoveUserFromGroup(string uuid, string group) => Permission("user/remove-group", uuid, group, uuid);
    public void AddGroupNode(string group, string node) => Permission("group/add-node", group, node, null);
    public void RemoveGroupNode(string group, string node) => Permission("group/remove-node", group, node, null);
    public void AddGroupParent(string group, string parent) => Permission("group/add-parent", group, parent, null);
    public void RemoveGroupParent(string group, string parent) => Permission("group/remove-parent", group, parent, null);

    public void CreateGroup(string group)
    {
        lock (Calls) Calls.Add(("group/create", group, null));
    }

    public bool DeleteGroup(string group)
    {
        Permission("group/delete", group, null, null);
        return true;
    }

    public PermissionStore SnapshotPermissions() => Store;

    public ModerationResult Ban(string uuid, string reason)
    {
        lock (Calls) Calls.Add(("ban", uuid, reason));
        OnBansChanged?.Invoke();
        return new(true, $"Player {uuid} banned (offline).");
    }

    public ModerationResult IpBan(string uuid, string reason)
    {
        lock (Calls) Calls.Add(("ipban", uuid, reason));
        OnBansChanged?.Invoke();
        return new(true, $"Player {uuid} and IP 127.0.0.1 banned.");
    }

    public ModerationResult Kick(string uuid, string reason)
    {
        lock (Calls) Calls.Add(("kick", uuid, reason));
        return new(true, $"Player {uuid} kicked.");
    }

    public ModerationResult Unban(string uuid) => new(false, "UUID not banned");
    public ModerationResult UnbanIp(string ip) => new(false, "IP not banned");
    public IReadOnlyList<BasisPlayerModeration.BannedPlayer> ListBans() => Bans;

    public ModerationResult AddToAllowlist(string uuid)
    {
        Allowlist.Add(uuid);
        OnAllowlistChanged?.Invoke();
        return new(true, $"Added {uuid} to allowlist.");
    }

    public ModerationResult RemoveFromAllowlist(string uuid)
    {
        Allowlist.Remove(uuid);
        OnAllowlistChanged?.Invoke();
        return new(true, $"Removed {uuid} from allowlist.");
    }

    public ModerationResult SetAllowlist(IReadOnlyCollection<string> uuids)
    {
        Allowlist.Clear();
        Allowlist.AddRange(uuids);
        OnAllowlistChanged?.Invoke();
        return new(true, $"Allowlist replaced ({uuids.Count} entries).");
    }

    public IReadOnlyList<string> ListAllowlist() => Allowlist;

    public void Dispose() { }
}
