using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Basis.EventDriver;
using Basis.ModelPickup.Tests.Validation;
using Basis.ModelPickup.Validation;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// The manager driven through its message handler and test seams: a recording sink stands in for the
    /// transport, and no network, menu or player exists.
    /// </summary>
    public sealed class BasisModelPickupManagerTests
    {
        private const ushort LocalPlayer = 1;
        private const DeliveryMethod Reliable = DeliveryMethod.ReliableOrdered;

        private RecordingSink _sink;
        private bool _restoreReceiveOff;
        private bool _restoreCacheSeams;
        private Func<byte[], BasisModelLimits, CancellationToken, Task<BasisGlbValidationResult>> _savedValidation;
        private readonly List<string> _tempFiles = new List<string>();

        [SetUp]
        public void SetUp()
        {
            BasisModelPickupManager.Shutdown();
            // Before the sink exists: turning receive back on would announce it.
            _restoreReceiveOff = !BasisModelPickupSettings.ReceiveEnabled;
            if (_restoreReceiveOff)
                BasisModelPickupSettings.ReceiveEnabled = true;

            _sink = new RecordingSink();
            _savedValidation = BasisModelPickupManager.StartReceiveValidation;
            BasisModelPickupManager.SinkOverride = _sink;
            BasisModelPickupManager.LocalIdOverride = LocalPlayer;
            BasisModelPickupManager.IsHeadless = false;
            BasisModelSizeDialog.ResetForTests();
            BasisModelSizeDialog.Prompt = new UnavailablePrompt();
            BasisModelPickupManager.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelPickupManager.Shutdown();
            BasisModelPickupManager.SinkOverride = null;
            BasisModelPickupManager.LocalIdOverride = BasisModelShareNet.UnownedPlayerId;
            BasisModelPickupManager.IsHeadless = BasisEventDriver.IsHeadlessClient;
            BasisModelPickupManager.StartReceiveValidation = _savedValidation;
            BasisModelSizeDialog.ResetForTests();
            if (_restoreReceiveOff)
                BasisModelPickupSettings.ReceiveEnabled = false;
            if (_restoreCacheSeams)
            {
                _restoreCacheSeams = false;
                BasisModelPickupManager.ServerCache.LocalId = BasisNetworkConnection.TryGetLocalPlayerID;
                BasisModelPickupManager.ServerCache.Viewer = BasisModelReplicationScan.TryGetLocalViewerPosition;
            }
            for (int i = 0; i < _tempFiles.Count; i++)
            {
                try
                {
                    File.Delete(_tempFiles[i]);
                }
                catch (IOException)
                {
                }
            }
            _tempFiles.Clear();
        }

        [Test]
        public void ManagerIsStaticAndNeedsNoGameObject()
        {
            Type type = typeof(BasisModelPickupManager);
            Assert.That(type.IsAbstract && type.IsSealed, Is.True, "the manager is a static class");
            Assert.That(typeof(Component).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void ShutdownIsIdempotentWithoutInitialize()
        {
            BasisModelPickupManager.Shutdown();
            Assert.DoesNotThrow(BasisModelPickupManager.Shutdown);
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(0));
            Assert.That(BasisModelPickupManager.JobCount, Is.EqualTo(0));
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0L));
        }

        [Test]
        public void SpawnFromFilesIgnoresNonModelPaths()
        {
            string image = TempFile(".png");
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".glb");

            int accepted = BasisModelPickupManager.SpawnFromFiles(new[] { image, "notes.txt", null, "", missing, "bad\0.glb" });

            Assert.That(accepted, Is.EqualTo(0));
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(0));
            Assert.That(BasisModelPickupManager.JobCount, Is.EqualTo(0));
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void DispatchIgnoresEmptyUnknownAndSelfEchoOpcodes()
        {
            Guid id = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(3, null, Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, Array.Empty<byte>(), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, new byte[] { 6, 1, 2, 3 }, Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, new byte[] { 7, 1, 2, 3 }, Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, new byte[] { 200 }, Reliable);
            // Our own server-cache request and our own hello come back through the relay and mean nothing.
            BasisModelPickupManager.OnDirectNetworkMessage(LocalPlayer, IdMessage(BasisModelShareWire.OpServerCacheRequest, id), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(LocalPlayer, Hello(false), Reliable);

            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(0));

            // Someone else's hello is answered, so the silence above was not just a deaf handler.
            BasisModelPickupManager.OnDirectNetworkMessage(3, Hello(false), Reliable);
            Assert.That(_sink.Count, Is.EqualTo(1));
            Assert.That(_sink.Packets[0][0], Is.EqualTo(BasisModelWire.OpHello));
            Assert.That(_sink.Packets[0][2] & BasisModelWire.HelloFlagReply, Is.Not.EqualTo(0));
            Assert.That(_sink.Recipients[0], Is.EqualTo(new ushort[] { 3 }));
        }

        [Test]
        public void HeadlessIgnoresSpawnsAndSendsNoHello()
        {
            BasisModelPickupManager.IsHeadless = true;
            BasisModelPickupManager.OnIdentityArmed();
            BasisModelPickupManager.OnDirectNetworkMessage(3, Hello(false), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(Guid.NewGuid(), 3, 1000), Reliable);

            Assert.That(_sink.Count, Is.EqualTo(0), "no hello, and no reply to one");
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(0));
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0L));

            BasisModelPickupManager.IsHeadless = false;
            BasisModelPickupManager.OnIdentityArmed();
            Assert.That(_sink.Count, Is.EqualTo(1), "a capable client announces itself on arming");
            Assert.That(_sink.Packets[0], Is.EqualTo(Hello(false)));
            Assert.That(_sink.Recipients[0], Is.Null);
        }

        [Test]
        public void DespawnFromAnyPeerRemovesAndOwnerEchoes()
        {
            Assert.That(BasisModelPickupManager.SpawnFromFile(TempFile(".glb")), Is.True);
            Guid owned = SingleModelId();
            Guid remote = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(remote, 3, 1000), Reliable);
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(2));
            _sink.Clear();

            // Player 5 deletes player 3's model: honoured, and not ours to repeat.
            BasisModelPickupManager.OnDirectNetworkMessage(5, IdMessage(BasisModelShareWire.OpDespawn, remote), Reliable);
            Assert.That(BasisModelPickupManager.TryGetModel(remote, out _), Is.False);
            Assert.That(_sink.Count, Is.EqualTo(0));

            // Player 5 deletes ours: we repeat it, because the server cache only forgets on the owner's word.
            BasisModelPickupManager.OnDirectNetworkMessage(5, IdMessage(BasisModelShareWire.OpDespawn, owned), Reliable);
            Assert.That(BasisModelPickupManager.TryGetModel(owned, out _), Is.False);
            // The broadcast, then a copy addressed to ourselves so the relay (and its cache) always sees it.
            Assert.That(_sink.Count, Is.EqualTo(2));
            Assert.That(_sink.Packets[0], Is.EqualTo(IdMessage(BasisModelShareWire.OpDespawn, owned)));
            Assert.That(_sink.Recipients[0], Is.Null);
            Assert.That(_sink.Packets[1], Is.EqualTo(IdMessage(BasisModelShareWire.OpDespawn, owned)));
            Assert.That(_sink.Recipients[1], Is.EqualTo(new[] { BasisModelPickupManager.LocalIdOverride }));
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0L));
        }

        [Test]
        public void RemoteFollowersMoveInTheFollowPassAndKeepTheirRootsAcrossARemoval()
        {
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(4, Spawn(first, 4, 1000), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(second, 3, 1000), Reliable);
            Assert.That(BasisModelPickupManager.TryGetModel(second, out BasisModelPickupObject pickup), Is.True);
            Assert.That(pickup.ManagerIndex, Is.EqualTo(1));

            // The first goes; the second is swapped into its slot, and its root into the same place in the pass.
            BasisModelPickupManager.OnDirectNetworkMessage(5, IdMessage(BasisModelShareWire.OpDespawn, first), Reliable);
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(1));
            Assert.That(pickup.ManagerIndex, Is.EqualTo(0));
            Assert.That(BasisModelPickupManager.ModelArray[0], Is.SameAs(pickup));

            var start = new Vector3(0f, 1f, 0f);
            BasisModelPickupManager.OnDirectNetworkMessage(3, TransformMessage(second, start), Reliable);
            Assert.That(pickup.Root.position, Is.EqualTo(start), "the first target snaps");

            // Within a millimetre: one pass lands it exactly, whatever the frame's delta time.
            var target = start + new Vector3(0.0005f, 0f, 0f);
            BasisModelPickupManager.OnDirectNetworkMessage(3, TransformMessage(second, target), Reliable);
            Assert.That(pickup.Sync.NeedsFollow, Is.True);
            BasisModelPickupManager.SimulateUpdate();
            Assert.That(pickup.Root.position, Is.EqualTo(target), "the follow pass moved the right root");
            Assert.That(pickup.Sync.Settled, Is.True);

            pickup.Root.position = new Vector3(7f, 1f, 0f);
            BasisModelPickupManager.SimulateUpdate();
            Assert.That(pickup.Root.position, Is.EqualTo(new Vector3(7f, 1f, 0f)), "a settled follower is left alone");
        }

        private static byte[] TransformMessage(Guid id, Vector3 position)
        {
            var message = new byte[BasisModelShareWire.TransformBytes];
            BasisModelShareWire.WriteTransform(message, id, BasisModelShareConvert.ToSharePose(position, Quaternion.identity), 1f);
            return message;
        }

        [Test]
        public void JoinerIsGreetedDirectly()
        {
            BasisModelPickupManager.HandlePlayerJoined(LocalPlayer);
            Assert.That(_sink.Count, Is.EqualTo(0), "our own join");

            // The hello sent on arming reached only the players listed then.
            BasisModelPickupManager.HandlePlayerJoined(4);
            Assert.That(_sink.Count, Is.EqualTo(1));
            Assert.That(_sink.Packets[0], Is.EqualTo(Hello(false)));
            Assert.That(_sink.Recipients[0], Is.EqualTo(new ushort[] { 4 }));
        }

        [Test]
        public void OffersAreRequestedOnlyAsFarAsTheBudgetGoesCountingThoseInFlight()
        {
            UseServerCache();
            var validation = new TaskCompletionSource<BasisGlbValidationResult>();
            BasisModelPickupManager.StartReceiveValidation = (wire, limits, token) => validation.Task;

            int budget = BasisModelPickupSettings.Limits.Sender.MaxModels;
            for (int i = 0; i < budget + 2; i++)
                BasisModelPickupManager.OnDirectNetworkMessage(LocalPlayer, Offer(Guid.NewGuid(), 3), Reliable);

            RunOfferPass();
            List<Guid> first = RequestedIds();
            Assert.That(first.Count, Is.EqualTo(budget), "one pass asks only for what fits beside the requests still on their way");

            // The replays land and their bytes move to jobs, which hold the budget while they load.
            _sink.Clear();
            for (int i = 0; i < first.Count; i++)
            {
                BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(first[i], 3, 1000), Reliable);
                BasisModelPickupManager.OnDirectNetworkMessage(3, Chunk(first[i], 1000), Reliable);
            }
            Assert.That(BasisModelPickupManager.JobCount, Is.EqualTo(budget), "every requested replay was admitted");
            RunOfferPass();
            Assert.That(RequestedIds(), Is.Empty);

            // They finish (here by failing validation) and give the budget back: the two held back are asked for now.
            validation.SetResult(new BasisGlbValidationResult { Ok = false, Error = "test" });
            for (int i = 0; i < 4 * budget && BasisModelPickupManager.JobCount > 0; i++)
                BasisModelPickupManager.SimulateUpdate();
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(0));
            RunOfferPass();
            List<Guid> rest = RequestedIds();
            Assert.That(rest.Count, Is.EqualTo(2));
            Assert.That(first, Has.No.Member(rest[0]).And.No.Member(rest[1]));
        }

        [Test]
        public void ReplayLandingAfterItsDespawnIsIgnored()
        {
            Guid id = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(id, 3, 1000), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, IdMessage(BasisModelShareWire.OpDespawn, id), Reliable);
            Assert.That(BasisModelPickupManager.TryGetModel(id, out _), Is.False);

            // The server's paced copy of the same model, still queued when the despawn overtook it.
            BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(id, 3, 1000), Reliable);

            Assert.That(BasisModelPickupManager.TryGetModel(id, out _), Is.False);
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0L));
        }

        [Test]
        public void OffersLeaveWithTheirSharer()
        {
            UseServerCache();
            Guid fromLeaver = Guid.NewGuid();
            Guid fromStayer = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(LocalPlayer, Offer(fromLeaver, 3), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(LocalPlayer, Offer(fromStayer, 4), Reliable);
            Assert.That(BasisModelPickupManager.ServerCache.PendingOfferCount, Is.EqualTo(2));

            BasisModelPickupManager.HandlePlayerLeft(3);

            Assert.That(BasisModelPickupManager.ServerCache.TryGetOffer(fromLeaver, out _), Is.False, "the server never withdraws it");
            Assert.That(BasisModelPickupManager.ServerCache.TryGetOffer(fromStayer, out _), Is.True);
        }

        [UnityTest]
        public IEnumerator SharedModelDestroyedFromOutsideIsDespawnedForEveryone()
        {
            byte[] glb = BasisGlbTestBuilder.Triangle().BuildGlb();
            Assert.That(BasisModelPickupManager.SpawnFromFiles(new[] { TempFile(".glb", glb), TempFile(".glb", glb) }), Is.EqualTo(2));
            var ids = new List<Guid>(BasisModelPickupManager.Models.Keys);

            Stopwatch clock = Stopwatch.StartNew();
            while (!BasisModelPickupManager.IsReplicable(ids[0]) || !BasisModelPickupManager.IsReplicable(ids[1]))
            {
                Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(2), "a drop failed to load");
                if (clock.Elapsed.TotalSeconds > BasisModelUnityTestSupport.TaskTimeoutSeconds)
                    Assert.Fail("The dropped models did not load within " + BasisModelUnityTestSupport.TaskTimeoutSeconds + " s.");
                BasisModelPickupManager.SimulateUpdate();
                yield return null;
            }
            _sink.Clear();

            // Destroyed with the manager told, as OnDestroy does in play mode...
            Assert.That(BasisModelPickupManager.TryGetModel(ids[0], out BasisModelPickupObject told), Is.True);
            BasisModelPickupManager.OnPickupDestroyed(told);
            UnityEngine.Object.DestroyImmediate(told.gameObject);
            // ...and untold: edit mode sends no OnDestroy, so the tick's sweep finds it gone.
            Assert.That(BasisModelPickupManager.TryGetModel(ids[1], out BasisModelPickupObject swept), Is.True);
            UnityEngine.Object.DestroyImmediate(swept.gameObject);
            BasisModelPickupManager.SimulateUpdate();

            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(0));
            // Each: the broadcast, then the copy to ourselves that the server cache evicts on.
            Assert.That(_sink.Count, Is.EqualTo(4));
            for (int i = 0; i < 2; i++)
            {
                Assert.That(_sink.Packets[2 * i], Is.EqualTo(IdMessage(BasisModelShareWire.OpDespawn, ids[i])));
                Assert.That(_sink.Recipients[2 * i], Is.Null);
                Assert.That(_sink.Packets[2 * i + 1], Is.EqualTo(IdMessage(BasisModelShareWire.OpDespawn, ids[i])));
                Assert.That(_sink.Recipients[2 * i + 1], Is.EqualTo(new[] { LocalPlayer }));
            }
        }

        [UnityTest]
        public IEnumerator TickBeginsTheUplinkFrameEvenWhenIdle()
        {
            // A frame nobody has begun yet, or the check below would pass on an earlier call.
            Stopwatch clock = Stopwatch.StartNew();
            while (BasisModelBandwidth.HasBegunFrame(Time.frameCount))
            {
                if (clock.Elapsed.TotalSeconds > 10d)
                    Assert.Fail("Time.frameCount did not advance in edit mode.");
                yield return null;
            }
            Assert.That(BasisModelPickupManager.ModelCount + BasisModelPickupManager.JobCount, Is.EqualTo(0), "idle");

            BasisModelPickupManager.SimulateUpdate();

            Assert.That(BasisModelBandwidth.HasBegunFrame(Time.frameCount), Is.True);
        }

        [Test]
        public void LeaveDuringValidationReleasesEachReservationOnce()
        {
            var validation = new TaskCompletionSource<BasisGlbValidationResult>();
            BasisModelPickupManager.StartReceiveValidation = (wire, limits, token) => validation.Task;

            Guid first = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(3, Spawn(first, 3, 1000), Reliable);
            BasisModelPickupManager.OnDirectNetworkMessage(3, Chunk(first, 1000), Reliable);
            Assert.That(BasisModelPickupManager.JobCount, Is.EqualTo(1), "the bytes moved to a job");
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(1000L));
            BasisModelPickupManager.SimulateUpdate();

            BasisModelPickupManager.HandleLocalPlayerLeft(null, null);
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0L));
            Assert.That(BasisModelPickupManager.JobCount, Is.EqualTo(1), "the dead job waits for its task");

            // Rejoined: a new transfer reserves, then the old validation lands.
            Guid second = Guid.NewGuid();
            BasisModelPickupManager.OnDirectNetworkMessage(4, Spawn(second, 4, 2000), Reliable);
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(2000L));
            validation.SetResult(new BasisGlbValidationResult { Ok = false, Error = "late" });
            BasisModelPickupManager.SimulateUpdate();

            Assert.That(BasisModelPickupManager.JobCount, Is.EqualTo(0));
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(2000L));
            Assert.That(BasisModelPickupManager.TryGetModel(second, out _), Is.True);
            Assert.That(BasisModelPickupManager.TryGetModel(first, out _), Is.False);
        }

        /// <summary>The cache client gets our id and a viewer standing on the offers, so every offer is in range.</summary>
        private void UseServerCache()
        {
            _restoreCacheSeams = true;
            BasisModelCoreTestData.SamplePose().ToUnity(out Vector3 offerPosition, out _);
            BasisModelPickupManager.ServerCache.LocalId = (out ushort id) =>
            {
                id = LocalPlayer;
                return true;
            };
            BasisModelPickupManager.ServerCache.Viewer = (out Vector3 position) =>
            {
                position = offerPosition;
                return true;
            };
        }

        /// <summary>One offer range pass now, whatever the clock says.</summary>
        private static void RunOfferPass()
        {
            BasisModelPickupManager.ServerCache.ResetTimers();
            BasisModelPickupManager.SimulateUpdate();
        }

        private List<Guid> RequestedIds()
        {
            var ids = new List<Guid>();
            for (int i = 0; i < _sink.Count; i++)
            {
                byte[] packet = _sink.Packets[i];
                if (packet[0] == BasisModelShareWire.OpServerCacheRequest && BasisModelShareWire.TryReadIdMessage(packet, out Guid id))
                    ids.Add(id);
            }
            return ids;
        }

        /// <summary>A server-cache offer: the sharer's spawn with the opcode swapped. Only the server stamps one with our id.</summary>
        private static byte[] Offer(Guid id, ushort ownerId)
        {
            byte[] message = Spawn(id, ownerId, 1000);
            message[0] = BasisModelShareWire.OpServerCacheOffer;
            return message;
        }

        private static byte[] Spawn(Guid id, ushort ownerId, int totalBytes)
        {
            return BasisModelWire.EncodeSpawn(
                id,
                ownerId,
                "Owner",
                totalBytes,
                BasisModelShareWire.ExpectedChunkCount(totalBytes, BasisModelWire.ChunkPayloadBytes),
                BasisModelCoreTestData.SamplePose(),
                BasisModelCoreTestData.SmallTail()
            );
        }

        /// <summary>The whole model in one chunk (callers keep it under the chunk size).</summary>
        private static byte[] Chunk(Guid id, int totalBytes)
        {
            var payload = new byte[totalBytes];
            var packet = new byte[BasisModelShareWire.ChunkHeaderBytes + totalBytes];
            BasisModelShareWire.WriteChunk(packet, id, 0, payload, 0, totalBytes);
            return packet;
        }

        private static byte[] IdMessage(byte opcode, Guid id)
        {
            var message = new byte[BasisModelShareWire.IdMessageBytes];
            BasisModelShareWire.WriteIdMessage(message, opcode, id);
            return message;
        }

        private static byte[] Hello(bool reply)
        {
            var message = new byte[BasisModelWire.HelloBytes];
            BasisModelWire.WriteHello(message, reply);
            return message;
        }

        private static Guid SingleModelId()
        {
            Assert.That(BasisModelPickupManager.ModelCount, Is.EqualTo(1));
            foreach (Guid id in BasisModelPickupManager.Models.Keys)
                return id;
            return Guid.Empty;
        }

        /// <summary>A file the drop path will accept by name; without <paramref name="contents"/> it is never ticked far enough to read.</summary>
        private string TempFile(string extension, byte[] contents = null)
        {
            string path = Path.Combine(Path.GetTempPath(), "BasisModelPickupTest_" + Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(path, contents ?? new byte[] { 0x67, 0x6C, 0x54, 0x46 });
            _tempFiles.Add(path);
            return path;
        }

        /// <summary>Records every send, copying the buffer: the manager reuses its scratch buffers.</summary>
        private sealed class RecordingSink : IBasisModelPacketSink
        {
            public readonly List<byte[]> Packets = new List<byte[]>();
            public readonly List<ushort[]> Recipients = new List<ushort[]>();

            public int Count => Packets.Count;

            public void Send(byte[] buffer, DeliveryMethod deliveryMethod, ushort[] recipients)
            {
                Packets.Add((byte[])buffer.Clone());
                Recipients.Add(recipients != null ? (ushort[])recipients.Clone() : null);
            }

            public void Clear()
            {
                Packets.Clear();
                Recipients.Clear();
            }
        }

        /// <summary>No menu in edit mode: every batch is decided as Fit at once.</summary>
        private sealed class UnavailablePrompt : IBasisModelSizePrompt
        {
            public bool TryShow(string title, string description, string fitLabel, string originalLabel,
                Action<BasisModelDialogChoice> onResolved, out int token)
            {
                token = 0;
                return false;
            }

            public void Close(int token)
            {
            }
        }
    }
}
