using Basis.Network.Core;
using Basis.Network.Server.Auth;
using Basis.Network.Server.Generic;
using BasisNetworkCore;
using BasisNetworkServer.Security;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static BasisServerTests.ModelCacheWire;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Model traffic through the real <see cref="BasisNetworkingGeneric.HandleScene"/>: routed into the
/// model cache only, charged to the model governor's own bucket - never the image governor's, never
/// the scene backstop - and gated by the props rules, which refuse new shares only, and only
/// through the relay.
/// </summary>
[Collection("BasisServer shared network statics")]
public class BasisSceneRelayModelTrafficTests : IDisposable
{
    private const ushort ImageNetId = 4242;
    private const ushort ModelNetId = 4343;
    private const int ChunkBytes = 16 * 1024;
    private const int TailBytes = 96;

    private static int peerIdCounter = 27_000;

    private readonly Configuration _previousConfiguration;
    private readonly IAuthIdentity? _previousIdentity;
    private readonly MapAuthIdentity _identity = new();
    private readonly List<FakeNetPeer> _peers = new();
    private readonly List<(string Uuid, string Node)> _grants = new();
    private readonly bool _propsWereLocked;
    private readonly bool _gifsWereLocked;

    public BasisSceneRelayModelTrafficTests()
    {
        _previousConfiguration = NetworkServer.Configuration;
        _previousIdentity = NetworkServer.AuthIdentity;
        NetworkServer.Configuration = new Configuration
        {
            ImageCacheEnabled = true,
            ImageCacheMaxMegabytes = 4,
            ImageCacheMinimumPerOwnerMegabytes = 0,
            ModelCacheEnabled = true,
            ModelCacheMaxMegabytes = 4,
            ModelCacheMinimumPerOwnerMegabytes = 0,
            ImageShareDownloadMegabitsPerSecond = 0,
            ImageShareEgressMegabitsPerSecond = 0,
        };
        NetworkServer.AuthIdentity = _identity;
        // The shipped default group grants basis.resource.load.prop, so a plain user may share.
        PermissionIntegration.Manager.EnsureDefaults();

        _propsWereLocked = BasisGlobalLockManager.PropsLocked;
        if (_propsWereLocked)
        {
            BasisGlobalLockManager.ToggleProps();
        }
        _gifsWereLocked = BasisGlobalLockManager.GifsLocked;
        if (_gifsWereLocked)
        {
            BasisGlobalLockManager.ToggleGifs();
        }

        ResetAll();
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkImageCache.ImageManagerIdentifier] = ImageNetId;
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkModelCache.ModelManagerIdentifier] = ModelNetId;
    }

    public void Dispose()
    {
        if (BasisGlobalLockManager.PropsLocked != _propsWereLocked)
        {
            BasisGlobalLockManager.ToggleProps();
        }
        if (BasisGlobalLockManager.GifsLocked != _gifsWereLocked)
        {
            BasisGlobalLockManager.ToggleGifs();
        }
        foreach ((string uuid, string node) in _grants)
        {
            PermissionIntegration.Manager.RemoveUserNode(uuid, node);
        }
        foreach (FakeNetPeer peer in _peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(new KeyValuePair<int, NetPeer>(peer.Id, peer));
            BasisNetworkingGeneric.RemovePeerSceneEgress(peer.Id);
        }
        NetworkServer.RebuildPeerSnapshot();
        ResetAll();
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkImageCache.ImageManagerIdentifier, out _);
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkModelCache.ModelManagerIdentifier, out _);
        NetworkServer.Configuration = _previousConfiguration;
        NetworkServer.AuthIdentity = _previousIdentity;
    }

    private static void ResetAll()
    {
        // Image state too: some of these relay image traffic to show models leave it alone.
        BasisNetworkImageCache.Reset();
        BasisImageBandwidthGovernor.Reset();
        BasisNetworkModelCache.Reset();
        BasisModelBandwidthGovernor.Reset();
        BasisModelShareGate.Reset();
    }

    private (FakeNetPeer Peer, string Uuid) NewPeer()
    {
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.8.8.8") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"model-relay-{Guid.NewGuid():N}";
        _identity.Register(uuid, id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        _peers.Add(peer);
        return (peer, uuid);
    }

    private void Grant(string uuid, string node)
    {
        PermissionIntegration.Manager.AddUserNode(uuid, node);
        _grants.Add((uuid, node));
    }

    private static void Lock()
    {
        if (!BasisGlobalLockManager.PropsLocked)
        {
            Assert.True(BasisGlobalLockManager.ToggleProps());
        }
    }

    private static void Unlock()
    {
        if (BasisGlobalLockManager.PropsLocked)
        {
            Assert.False(BasisGlobalLockManager.ToggleProps());
        }
    }

    private static void Relay(FakeNetPeer sender, byte[] payload, ushort[]? recipients = null, ushort messageIndex = ModelNetId)
    {
        BasisNetworkingGeneric.HandleScene(
            ScenePacket(messageIndex, payload, recipients),
            DeliveryMethod.ReliableOrdered,
            sender,
            BasisNetworkCommons.DirectSceneServerChannel);
    }

    /// <summary>What a peer was relayed on the direct scene channel, envelope included.</summary>
    private static List<byte[]> Relayed(FakeNetPeer peer) =>
        peer.Sent.Where(sent => sent.Channel == BasisNetworkCommons.DirectSceneServerChannel).Select(sent => sent.Data).ToList();

    private static int AdminMessages(FakeNetPeer peer) => peer.Sent.Count(sent => sent.Channel == BasisNetworkCommons.AdminChannel);

    private static byte[] ModelSpawn(Guid id, FakeNetPeer owner, int totalChunks = 2, int chunkBytes = ChunkBytes) =>
        EncodeSpawn(id, (ushort)owner.Id, "Sharer", 1, TailBytes, totalChunks * chunkBytes, totalChunks, 0f, 0f, 0f, 0f, 0f, 0f, 1f, new byte[TailBytes]);

    private static Guid ShareModel(FakeNetPeer owner, int chunks = 2, ushort[]? recipients = null)
    {
        Guid id = Guid.NewGuid();
        Relay(owner, ModelSpawn(id, owner, chunks), recipients);
        for (int index = 0; index < chunks; index++)
        {
            Relay(owner, EncodeChunk(id, index, ChunkBytes), recipients);
        }
        return id;
    }

    // ---- routing and budgets ------------------------------------------------------------------

    [Fact]
    public void ModelTraffic_IsRelayedAndObservedIntoTheModelCacheOnly()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();

        ShareModel(sharer);

        List<byte[]> relayed = Relayed(viewer);
        Assert.Equal(3, relayed.Count);
        Assert.All(relayed, data =>
        {
            Assert.Equal(ModelNetId, MessageIndexOf(data));
            Assert.Equal((ushort)sharer.Id, PlayerIdOf(data));
        });
        Assert.Equal(1, BasisNetworkModelCache.ServableCount);
        Assert.Equal(0, BasisNetworkImageCache.Count);
    }

    [Fact]
    public void ModelTraffic_IsChargedToTheModelGovernor()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        ushort[] target = { (ushort)viewer.Id };

        Guid id = Guid.NewGuid();
        Relay(sharer, ModelSpawn(id, sharer, 200), target);
        for (int index = 0; index < 200; index++)
        {
            Relay(sharer, EncodeChunk(id, index, ChunkBytes), target);
        }

        Assert.True(Relayed(viewer).Count < 201, "a sender far past its budget must be cut off");
        Assert.True(BasisModelBandwidthGovernor.DroppedMessages > 0);
        Assert.Equal(0, BasisImageBandwidthGovernor.DroppedMessages);
    }

    [Fact]
    public void ImagesAndModels_DrawOnSeparateBuckets()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        ushort[] target = { (ushort)viewer.Id };

        // One image chunk charged at a fan-out of fifty overdraws the image bucket by seconds' worth,
        // so the next image charge is refused however slowly this test runs.
        ushort[] wide = Enumerable.Repeat((ushort)viewer.Id, 50).ToArray();
        Relay(sharer, EncodeChunk(Guid.NewGuid(), 0, 64 * 1024), wide, ImageNetId);
        Relay(sharer, EncodeChunk(Guid.NewGuid(), 0, 1024), target, ImageNetId);
        Assert.Single(Relayed(viewer));

        // The same sender's model traffic is metered by the model governor, which it has not spent.
        Relay(sharer, EncodeChunk(Guid.NewGuid(), 0, 1024), target);
        Assert.Equal(2, Relayed(viewer).Count);
        Assert.Equal(ModelNetId, MessageIndexOf(Relayed(viewer)[1]));
        Assert.Equal(0, BasisModelBandwidthGovernor.DroppedMessages);
    }

    [Fact]
    public void ModelTraffic_BypassesTheSceneRelayBackstop()
    {
        NetworkServer.Configuration.MaxSceneRelayMegabitsPerSecondPerPlayer = 1;
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        ushort[] target = { (ushort)viewer.Id };

        Guid id = Guid.NewGuid();
        for (int index = 0; index < 200; index++)
        {
            Relay(sharer, EncodeChunk(id, index, ChunkBytes), target);
        }

        Assert.Equal(200, Relayed(viewer).Count);
    }

    [Fact]
    public void GifLock_DoesNotDropModelTrafficUsingOpcodesSixOrSeven()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Assert.True(BasisGlobalLockManager.ToggleGifs());

        Guid id = Guid.NewGuid();
        byte[] six = EncodeOpGuid(OpAnimationSpawn, id);
        byte[] seven = EncodeChunk(id, 0, 64, OpAnimationChunk);
        Relay(sharer, six);
        Relay(sharer, seven);

        Assert.Equal(2, Relayed(viewer).Count);
    }

    [Fact]
    public void ModelHelloOpcodeIsRelayedButNeverCachedOrGated()
    {
        // The capability hello rides the model id: three bytes, possibly more from a newer client.
        // It is nobody's new content, so even a refused sharer under the lock gets it through.
        (FakeNetPeer sharer, string uuid) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Grant(uuid, "-" + PermNodes.ResourceLoadProp);
        Lock();

        Relay(sharer, new byte[] { OpHello, 1, 0 });
        byte[] longer = new byte[24];
        longer[0] = OpHello;
        longer[1] = 1;
        Relay(sharer, longer);

        Assert.Equal(2, Relayed(viewer).Count);
        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.TotalBytes);
        Assert.Equal(0, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));
        Assert.Equal(0, AdminMessages(sharer));
    }

    // ---- the props rule ---------------------------------------------------------------------

    [Fact]
    public void EvaluateSpawnForUuid_MirrorsThePropLoadRule()
    {
        string plain = $"model-rule-{Guid.NewGuid():N}";
        string bypass = $"model-rule-{Guid.NewGuid():N}";
        string denied = $"model-rule-{Guid.NewGuid():N}";
        string bypassDenied = $"model-rule-{Guid.NewGuid():N}";
        Grant(plain, PermNodes.ResourceLoadProp);
        Grant(bypass, PermNodes.ResourceLoadProp);
        Grant(bypass, PermNodes.ResourceLockBypassProp);
        Grant(denied, "-" + PermNodes.ResourceLoadProp);
        Grant(bypassDenied, PermNodes.ResourceLockBypassProp);
        Grant(bypassDenied, "-" + PermNodes.ResourceLoadProp);

        Assert.Equal(BasisModelSpawnVerdict.NoIdentity, BasisModelShareGate.EvaluateSpawnForUuid(null!));
        Assert.Equal(BasisModelSpawnVerdict.NoIdentity, BasisModelShareGate.EvaluateSpawnForUuid(string.Empty));
        Assert.Equal(BasisModelSpawnVerdict.Allowed, BasisModelShareGate.EvaluateSpawnForUuid(plain));
        Assert.Equal(BasisModelSpawnVerdict.NotPermitted, BasisModelShareGate.EvaluateSpawnForUuid(denied));

        Lock();
        Assert.Equal(BasisModelSpawnVerdict.PropsLocked, BasisModelShareGate.EvaluateSpawnForUuid(plain));
        Assert.Equal(BasisModelSpawnVerdict.Allowed, BasisModelShareGate.EvaluateSpawnForUuid(bypass));
        Assert.Equal(BasisModelSpawnVerdict.NotPermitted, BasisModelShareGate.EvaluateSpawnForUuid(bypassDenied));
    }

    [Fact]
    public void WhilePropsAreLocked_ANewModelFromAPlainUserIsNeitherRelayedNorCached()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Lock();

        Relay(sharer, ModelSpawn(Guid.NewGuid(), sharer));

        Assert.Empty(Relayed(viewer));
        Assert.Equal(0, BasisNetworkModelCache.Count);
    }

    [Fact]
    public void WhilePropsAreLocked_ChunksOfARefusedModelAreNotRelayed()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Lock();

        Guid id = ShareModel(sharer);

        Assert.True(BasisModelShareGate.IsDeniedForTests(id));
        Assert.Empty(Relayed(viewer));
        Assert.Equal(0, BasisNetworkModelCache.Count);
    }

    [Fact]
    public void WhilePropsAreLocked_TheSharerIsToldAtMostOncePerInterval()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        NewPeer();
        Lock();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Relay(sharer, ModelSpawn(Guid.NewGuid(), sharer));
        }

        Assert.Equal(1, AdminMessages(sharer));
    }

    [Fact]
    public void WhilePropsAreLocked_ABypassHolderStillShares()
    {
        (FakeNetPeer sharer, string uuid) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Grant(uuid, PermNodes.ResourceLockBypassProp);
        Lock();

        ShareModel(sharer);

        Assert.Equal(3, Relayed(viewer).Count);
        Assert.Equal(1, BasisNetworkModelCache.ServableCount);
        Assert.Equal(0, AdminMessages(sharer));
    }

    [Fact]
    public void WhilePropsAreLocked_AModelSharedBeforeTheLock_CanStillBeResent()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer early, _) = NewPeer();
        Guid id = ShareModel(sharer, recipients: new[] { (ushort)early.Id });
        Lock();

        (FakeNetPeer arrival, _) = NewPeer();
        ushort[] target = { (ushort)arrival.Id };
        Relay(sharer, ModelSpawn(id, sharer), target);
        Relay(sharer, EncodeChunk(id, 0, ChunkBytes), target);
        Relay(sharer, EncodeChunk(id, 1, ChunkBytes), target);

        Assert.Equal(3, Relayed(arrival).Count);
        Assert.False(BasisModelShareGate.IsDeniedForTests(id));
        Assert.Equal(0, AdminMessages(sharer));
    }

    /// <summary>
    /// The hole a GUID-and-owner match alone left open: register header-only ids while unlocked,
    /// then pour new content into them once the lock is on. Only a model whose every chunk the
    /// server saw, under the same totals, counts as shared before the lock.
    /// </summary>
    [Fact]
    public void WhilePropsAreLocked_AHeaderOnlyPreRegistrationCannotBeReusedForNewContent()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Guid headerOnly = Guid.NewGuid();
        Relay(sharer, ModelSpawn(headerOnly, sharer, totalChunks: 4));
        Guid partial = Guid.NewGuid();
        Relay(sharer, ModelSpawn(partial, sharer, totalChunks: 2));
        Relay(sharer, EncodeChunk(partial, 0, ChunkBytes));
        Guid complete = ShareModel(sharer, chunks: 2);
        int relayedBeforeLock = Relayed(viewer).Count;
        Lock();

        // Same totals, never finished: re-checked, refused, and its chunks with it.
        Relay(sharer, ModelSpawn(headerOnly, sharer, totalChunks: 4));
        Relay(sharer, EncodeChunk(headerOnly, 0, ChunkBytes));
        Relay(sharer, ModelSpawn(partial, sharer, totalChunks: 2));
        Relay(sharer, EncodeChunk(partial, 1, ChunkBytes));
        // Finished, but re-sent with different totals: new content under an old id.
        Relay(sharer, ModelSpawn(complete, sharer, totalChunks: 3));

        Assert.Equal(relayedBeforeLock, Relayed(viewer).Count);
        Assert.True(BasisModelShareGate.IsDeniedForTests(headerOnly));
        Assert.True(BasisModelShareGate.IsDeniedForTests(partial));
        Assert.True(BasisModelShareGate.IsDeniedForTests(complete));
    }

    [Fact]
    public void WhilePropsAreLocked_TransformsClaimsDespawnsAndRequestsStillRelay()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer mover, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Guid id = ShareModel(sharer);
        Lock();

        int before = Relayed(viewer).Count;
        Relay(mover, EncodeTransform(id, 1f, 2f, 3f));
        Relay(mover, EncodeOpGuid(OpClaim, id));
        Relay(mover, EncodeOpGuid(OpDespawn, id));
        Assert.Equal(before + 3, Relayed(viewer).Count);

        Relay(mover, EncodeOpGuid(OpServerCacheRequest, id), new[] { (ushort)mover.Id });
        Assert.Contains(Relayed(mover), data => PayloadOpcode(data) == OpServerCacheRequest);
        Assert.Equal(0, AdminMessages(mover));
    }

    [Fact]
    public void WithoutPropLoadPermission_ANewModelIsRefusedEvenUnlocked()
    {
        (FakeNetPeer sharer, string uuid) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Grant(uuid, "-" + PermNodes.ResourceLoadProp);

        Guid id = ShareModel(sharer);

        Assert.Empty(Relayed(viewer));
        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.True(BasisModelShareGate.IsDeniedForTests(id));
        // The prop path only logs a missing permission; it messages for the lock alone.
        Assert.Equal(0, AdminMessages(sharer));
    }

    [Fact]
    public void AfterTheLockLifts_ARefusedModelCanBeSharedAgain()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Lock();
        Guid id = Guid.NewGuid();
        Relay(sharer, ModelSpawn(id, sharer));
        Assert.True(BasisModelShareGate.IsDeniedForTests(id));

        Unlock();
        Relay(sharer, ModelSpawn(id, sharer));
        Relay(sharer, EncodeChunk(id, 0, ChunkBytes));
        Relay(sharer, EncodeChunk(id, 1, ChunkBytes));

        Assert.Equal(3, Relayed(viewer).Count);
        Assert.False(BasisModelShareGate.IsDeniedForTests(id));
        Assert.Equal(1, BasisNetworkModelCache.ServableCount);
    }

    [Fact]
    public void PropsLock_LeavesImageTrafficAlone()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Lock();

        Guid id = Guid.NewGuid();
        Relay(sharer, EncodeSpawn(id, (ushort)sharer.Id, "Sharer", 64, 64, 2 * ChunkBytes, 2, 0f, 0f, 0f, 0f, 0f, 0f, 1f), messageIndex: ImageNetId);
        Relay(sharer, EncodeChunk(id, 0, ChunkBytes), messageIndex: ImageNetId);
        Relay(sharer, EncodeChunk(id, 1, ChunkBytes), messageIndex: ImageNetId);

        Assert.Equal(3, Relayed(viewer).Count);
        Assert.Equal(1, BasisNetworkImageCache.ServableCount);
        Assert.Equal(0, AdminMessages(sharer));
    }

    // ---- gate bookkeeping -------------------------------------------------------------------

    [Fact]
    public void ADisconnectingSharer_ForgetsTheirGateEntries()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        NewPeer();
        ShareModel(sharer);
        Assert.Equal(1, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));

        BasisModelShareGate.RemovePeer(sharer.Id);

        Assert.Equal(0, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));
    }

    [Fact]
    public void TheGate_TracksAtMost64TransfersPerSharer()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        NewPeer();

        for (int index = 0; index < BasisModelShareGate.MaxTrackedTransfersPerOwner + 6; index++)
        {
            Relay(sharer, ModelSpawn(Guid.NewGuid(), sharer));
        }

        Assert.Equal(BasisModelShareGate.MaxTrackedTransfersPerOwner, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));
    }

    [Fact]
    public void ADespawnFromANonOwner_LeavesTheGateEntry()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer other, _) = NewPeer();
        Guid id = ShareModel(sharer);

        Relay(other, EncodeOpGuid(OpDespawn, id));
        Assert.Equal(1, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));
        Assert.Equal(1, BasisNetworkModelCache.Count);

        Relay(sharer, EncodeOpGuid(OpDespawn, id));
        Assert.Equal(0, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));
        Assert.Equal(0, BasisNetworkModelCache.Count);
    }

    [Fact]
    public void ASecondSender_CannotRewriteAnotherSharersGateEntry()
    {
        (FakeNetPeer sharer, _) = NewPeer();
        (FakeNetPeer squatter, string squatterUuid) = NewPeer();
        (FakeNetPeer viewer, _) = NewPeer();
        Guid id = ShareModel(sharer);
        Grant(squatterUuid, "-" + PermNodes.ResourceLoadProp);

        Relay(squatter, ModelSpawn(id, squatter));

        Assert.False(BasisModelShareGate.IsDeniedForTests(id));
        Assert.Equal(1, BasisModelShareGate.TrackedCountForTests((ushort)sharer.Id));
        Assert.Equal(0, BasisModelShareGate.TrackedCountForTests((ushort)squatter.Id));
        Assert.Equal(3, Relayed(viewer).Count);
    }

    [Fact]
    public void EvaluateSpawn_FallsBackToTheStoredJoinUuidWithoutAuthIdentity()
    {
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.8.8.9");
        string uuid = $"model-saved-{Guid.NewGuid():N}";
        Grant(uuid, PermNodes.ResourceLoadProp);
        Assert.Equal(BasisModelSpawnVerdict.NoIdentity, BasisModelShareGate.EvaluateSpawn(peer));

        BasisSavedState.AddLastData(peer, new ReadyMessage
        {
            playerMetaDataMessage = new ClientMetaDataMessage { playerUUID = uuid },
        });
        try
        {
            Assert.Equal(BasisModelSpawnVerdict.Allowed, BasisModelShareGate.EvaluateSpawn(peer));
        }
        finally
        {
            BasisSavedState.RemovePlayer(id);
        }
    }
}
