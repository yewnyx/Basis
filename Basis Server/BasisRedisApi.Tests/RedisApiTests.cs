using Basis.Network.Server.Redis;
using BasisNetworkServer.Security;
using System.Text.Json;
using Xunit;

namespace Basis.Network.Server.RedisTests;

/// <summary>
/// Exercises the Redis binding against an in-memory <see cref="IRedisConnection"/>
/// fake: the command envelope/reply cycle, route wiring, stream events, the
/// TTL'd status slot, and the permission-sync copy. No live Redis — the fake
/// implements exactly the seven operations the binding uses.
/// </summary>
public class RedisApiTests : IDisposable
{
    private readonly FakeRedisConnection _connection = new();
    private readonly FakeControl _control = new();
    private readonly BasisRedisApiHandler _handler;

    private const string ReplyKey = "minos:replies:test";

    public RedisApiTests()
    {
        _handler = new BasisRedisApiHandler(BuildConfig(), _connection, _control);
    }

    public void Dispose() => _handler.Dispose();

    private static BasisRedisPluginConfig BuildConfig() => new()
    {
        RedisEnabled = true,
        RedisHost = "unused",
        RedisKeyPrefix = "basis",
        RedisServerId = "test",
        RedisStatusIntervalSeconds = 0,
    };

    private void Push(string envelope) =>
        _connection.PushCommand(_handler.CommandListKey, _handler.CommandWakeChannel, envelope);

    private async Task<JsonDocument> AwaitReplyAsync(int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            string? reply = _connection.TryTakeFromList(ReplyKey);
            if (reply is not null) return JsonDocument.Parse(reply);
            await Task.Delay(25);
        }
        throw new TimeoutException("No reply arrived.");
    }

    // ── Envelope / reply cycle ──────────────────────────────────────────────

    [Fact]
    public async Task StatusCommand_RepliesWithCorrelationId()
    {
        Push($$"""{"cmd":"status","payload":{},"cid":"c-1","reply":"{{ReplyKey}}"}""");

        using var reply = await AwaitReplyAsync();
        Assert.Equal("c-1", reply.RootElement.GetProperty("cid").GetString());
        var body = reply.RootElement.GetProperty("reply");
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task UnknownCommand_RepliesError()
    {
        Push($$"""{"cmd":"no/such/thing","cid":"c-2","reply":"{{ReplyKey}}"}""");

        using var reply = await AwaitReplyAsync();
        var body = reply.RootElement.GetProperty("reply");
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown command", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingCmd_RepliesError()
    {
        Push($$"""{"payload":{},"cid":"c-3","reply":"{{ReplyKey}}"}""");

        using var reply = await AwaitReplyAsync();
        Assert.False(reply.RootElement.GetProperty("reply").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task FireAndForget_ExecutesWithoutReply()
    {
        Push("""{"cmd":"announce","payload":{"message":"doors in 5"}}""");

        string announced = await _control.AnnounceSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("doors in 5", announced);
        Assert.Null(_connection.TryTakeFromList(ReplyKey));
    }

    [Fact]
    public async Task ReplyList_GetsAnExpiry()
    {
        Push($$"""{"cmd":"status","cid":"c-4","reply":"{{ReplyKey}}"}""");

        using var _ = await AwaitReplyAsync();
        Assert.Equal(BasisRedisApiHandler.ReplyTtl, _connection.ListExpiries[ReplyKey]);
    }

    [Fact]
    public async Task RouteValidation_MirrorsMqttDialect()
    {
        // The routes are a copy of the MQTT plugin's; spot-check one
        // validation rule survived the transplant.
        Push($$"""{"cmd":"worlds/load","payload":{"url":"http://insecure.example/w.bee","password":"p"},"cid":"c-5","reply":"{{ReplyKey}}"}""");

        using var reply = await AwaitReplyAsync();
        var body = reply.RootElement.GetProperty("reply");
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("https://", body.GetProperty("error").GetString());
    }

    // ── Events and status ───────────────────────────────────────────────────

    [Fact]
    public async Task PlayerJoinedAndRejected_AppendToEventStream()
    {
        using var publisher = new BasisRedisEventPublisher(_handler, BuildConfig(), "Basis Server", _control);

        BasisServerEvents.RaisePlayerJoined(7, "uuid-7", "Bob");
        BasisServerEvents.RaisePlayerRejected("uuid-9", "You are not on the allowlist.");

        var entries = await _connection.AwaitStreamAsync(_handler.EventStreamKey, count: 2);
        Assert.Equal("player/joined", entries[0].Type);
        Assert.Equal("player/rejected", entries[1].Type);
        using var rejected = JsonDocument.Parse(entries[1].Json);
        Assert.Equal("uuid-9", rejected.RootElement.GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task Status_WrittenWithTtl_OfflinePersistedOnDispose()
    {
        var publisher = new BasisRedisEventPublisher(_handler, BuildConfig(), "Basis Server", _control);

        var (json, ttl) = await _connection.AwaitStringAsync(_handler.StatusKey);
        Assert.NotNull(ttl); // dead-man's switch: unclean death expires the key
        using (var doc = JsonDocument.Parse(json))
            Assert.True(doc.RootElement.GetProperty("online").GetBoolean());

        publisher.Dispose();

        (json, ttl) = await _connection.AwaitStringAsync(_handler.StatusKey, s => s.Contains("false"));
        Assert.Null(ttl); // clean shutdown persists a definite offline
        using (var doc = JsonDocument.Parse(json))
            Assert.False(doc.RootElement.GetProperty("online").GetBoolean());
    }

    // ── Permission sync (copy) ──────────────────────────────────────────────

    [Fact]
    public async Task PermBan_MutatesStore_AcksWithRev_AppendsChangedEvent()
    {
        var moderation = new FakeModerationControl();
        using var sync = new BasisRedisPermissionSync(_handler, moderation);

        Push($$"""{"cmd":"perm/ban","payload":{"uuid":"did:key:scalper","reason":"resale bot"},"cid":"c-6","reply":"{{ReplyKey}}"}""");

        using var reply = await AwaitReplyAsync();
        var body = reply.RootElement.GetProperty("reply");
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(1, body.GetProperty("rev").GetInt64());
        Assert.Contains(("ban", "did:key:scalper", "resale bot"), moderation.Calls);

        var entries = await _connection.AwaitStreamAsync(_handler.EventStreamKey,
            count: 1, filter: e => e.Type == "perm/changed");
        using var changed = JsonDocument.Parse(entries[0].Json);
        Assert.Equal("bans", changed.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, changed.RootElement.GetProperty("rev").GetInt64());
    }

    [Fact]
    public async Task Reconnect_AppendsFreshSnapshot()
    {
        var moderation = new FakeModerationControl();
        moderation.Allowlist.Add("did:key:holder");
        using var sync = new BasisRedisPermissionSync(_handler, moderation);

        _connection.RaiseConnected();

        var entries = await _connection.AwaitStreamAsync(_handler.EventStreamKey,
            count: 1, filter: e => e.Type == "perm/snapshot");
        using var snapshot = JsonDocument.Parse(entries[0].Json);
        Assert.Equal(1, snapshot.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("did:key:holder", snapshot.RootElement.GetProperty("allowlist")[0].GetString());
    }
}
