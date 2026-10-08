using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkCore;
using Xunit;
using static BasisServerTests.ModelCacheWire;

namespace BasisServerTests;

/// <summary>
/// The server model cache, driven with the bytes a model client puts on the wire: a claims tail the
/// server must carry verbatim, a cap on header size and on models per sharer, owner-only despawn, a
/// pose limit, and a manager id that has to follow the id database through its resets.
/// </summary>
// Mutates NetworkServer.Configuration, AuthenticatedPeers and BasisNetworkIDDatabase, all
// process-wide statics, so it shares the collection that serialises every other test touching them.
[Collection("BasisServer shared network statics")]
public class BasisNetworkModelCacheTests : IDisposable
{
    private const ushort ModelNetId = 4343;
    private const ushort ImageNetId = 4242;
    private const string OtherIdentifier = "net:model-cache-other";

    /// <summary>Chunk payload sized so a megabyte-granularity budget still exercises eviction.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>The model client's v1 tail: size mode, reserved, base scale and 88 bytes of claims.</summary>
    private const int TailBytes = 96;

    private readonly Configuration _previous;
    private readonly List<int> _registeredPeerIds = new();

    public BasisNetworkModelCacheTests()
    {
        _previous = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration
        {
            ModelCacheEnabled = true,
            ModelCacheMaxMegabytes = 4,
            ModelCacheMinimumPerOwnerMegabytes = 0,
            // Replay inline; the paced path has its own tests.
            ImageShareDownloadMegabitsPerSecond = 0,
            ImageShareEgressMegabitsPerSecond = 0,
        };
        ResetAll();
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkModelCache.ModelManagerIdentifier] = ModelNetId;
    }

    public void Dispose()
    {
        ResetAll();
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkModelCache.ModelManagerIdentifier, out _);
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(OtherIdentifier, out _);
        NetworkServer.Configuration = _previous;
        foreach (int id in _registeredPeerIds)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(id, out _);
        }
    }

    private static void ResetAll()
    {
        BasisNetworkModelCache.Reset();
        BasisModelBandwidthGovernor.Reset();
        BasisModelShareGate.Reset();
    }

    private ImageCacheRecordingPeer RegisterPeer(int id)
    {
        ImageCacheRecordingPeer peer = new ImageCacheRecordingPeer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        _registeredPeerIds.Add(id);
        return peer;
    }

    private static byte[] Tail(byte seed = 3)
    {
        byte[] tail = new byte[TailBytes];
        for (int index = 0; index < tail.Length; index++)
        {
            tail[index] = (byte)(index * 7 + seed);
        }
        return tail;
    }

    private static byte[] ModelSpawn(
        Guid id,
        ushort owner,
        int totalChunks,
        int chunkBytes = ChunkBytes,
        byte[]? tail = null,
        float px = 0f,
        float py = 0f,
        float pz = 0f,
        string name = "Sharer"
    )
    {
        tail ??= Tail();
        return EncodeSpawn(id, owner, name, 1, tail.Length, totalChunks * chunkBytes, totalChunks, px, py, pz, 0f, 0f, 0f, 1f, tail);
    }

    private static void Observe(ushort sender, byte[] payload) =>
        BasisNetworkModelCache.Observe(sender, payload, payload.Length);

    private static void ObserveTargeted(ushort sender, byte[] payload, ushort[] recipients) =>
        BasisNetworkModelCache.Observe(sender, payload, payload.Length, recipients, recipients.Length);

    private static Guid ShareModel(
        ushort owner,
        int chunks = 2,
        int chunkBytes = ChunkBytes,
        byte[]? tail = null,
        float px = 0f,
        float py = 0f,
        float pz = 0f
    )
    {
        Guid id = Guid.NewGuid();
        Observe(owner, ModelSpawn(id, owner, chunks, chunkBytes, tail, px, py, pz));
        for (int index = 0; index < chunks; index++)
        {
            Observe(owner, EncodeChunk(id, index, chunkBytes));
        }
        return id;
    }

    private static List<byte[]> PayloadsOf(ImageCacheRecordingPeer peer) => peer.Sent.Select(sent => PayloadOf(sent.Data)).ToList();

    private static bool HasCacheState(ImageCacheRecordingPeer peer, Guid id, bool held) =>
        PayloadsOf(peer).Any(payload =>
            payload.Length == 18 && payload[0] == OpServerCacheState && GuidOf(payload) == id && payload[17] == (held ? 1 : 0));

    // ---- identification --------------------------------------------------------------------

    [Fact]
    public void IsModelTraffic_MatchesOnlyTheModelManagersNetworkId()
    {
        Assert.Equal("BasisModelPickupManager", BasisNetworkModelCache.ModelManagerIdentifier);
        Assert.True(BasisNetworkModelCache.IsModelTraffic(ModelNetId));
        Assert.False(BasisNetworkModelCache.IsModelTraffic(ModelNetId + 1));
        Assert.False(BasisNetworkModelCache.IsModelTraffic(ImageNetId));
    }

    /// <summary>
    /// Ids restart from 0 whenever the instance empties. Whatever takes the model manager's old id
    /// next must not be treated as model traffic - its opcode-1 messages would be run through the
    /// props gate - and the manager's new id must be, with nobody resetting the cache in between.
    /// </summary>
    [Fact]
    public void ResettingTheIdDatabase_MovesModelTrafficToTheNewId()
    {
        Assert.True(BasisNetworkModelCache.IsModelTraffic(ModelNetId));

        BasisNetworkIDDatabase.Reset();
        Assert.False(BasisNetworkModelCache.IsModelTraffic(ModelNetId));

        BasisNetworkIDDatabase.UshortNetworkDatabase[OtherIdentifier] = ModelNetId;
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkModelCache.ModelManagerIdentifier] = 8;

        Assert.False(BasisNetworkModelCache.IsModelTraffic(ModelNetId));
        Assert.True(BasisNetworkModelCache.IsModelTraffic(8));
    }

    [Fact]
    public void RemovingTheModelManagersIdMapping_StopsMatchingIt()
    {
        Assert.True(BasisNetworkModelCache.IsModelTraffic(ModelNetId));

        BasisNetworkIDDatabase.RemoveUshortNetworkID(ModelNetId);

        Assert.False(BasisNetworkModelCache.IsModelTraffic(ModelNetId));
    }

    [Fact]
    public void AfterAnIdReset_RepliesAndNoticesUseTheNewManagerId()
    {
        const ushort NewModelNetId = 18;
        ImageCacheRecordingPeer owner = RegisterPeer(8);
        Guid model = ShareModel(owner: 8);
        owner.Sent.Clear();

        BasisNetworkIDDatabase.Reset();
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkModelCache.ModelManagerIdentifier] = NewModelNetId;

        ImageCacheRecordingPeer requester = RegisterPeer(9);
        BasisNetworkModelCache.ServeRequestedModel(9, model);
        Assert.Equal(3, requester.Sent.Count);
        Assert.All(requester.Sent, sent => Assert.Equal(NewModelNetId, MessageIndexOf(sent.Data)));

        Assert.True(BasisNetworkModelCache.Remove(model, requesterId: 8, ownerOnly: true));
        Assert.Equal(NewModelNetId, MessageIndexOf(Assert.Single(owner.Sent).Data));
    }

    [Fact]
    public void WithNoManagerIdRegistered_NothingIsSent()
    {
        Guid model = ShareModel(owner: 7);
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkModelCache.ModelManagerIdentifier, out _);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);
        BasisNetworkModelCache.ServeRequestedModel(9, model);

        Assert.Empty(joiner.Sent);
    }

    // ---- retention -------------------------------------------------------------------------

    [Fact]
    public void AFullyReceivedModel_IsHeldAndServable()
    {
        ShareModel(owner: 7);

        Assert.Equal(1, BasisNetworkModelCache.Count);
        Assert.Equal(1, BasisNetworkModelCache.ServableCount);
        Assert.True(BasisNetworkModelCache.TotalBytes > 0);
    }

    [Fact]
    public void AModelMissingChunks_IsHeldButNotServed()
    {
        Guid id = Guid.NewGuid();
        Observe(7, ModelSpawn(id, 7, totalChunks: 3));
        Observe(7, EncodeChunk(id, 0, ChunkBytes));

        Assert.Equal(1, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.ServableCount);
    }

    [Fact]
    public void NetIdZeroOwner_IsCachedLikeAnyOtherPlayer()
    {
        ShareModel(owner: 0);

        Assert.Equal(1, BasisNetworkModelCache.ServableCount);
        Assert.True(BasisNetworkModelCache.BytesHeldFor(0) > 0);
    }

    [Fact]
    public void ARepeatedSpawnHeader_DoesNotDoubleCount()
    {
        Guid id = Guid.NewGuid();
        byte[] spawn = ModelSpawn(id, 7, totalChunks: 1);

        Observe(7, spawn);
        long afterFirst = BasisNetworkModelCache.TotalBytes;
        Observe(7, spawn);

        Assert.Equal(afterFirst, BasisNetworkModelCache.TotalBytes);
        Assert.Equal(1, BasisNetworkModelCache.Count);
    }

    [Fact]
    public void AnimationOpcodes_UnderTheModelId_AreNotRetained()
    {
        // Models have no secondary stream. Opcodes 6 and 7 under the model id are relayed like any
        // payload, but the cache neither charges nor replays them.
        Guid id = ShareModel(owner: 7);
        long before = BasisNetworkModelCache.TotalBytes;

        byte[] animationSpawn = new byte[17 + 1 + 4 + 4 + 8];
        animationSpawn[0] = OpAnimationSpawn;
        id.TryWriteBytes(animationSpawn.AsSpan(1));
        BitConverter.TryWriteBytes(animationSpawn.AsSpan(22, 4), 1);
        Observe(7, animationSpawn);
        Observe(7, EncodeChunk(id, 0, ChunkBytes, OpAnimationChunk));

        Assert.Equal(before, BasisNetworkModelCache.TotalBytes);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.ServeRequestedModel(9, id);
        Assert.NotEmpty(joiner.Sent);
        Assert.DoesNotContain(joiner.Sent, sent => PayloadOpcode(sent.Data) == OpAnimationSpawn || PayloadOpcode(sent.Data) == OpAnimationChunk);
    }

    [Fact]
    public void ASpawnHeaderOverTheModelLimit_IsNotCached()
    {
        // 70 bytes of fixed header with a six-byte owner name; the rest is tail.
        int fixedBytes = ModelSpawn(Guid.NewGuid(), 7, 1, tail: Array.Empty<byte>()).Length;

        byte[] tooBig = ModelSpawn(Guid.NewGuid(), 7, 1, tail: new byte[BasisNetworkModelCache.MaxSpawnHeaderBytes - fixedBytes + 1]);
        Assert.Equal(BasisNetworkModelCache.MaxSpawnHeaderBytes + 1, tooBig.Length);
        Observe(7, tooBig);
        Assert.Equal(0, BasisNetworkModelCache.Count);

        byte[] atLimit = ModelSpawn(Guid.NewGuid(), 7, 1, tail: new byte[BasisNetworkModelCache.MaxSpawnHeaderBytes - fixedBytes]);
        Assert.Equal(BasisNetworkModelCache.MaxSpawnHeaderBytes, atLimit.Length);
        Observe(7, atLimit);
        Assert.Equal(1, BasisNetworkModelCache.Count);
    }

    [Fact]
    public void AnOwnersSeventeenthModel_EvictsTheirOldest()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(7);

        Guid first = ShareModel(owner: 7, chunks: 1, chunkBytes: 16);
        for (int index = 1; index < BasisNetworkModelCache.MaxModelsPerOwner; index++)
        {
            ShareModel(owner: 7, chunks: 1, chunkBytes: 16);
        }
        Assert.Equal(BasisNetworkModelCache.MaxModelsPerOwner, BasisNetworkModelCache.Count);
        Assert.False(HasCacheState(owner, first, held: false));

        Guid seventeenth = ShareModel(owner: 7, chunks: 1, chunkBytes: 16);

        Assert.Equal(BasisNetworkModelCache.MaxModelsPerOwner, BasisNetworkModelCache.Count);
        Assert.False(BasisNetworkModelCache.Remove(first, requesterId: 7, ownerOnly: true));
        Assert.True(HasCacheState(owner, first, held: false), "the owner must be told to provide its evicted model again");
        Assert.True(HasCacheState(owner, seventeenth, held: true));
    }

    [Fact]
    public void AnotherOwnersModels_DoNotCountAgainstTheSixteen()
    {
        for (int index = 0; index < BasisNetworkModelCache.MaxModelsPerOwner; index++)
        {
            ShareModel(owner: 7, chunks: 1, chunkBytes: 16);
            ShareModel(owner: 8, chunks: 1, chunkBytes: 16);
        }

        Assert.Equal(2 * BasisNetworkModelCache.MaxModelsPerOwner, BasisNetworkModelCache.Count);
    }

    // ---- removal ---------------------------------------------------------------------------

    [Fact]
    public void TheOwnersDespawn_ClearsTheServerCopy()
    {
        Guid id = ShareModel(owner: 7);

        Observe(7, EncodeOpGuid(OpDespawn, id));

        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.TotalBytes);
    }

    /// <summary>
    /// Clients honour a despawn from anyone, but the server copy goes only on the owner's word:
    /// otherwise anyone could clear a sharer's entry and re-spawn the id with content of their own.
    /// The owner echoes a despawn it honoured, and that echo is what evicts.
    /// </summary>
    [Fact]
    public void ADespawnFromANonOwner_IsIgnoredUntilTheOwnerEchoes()
    {
        Guid id = ShareModel(owner: 7);

        Observe(9, EncodeOpGuid(OpDespawn, id));
        Assert.Equal(1, BasisNetworkModelCache.Count);

        Observe(7, EncodeOpGuid(OpDespawn, id));
        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.TotalBytes);
    }

    [Fact]
    public void AnEvictedGuidCannotBeReclaimedByAnotherSender()
    {
        byte[] ownerTail = Tail(seed: 1);
        Guid id = Guid.NewGuid();
        byte[] ownerSpawn = ModelSpawn(id, 7, 2, tail: ownerTail);
        Observe(7, ownerSpawn);
        Observe(7, EncodeChunk(id, 0, ChunkBytes));
        Observe(7, EncodeChunk(id, 1, ChunkBytes));

        // The takeover attempt: clear the entry, then claim the id with different content.
        Observe(9, EncodeOpGuid(OpDespawn, id));
        Observe(9, ModelSpawn(id, 9, 1, chunkBytes: 16, tail: Tail(seed: 200)));
        Observe(9, EncodeChunk(id, 0, 16));

        Assert.Equal(0, BasisNetworkModelCache.BytesHeldFor(9));
        ImageCacheRecordingPeer joiner = RegisterPeer(11);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);
        byte[] expected = (byte[])ownerSpawn.Clone();
        expected[0] = OpServerCacheOffer;
        Assert.Equal(expected, PayloadOf(Assert.Single(joiner.Sent).Data));
    }

    [Fact]
    public void RemoveRequest_ClearsRegardlessOfRequesterWhenNotOwnerGated()
    {
        Guid id = ShareModel(owner: 7);

        Assert.True(BasisNetworkModelCache.Remove(id, requesterId: 9, ownerOnly: false));
        Assert.Equal(0, BasisNetworkModelCache.Count);
    }

    [Fact]
    public void WhenTheSharerDisconnects_TheirModelsAreDropped()
    {
        ShareModel(owner: 7);
        ShareModel(owner: 7);
        ShareModel(owner: 9);

        BasisNetworkModelCache.RemovePlayerModels(7);

        Assert.Equal(1, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.BytesHeldFor(7));
        Assert.True(BasisNetworkModelCache.BytesHeldFor(9) > 0);
    }

    [Fact]
    public void RemovePlayerModels_ScrubsTheLeaverFromOfferedAndDelivered()
    {
        Guid id = ShareModel(owner: 7);
        ImageCacheRecordingPeer leaver = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(leaver);
        BasisNetworkModelCache.ServeRequestedModel(9, id);
        Assert.NotEmpty(leaver.Sent);

        BasisNetworkModelCache.RemovePlayerModels(9);
        NetworkServer.AuthenticatedPeers.TryRemove(9, out _);

        // Peer ids are recycled: whoever gets 9 next has been sent nothing.
        ImageCacheRecordingPeer successor = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(successor);
        Assert.Single(successor.Sent);
        Assert.Equal(OpServerCacheOffer, PayloadOpcode(successor.Sent[0].Data));
    }

    // ---- budget and fairness ---------------------------------------------------------------

    [Fact]
    public void AModelBiggerThanTheWholeBuffer_IsNotCached()
    {
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 1;

        ShareModel(owner: 7, chunks: 2, chunkBytes: 1024 * 1024);

        Assert.Equal(0, BasisNetworkModelCache.ServableCount);
        Assert.True(BasisNetworkModelCache.TotalBytes <= 1024 * 1024);
    }

    [Fact]
    public void TheBufferNeverExceedsItsCap()
    {
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 1;
        long cap = 1024 * 1024;

        for (int index = 0; index < 40; index++)
        {
            ShareModel(owner: (ushort)(index % 4), chunks: 2);
            Assert.True(BasisNetworkModelCache.TotalBytes <= cap, $"cache overran its cap after {index + 1} shares");
        }
    }

    [Fact]
    public void OnePlayerFloodingTheBuffer_CannotEvictAnotherPlayersModels()
    {
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 1;

        ShareModel(owner: 1, chunks: 1);
        long quietOwnerBytes = BasisNetworkModelCache.BytesHeldFor(1);
        Assert.True(quietOwnerBytes > 0);

        for (int index = 0; index < 30; index++)
        {
            ShareModel(owner: 2, chunks: 2);
        }

        Assert.Equal(quietOwnerBytes, BasisNetworkModelCache.BytesHeldFor(1));
    }

    [Fact]
    public void AnOwnerOverTheirShare_LosesTheirOwnOldestModelFirst()
    {
        // Fewer than sixteen models, so it is the byte share doing the evicting, not the count.
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 1;

        Guid oldest = ShareModel(owner: 5, chunks: 4);
        for (int index = 0; index < 5; index++)
        {
            ShareModel(owner: 5, chunks: 4);
        }

        Assert.False(BasisNetworkModelCache.Remove(oldest, requesterId: 5, ownerOnly: true));
        Assert.True(BasisNetworkModelCache.BytesHeldFor(5) > 0);
    }

    /// <summary>
    /// The sizing rule behind the default floor: one maximum-size model (32 MiB in 16 KiB chunks,
    /// with its framing and backbone) must still fit one sharer's slice in a busy room.
    /// </summary>
    [Fact]
    public void TheLargestAllowedModel_FitsUnderTheDefaultFloorInABusyRoom()
    {
        NetworkServer.Configuration = new Configuration { ImageShareDownloadMegabitsPerSecond = 0 };
        for (ushort owner = 100; owner < 120; owner++)
        {
            ShareModel(owner, chunks: 1, chunkBytes: 1024);
        }

        Guid big = ShareLargestModel(owner: 7);

        Assert.Equal(21, BasisNetworkModelCache.ServableCount);
        Assert.True(BasisNetworkModelCache.BytesHeldFor(7) > 32L * 1024 * 1024);
        Assert.True(BasisNetworkModelCache.Remove(big, requesterId: 7, ownerOnly: true));
    }

    [Fact]
    public void AFloorBelowTheLargestModel_RefusesItInABusyRoom()
    {
        NetworkServer.Configuration = new Configuration
        {
            ImageShareDownloadMegabitsPerSecond = 0,
            ModelCacheMinimumPerOwnerMegabytes = 32,
        };
        for (ushort owner = 100; owner < 140; owner++)
        {
            ShareModel(owner, chunks: 1, chunkBytes: 1024);
        }

        ShareLargestModel(owner: 7);

        Assert.Equal(40, BasisNetworkModelCache.ServableCount);
    }

    private static Guid ShareLargestModel(ushort owner)
    {
        const int Chunks = 2048;
        const int Bytes = 16 * 1024;
        Guid id = Guid.NewGuid();
        Observe(owner, ModelSpawn(id, owner, Chunks, Bytes, tail: new byte[256]));
        byte[] chunk = EncodeChunk(id, 0, Bytes);
        for (int index = 0; index < Chunks; index++)
        {
            SetChunkIndex(chunk, index);
            Observe(owner, chunk);
        }
        return id;
    }

    [Fact]
    public void OwnerTally_StaysConsistentWithEntries()
    {
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 1;
        Random random = new Random(20261006);
        List<(Guid Id, ushort Owner, int Chunks)> known = new();

        for (int step = 0; step < 500; step++)
        {
            int roll = random.Next(100);
            if (roll < 30 || known.Count == 0)
            {
                ushort owner = (ushort)random.Next(6);
                int chunks = random.Next(1, 4);
                Guid id = Guid.NewGuid();
                Observe(owner, ModelSpawn(id, owner, chunks, chunkBytes: 32 * 1024));
                known.Add((id, owner, chunks));
            }
            else
            {
                (Guid id, ushort owner, int chunks) = known[random.Next(known.Count)];
                if (roll < 75)
                {
                    Observe(owner, EncodeChunk(id, random.Next(chunks), 32 * 1024));
                }
                else if (roll < 85)
                {
                    Observe((ushort)random.Next(6), EncodeTransform(id, random.Next(10), 0f, 0f));
                }
                else if (roll < 92)
                {
                    Observe((ushort)random.Next(6), EncodeOpGuid(OpDespawn, id));
                }
                else if (roll < 97)
                {
                    BasisNetworkModelCache.Remove(id, (ushort)random.Next(6), ownerOnly: random.Next(2) == 0);
                }
                else
                {
                    BasisNetworkModelCache.RemovePlayerModels(owner);
                }
            }
        }

        Assert.True(BasisNetworkModelCache.OwnerTallyMatchesEntriesForTests());
        long sum = 0;
        for (ushort owner = 0; owner < 6; owner++)
        {
            sum += BasisNetworkModelCache.BytesHeldFor(owner);
        }
        Assert.Equal(BasisNetworkModelCache.TotalBytes, sum);
        Assert.True(BasisNetworkModelCache.TotalBytes <= 1024 * 1024);
    }

    // ---- disabled ---------------------------------------------------------------------------

    [Fact]
    public void WithTheModelCacheOff_NothingIsRetained()
    {
        NetworkServer.Configuration.ModelCacheEnabled = false;

        ShareModel(owner: 7);

        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.TotalBytes);
    }

    [Fact]
    public void WithAZeroModelBudget_NothingIsRetained()
    {
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 0;

        ShareModel(owner: 7);

        Assert.Equal(0, BasisNetworkModelCache.Count);
    }

    // ---- offer to a joiner, replay on request -------------------------------------------------

    [Fact]
    public void AJoinerIsOfferedEachModelAndSentNoChunks()
    {
        ShareModel(owner: 7, chunks: 3);
        ShareModel(owner: 7, chunks: 3);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        Assert.Equal(2, joiner.Sent.Count);
        Assert.All(joiner.Sent, sent => Assert.Equal(OpServerCacheOffer, PayloadOpcode(sent.Data)));
    }

    [Fact]
    public void AnOfferCarriesTheSharersHeaderAndTailVerbatimApartFromTheOpcode()
    {
        Guid id = Guid.NewGuid();
        byte[] spawn = ModelSpawn(id, 7, 2, px: 12.5f, pz: -3f);
        Observe(7, spawn);
        Observe(7, EncodeChunk(id, 0, ChunkBytes));
        Observe(7, EncodeChunk(id, 1, ChunkBytes));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        byte[] expected = (byte[])spawn.Clone();
        expected[0] = OpServerCacheOffer;
        byte[] offer = PayloadOf(joiner.Sent[0].Data);
        Assert.Equal(expected, offer);
        Assert.Equal(Tail(), TailOf(offer));
    }

    [Fact]
    public void AnOfferCarriesTheLatestPoseButLeavesTheTailUntouched()
    {
        Guid id = ShareModel(owner: 7, px: 12.5f);
        Observe(11, EncodeTransform(id, 1f, 2f, 3f, ry: 0.7071f, rw: 0.7071f, scale: 2f));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        byte[] offer = PayloadOf(joiner.Sent[0].Data);
        Assert.Equal((1f, 2f, 3f, 0f, 0.7071f, 0f, 0.7071f), ReadSpawnPose(offer));
        Assert.Equal(Tail(), TailOf(offer));
    }

    [Fact]
    public void RequestingAnOfferedModel_SendsSpawnTransformAndEveryChunkInOrder()
    {
        Guid id = ShareModel(owner: 7, chunks: 3);
        byte[] moved = EncodeTransform(id, 1f, 2f, 3f, rz: 0.3827f, rw: 0.9239f, scale: 2.5f);
        Observe(7, moved);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);
        joiner.Sent.Clear();

        BasisNetworkModelCache.ServeRequestedModel(9, id);

        List<byte[]> payloads = PayloadsOf(joiner);
        Assert.Equal(5, payloads.Count);
        Assert.Equal(OpSpawn, payloads[0][0]);
        Assert.Equal((1f, 2f, 3f, 0f, 0f, 0.3827f, 0.9239f), ReadSpawnPose(payloads[0]));
        Assert.Equal(Tail(), TailOf(payloads[0]));
        Assert.Equal(49, payloads[1].Length);
        Assert.Equal(moved, payloads[1]);
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(OpChunk, payloads[2 + index][0]);
            Assert.Equal(index, BitConverter.ToInt32(payloads[2 + index], 17));
        }
    }

    [Fact]
    public void ReplayedModels_GoOutOnDirectSceneServerChannelUnderTheModelId()
    {
        Guid id = ShareModel(owner: 7, chunks: 2);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);
        joiner.Sent.Clear();
        BasisNetworkModelCache.ServeRequestedModel(9, id);

        Assert.Equal(3, joiner.Sent.Count);
        Assert.All(joiner.Sent, sent =>
        {
            Assert.Equal(BasisNetworkCommons.DirectSceneServerChannel, sent.Channel);
            Assert.Equal(ModelNetId, MessageIndexOf(sent.Data));
            Assert.Equal((ushort)7, PlayerIdOf(sent.Data));
        });
    }

    [Fact]
    public void OffersAndCacheStateAreStampedWithTheRecipientsOwnId()
    {
        // The client trusts 8 and 9 only under its own id, which the relay never lets anyone forge.
        ImageCacheRecordingPeer owner = RegisterPeer(7);
        ShareModel(owner: 7);
        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        Assert.Contains(owner.Sent, sent => PayloadOpcode(sent.Data) == OpServerCacheState);
        Assert.All(owner.Sent, sent => Assert.Equal((ushort)7, PlayerIdOf(sent.Data)));
        Assert.Equal(OpServerCacheOffer, PayloadOpcode(Assert.Single(joiner.Sent).Data));
        Assert.Equal((ushort)9, PlayerIdOf(joiner.Sent[0].Data));
        Assert.Equal(ModelNetId, MessageIndexOf(joiner.Sent[0].Data));
    }

    [Fact]
    public void AnIncompleteModel_IsNotOffered()
    {
        Guid id = Guid.NewGuid();
        Observe(7, ModelSpawn(id, 7, totalChunks: 3));
        Observe(7, EncodeChunk(id, 0, ChunkBytes));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void RequestingAnIncompleteModel_SendsNothing()
    {
        Guid id = Guid.NewGuid();
        Observe(7, ModelSpawn(id, 7, totalChunks: 3));
        Observe(7, EncodeChunk(id, 0, ChunkBytes));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.ServeRequestedModel(9, id);

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void RequestingTheSameModelTwice_SendsItOnce()
    {
        Guid id = ShareModel(owner: 7);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        Observe(9, EncodeOpGuid(OpServerCacheRequest, id));
        Assert.NotEmpty(joiner.Sent);

        joiner.Sent.Clear();
        Observe(9, EncodeOpGuid(OpServerCacheRequest, id));

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void APeerIsOfferedAModelOnlyOnce()
    {
        ShareModel(owner: 7);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);
        Assert.NotEmpty(joiner.Sent);

        joiner.Sent.Clear();
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void APeerTheSharerAlreadyTargetedIsNotOffered()
    {
        ImageCacheRecordingPeer nearby = RegisterPeer(9);

        Guid id = Guid.NewGuid();
        ushort[] targeted = { 9 };
        ObserveTargeted(7, ModelSpawn(id, 7, totalChunks: 2), targeted);
        for (int index = 0; index < 2; index++)
        {
            ObserveTargeted(7, EncodeChunk(id, index, ChunkBytes), targeted);
        }
        nearby.Sent.Clear();

        BasisNetworkModelCache.OfferCachedModelsToPeer(nearby);

        Assert.Empty(nearby.Sent);
    }

    [Fact]
    public void APeerTheSharerCouldNotReachIsOfferedTheModelAsItCompletes()
    {
        ImageCacheRecordingPeer latecomer = RegisterPeer(11);

        Guid id = Guid.NewGuid();
        ushort[] targeted = { 9 };
        ObserveTargeted(7, ModelSpawn(id, 7, totalChunks: 2), targeted);
        for (int index = 0; index < 2; index++)
        {
            ObserveTargeted(7, EncodeChunk(id, index, ChunkBytes), targeted);
        }

        Assert.Single(latecomer.Sent);
        Assert.Equal(OpServerCacheOffer, PayloadOpcode(latecomer.Sent[0].Data));
    }

    [Fact]
    public void AnOwnerIsNeverOfferedTheirOwnModel()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(7);

        ShareModel(owner: 7);
        owner.Sent.Clear();
        BasisNetworkModelCache.OfferCachedModelsToPeer(owner);

        Assert.Empty(owner.Sent);
    }

    [Fact]
    public void TheOwnerIsToldWhenTheirModelBecomesServable()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(7);

        Guid id = ShareModel(owner: 7, chunks: 2);

        (byte Channel, byte[] Data) notice = Assert.Single(owner.Sent);
        Assert.Equal(BasisNetworkCommons.DirectSceneServerChannel, notice.Channel);
        Assert.Equal(ModelNetId, MessageIndexOf(notice.Data));
        Assert.True(HasCacheState(owner, id, held: true));
    }

    [Fact]
    public void EvictingAModel_TellsItsOwnerTheyAreProvidingItAgain()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(5);
        NetworkServer.Configuration.ModelCacheMaxMegabytes = 1;

        Guid first = ShareModel(owner: 5, chunks: 4);
        for (int index = 0; index < 5; index++)
        {
            ShareModel(owner: 5, chunks: 4);
        }

        Assert.True(HasCacheState(owner, first, held: false));
    }

    // ---- where the model actually is ---------------------------------------------------------

    [Fact]
    public void ATransformFromWhoeverPickedTheModelUp_IsFollowed()
    {
        Guid id = ShareModel(owner: 7, chunks: 2);
        Observe(11, EncodeTransform(id, 4f, 5f, 6f, rx: 0.5f, rw: 0.5f));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        Assert.Equal((4f, 5f, 6f, 0.5f, 0f, 0f, 0.5f), ReadSpawnPose(PayloadOf(joiner.Sent[0].Data)));
    }

    [Fact]
    public void RepeatedTransforms_AreChargedOnce()
    {
        Guid id = ShareModel(owner: 7, chunks: 2);
        long beforeAnyPose = BasisNetworkModelCache.TotalBytes;

        Observe(7, EncodeTransform(id, 1f, 0f, 0f));
        long afterFirstPose = BasisNetworkModelCache.TotalBytes;
        for (int step = 0; step < 32; step++)
        {
            Observe(7, EncodeTransform(id, step, 0f, 0f));
        }

        Assert.True(afterFirstPose > beforeAnyPose);
        Assert.Equal(afterFirstPose, BasisNetworkModelCache.TotalBytes);
    }

    [Fact]
    public void ATransformOfTheWrongLength_LeavesThePoseAlone()
    {
        Guid id = ShareModel(owner: 7, chunks: 2, px: 12.5f);
        Observe(7, EncodeTransform(id, 1f, 2f, 3f).AsSpan(0, 30).ToArray());

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);

        Assert.Equal((12.5f, 0f, 0f, 0f, 0f, 0f, 1f), ReadSpawnPose(PayloadOf(joiner.Sent[0].Data)));
    }

    /// <summary>
    /// Any peer can send a transform. One the model client would refuse - non-finite, beyond its
    /// 1e5 m position limit, a degenerate rotation or a non-positive scale - must not become the pose
    /// every later offer carries, or the model is hidden from every joiner after it.
    /// </summary>
    [Fact]
    public void ANonFiniteTransform_DoesNotReplaceTheCachedPose()
    {
        Guid id = ShareModel(owner: 7, chunks: 2, px: 12.5f);
        long before = BasisNetworkModelCache.TotalBytes;

        Observe(11, EncodeTransform(id, float.NaN, 0f, 0f));
        Observe(11, EncodeTransform(id, 0f, float.NegativeInfinity, 0f));
        Observe(11, EncodeTransform(id, 5e5f, 0f, 0f));
        Observe(11, EncodeTransform(id, 0f, 0f, 0f, rw: 0f));
        Observe(11, EncodeTransform(id, 0f, 0f, 0f, rw: 3f));
        Observe(11, EncodeTransform(id, 0f, 0f, 0f, scale: -1f));
        Observe(11, EncodeTransform(id, 0f, 0f, 0f, scale: float.PositiveInfinity));

        Assert.Equal(before, BasisNetworkModelCache.TotalBytes);
        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkModelCache.OfferCachedModelsToPeer(joiner);
        Assert.Equal((12.5f, 0f, 0f, 0f, 0f, 0f, 1f), ReadSpawnPose(PayloadOf(joiner.Sent[0].Data)));
    }

    [Fact]
    public void ASpawnWithANonFinitePose_IsNotCached()
    {
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, 1, px: float.NaN));
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, 1, py: float.PositiveInfinity));
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, 1, pz: 2e5f));
        Observe(7, EncodeSpawn(Guid.NewGuid(), 7, "Sharer", 1, TailBytes, ChunkBytes, 1, 0f, 0f, 0f, float.NaN, 0f, 0f, 1f, Tail()));

        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.TotalBytes);

        ShareModel(owner: 7, px: 9.9e4f);
        Assert.Equal(1, BasisNetworkModelCache.ServableCount);
    }

    [Fact]
    public void ATransformForAModelTheCacheDoesNotHold_IsIgnored()
    {
        Observe(7, EncodeTransform(Guid.NewGuid(), 1f, 2f, 3f));

        Assert.Equal(0, BasisNetworkModelCache.Count);
        Assert.Equal(0, BasisNetworkModelCache.TotalBytes);
    }

    // ---- malformed input --------------------------------------------------------------------

    [Fact]
    public void MalformedPayloads_AreIgnoredWithoutThrowing()
    {
        Observe(7, new byte[0]);
        Observe(7, new byte[] { OpSpawn });
        Observe(7, new byte[] { OpChunk, 1, 2, 3 });
        Observe(7, new byte[] { OpTransform, 1, 2, 3 });
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, 2).AsSpan(0, 20).ToArray());
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, 0));
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, -5));

        // An owner name longer than any client would send is not walked past.
        Observe(7, ModelSpawn(Guid.NewGuid(), 7, 1, name: new string('n', 1100)));

        Assert.Equal(0, BasisNetworkModelCache.Count);
    }
}
