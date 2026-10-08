using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkCore;
using Xunit;
using static BasisServerTests.ModelCacheWire;

namespace BasisServerTests;

/// <summary>
/// The model pickup's own rate control: a per-sender bucket under relayed model uploads and a paced
/// per-receiver replay queue, both reading the image-share rates without touching the image
/// governor's state.
/// </summary>
// Swaps NetworkServer.Configuration and registers the model manager id, so it joins the collection
// that serialises every other test touching the process-wide network statics.
[Collection("BasisServer shared network statics")]
public class BasisModelBandwidthGovernorTests : IDisposable
{
    private const ushort ModelNetId = 4343;

    private readonly Configuration _previous;
    private readonly List<int> _registeredPeerIds = new();

    public BasisModelBandwidthGovernorTests()
    {
        _previous = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration
        {
            ModelCacheEnabled = true,
            ModelCacheMaxMegabytes = 4,
            ModelCacheMinimumPerOwnerMegabytes = 0,
        };
        BasisModelBandwidthGovernor.Reset();
        BasisNetworkModelCache.Reset();
        // Driven by hand: left on, the pump thread drains the queue underneath a rate assertion.
        BasisModelBandwidthGovernor.AutoPump = false;
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkModelCache.ModelManagerIdentifier] = ModelNetId;
    }

    public void Dispose()
    {
        BasisModelBandwidthGovernor.Reset();
        BasisNetworkModelCache.Reset();
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkModelCache.ModelManagerIdentifier, out _);
        foreach (int id in _registeredPeerIds)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(id, out _);
        }
        NetworkServer.Configuration = _previous;
    }

    private ImageCacheRecordingPeer RegisterPeer(int id)
    {
        ImageCacheRecordingPeer peer = new ImageCacheRecordingPeer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        _registeredPeerIds.Add(id);
        return peer;
    }

    /// <summary>Payloads whose first byte is their position, so order can be asserted.</summary>
    private static List<BasisModelBandwidthGovernor.PendingPayload> Payloads(int count, int size)
    {
        var list = new List<BasisModelBandwidthGovernor.PendingPayload>();
        for (int i = 0; i < count; i++)
        {
            byte[] payload = new byte[size];
            payload[0] = (byte)i;
            list.Add(new BasisModelBandwidthGovernor.PendingPayload(7, payload));
        }
        return list;
    }

    // ── Upload ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SenderInsideItsBudget_IsNeverDropped()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 200;

        for (int i = 0; i < 50; i++)
        {
            Assert.True(BasisModelBandwidthGovernor.TryConsumeEgress(1, 16 * 1024));
        }
        Assert.Equal(0, BasisModelBandwidthGovernor.DroppedMessages);
    }

    [Fact]
    public void SenderThatIgnoresTheBudget_IsCutOffWithoutSpendingTheImageBucket()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;
        long imageDroppedBefore = BasisImageBandwidthGovernor.DroppedMessages;

        bool dropped = false;
        for (int i = 0; i < 200 && !dropped; i++)
        {
            dropped = !BasisModelBandwidthGovernor.TryConsumeEgress(1, 64 * 1024);
        }

        Assert.True(dropped, "a sender well past its budget must be cut off");
        Assert.True(BasisModelBandwidthGovernor.DroppedBytes > 0);
        Assert.Equal(imageDroppedBefore, BasisImageBandwidthGovernor.DroppedMessages);
        // The budget is per sharer: one exhausting itself must not stop anybody else.
        Assert.True(BasisModelBandwidthGovernor.TryConsumeEgress(2, 64 * 1024));
    }

    [Fact]
    public void ZeroUploadBudget_DisablesEnforcement()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 0;

        for (int i = 0; i < 500; i++)
        {
            Assert.True(BasisModelBandwidthGovernor.TryConsumeEgress(1, 1024 * 1024));
        }
    }

    // ── Download ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ZeroDownloadRate_LeavesReplayToTheCaller()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 0;

        Assert.False(BasisModelBandwidthGovernor.EnqueueReplay(RegisterPeer(9), Payloads(4, 1024)));
    }

    [Fact]
    public void PacedReplay_GoesOutInOrderUnderTheModelId()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 200;
        ImageCacheRecordingPeer peer = RegisterPeer(9);

        Assert.True(BasisModelBandwidthGovernor.EnqueueReplay(peer, Payloads(4, 1024)));
        Assert.True(BasisModelBandwidthGovernor.EnqueueReplay(peer, Payloads(4, 1024)));
        Assert.Empty(peer.Sent);

        BasisModelBandwidthGovernor.PumpOnceForTests();

        Assert.Equal(new byte[] { 0, 1, 2, 3, 0, 1, 2, 3 }, peer.Sent.Select(sent => PayloadOf(sent.Data)[0]).ToArray());
        Assert.All(peer.Sent, sent =>
        {
            Assert.Equal(BasisNetworkCommons.DirectSceneServerChannel, sent.Channel);
            Assert.Equal(ModelNetId, MessageIndexOf(sent.Data));
            Assert.Equal((ushort)7, PlayerIdOf(sent.Data));
        });
        Assert.False(BasisModelBandwidthGovernor.HasPendingReplay(peer.Id));
    }

    [Fact]
    public void ALowRate_DoesNotDeliverTheWholeReplayInOnePass()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 1;
        ImageCacheRecordingPeer peer = RegisterPeer(9);

        Assert.True(BasisModelBandwidthGovernor.EnqueueReplay(peer, Payloads(400, 16 * 1024)));
        BasisModelBandwidthGovernor.PumpOnceForTests();

        Assert.NotEmpty(peer.Sent);
        Assert.True(peer.Sent.Count < 400, $"a 1 Mb/s replay must not deliver 6.5 MB in one pass; sent {peer.Sent.Count}");
        Assert.True(BasisModelBandwidthGovernor.HasPendingReplay(peer.Id));
    }

    [Fact]
    public void RemovePeer_DropsTheQueuedReplay()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 1;
        ImageCacheRecordingPeer peer = RegisterPeer(9);
        Assert.True(BasisModelBandwidthGovernor.EnqueueReplay(peer, Payloads(400, 16 * 1024)));

        BasisModelBandwidthGovernor.RemovePeer(peer.Id);

        Assert.False(BasisModelBandwidthGovernor.HasPendingReplay(peer.Id));
    }

    [Fact]
    public void ARequestedModel_IsReplayedThroughThePump()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 200;
        Guid id = Guid.NewGuid();
        byte[] spawn = EncodeSpawn(id, 7, "Sharer", 1, 96, 2048, 2, 0f, 0f, 0f, 0f, 0f, 0f, 1f, new byte[96]);
        BasisNetworkModelCache.Observe(7, spawn, spawn.Length);
        for (int index = 0; index < 2; index++)
        {
            byte[] chunk = EncodeChunk(id, index, 1024);
            BasisNetworkModelCache.Observe(7, chunk, chunk.Length);
        }

        // Registered after the share, so the untargeted spawn did not seed it as already holding it.
        ImageCacheRecordingPeer requester = RegisterPeer(9);
        BasisNetworkModelCache.ServeRequestedModel(9, id);
        Assert.Empty(requester.Sent);

        BasisModelBandwidthGovernor.PumpOnceForTests();

        Assert.Equal(new[] { OpSpawn, OpChunk, OpChunk }, requester.Sent.Select(sent => PayloadOpcode(sent.Data)).ToArray());
        Assert.All(requester.Sent, sent =>
        {
            Assert.Equal(ModelNetId, MessageIndexOf(sent.Data));
            Assert.Equal((ushort)7, PlayerIdOf(sent.Data));
        });
    }

    // ── The pump thread ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePumpThread_ExitsOnceItsQueueIsEmpty()
    {
        // 300 KB at 1 Mb/s: one 250 KB burst, then about half a second of paced sends, so the
        // thread is still there to be caught right after the enqueue.
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 1;
        BasisModelBandwidthGovernor.AutoPump = true;
        ImageCacheRecordingPeer peer = RegisterPeer(9);

        Assert.True(BasisModelBandwidthGovernor.EnqueueReplay(peer, Payloads(300, 1024)));
        Thread? pump = BasisModelBandwidthGovernor.PumpThreadForTests;
        Assert.NotNull(pump);

        Assert.True(pump!.Join(5000), "an idle pump must not keep a thread alive");
        Assert.Null(BasisModelBandwidthGovernor.PumpThreadForTests);
        Assert.Equal(300, peer.Sent.Count);
    }

    [Fact]
    public void Reset_RetiresTheRunningPumpThread()
    {
        // Reset runs on every server start and stop; a pump left running across a quick restart
        // would be a second thread draining the same queue.
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 1;
        BasisModelBandwidthGovernor.AutoPump = true;
        ImageCacheRecordingPeer peer = RegisterPeer(9);

        Assert.True(BasisModelBandwidthGovernor.EnqueueReplay(peer, Payloads(400, 16 * 1024)));
        Thread? pump = BasisModelBandwidthGovernor.PumpThreadForTests;
        Assert.NotNull(pump);

        BasisModelBandwidthGovernor.Reset();

        Assert.True(pump!.Join(2000), "the pump thread outlived the reset that retired it");
        Assert.Null(BasisModelBandwidthGovernor.PumpThreadForTests);
        Assert.False(BasisModelBandwidthGovernor.HasPendingReplay(peer.Id));
    }
}
