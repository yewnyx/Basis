using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelServerCacheClientTests
    {
        private const ushort OurId = 7;
        private const ushort SomeoneElse = 8;
        private const float RangeMeters = 10f;
        private const float Interval = 0.5f;

        private RecordingPacketSink _sink;
        private HashSet<Guid> _known;
        private Vector3 _viewer;
        private float _now;
        private readonly List<IDisposable> _clients = new List<IDisposable>();

        private sealed class ClientLease : IDisposable
        {
            public BasisModelServerCacheClient Client;

            public void Dispose()
            {
                Client.ReleaseResources();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingPacketSink();
            _known = new HashSet<Guid>();
            _viewer = Vector3.zero;
            _now = 100f;
        }

        [TearDown]
        public void TearDown()
        {
            // Persistent native buffers would otherwise surface as leak errors in a later test.
            for (int i = 0; i < _clients.Count; i++)
                _clients[i].Dispose();
            _clients.Clear();
        }

        private BasisModelServerCacheClient Make(int maxPendingOffers = 0)
        {
            var range = new BasisModelRangeReporter("Test pickup", "test", BasisDebug.LogTag.Pickups)
            {
                AdvertisedRange = () => RangeMeters,
            };
            var client = new BasisModelServerCacheClient(
                _sink,
                range,
                id => _known.Contains(id),
                Interval,
                maxPendingOffers,
                "Test pickup",
                "test(s)",
                BasisDebug.LogTag.Pickups
            )
            {
                LocalId = (out ushort id) =>
                {
                    id = OurId;
                    return true;
                },
                Viewer = (out Vector3 position) =>
                {
                    position = _viewer;
                    return true;
                },
            };
            _clients.Add(new ClientLease { Client = client });
            return client;
        }

        private static byte[] Offer(Guid id, Vector3 position, int tailValue = 0)
        {
            var tail = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tail, tailValue);
            return BasisModelShareWire.EncodeSpawn(
                BasisModelShareWire.OpServerCacheOffer,
                id,
                3,
                "Owner",
                1,
                2,
                100,
                1,
                BasisModelShareConvert.ToSharePose(position, Quaternion.identity),
                tail
            );
        }

        private static byte[] State(Guid id, bool held)
        {
            var message = new byte[BasisModelShareWire.CacheStateBytes];
            BasisModelShareWire.WriteCacheState(message, id, held);
            return message;
        }

        private void RunRangeCheck(BasisModelServerCacheClient client)
        {
            _now += Interval;
            client.ScheduleRangeCheck(_now);
            client.CompleteRangeCheck();
        }

        private List<Guid> RequestedIds()
        {
            var ids = new List<Guid>();
            for (int i = 0; i < _sink.Count; i++)
            {
                if (_sink.Packets[i][0] == BasisModelShareWire.OpServerCacheRequest)
                {
                    BasisModelShareWire.TryReadIdMessage(_sink.Packets[i], out Guid id);
                    ids.Add(id);
                }
            }
            return ids;
        }

        [Test]
        public void StateIsAcceptedOnlyUnderOurOwnId()
        {
            BasisModelServerCacheClient client = Make();
            Guid id = BasisModelShareTestIds.Make(1);

            client.HandleState(SomeoneElse, State(id, true));
            Assert.That(client.IsHeld(id), Is.False, "another client cannot claim the server holds our item");

            client.HandleState(OurId, State(id, true));
            Assert.That(client.IsHeld(id), Is.True);

            client.HandleState(OurId, State(id, false));
            Assert.That(client.IsHeld(id), Is.False);

            client.HandleState(OurId, new byte[5]);
            Assert.That(client.IsHeld(id), Is.False);
        }

        [Test]
        public void OffersAreAcceptedOnlyUnderOurOwnId()
        {
            BasisModelServerCacheClient client = Make();
            client.HandleOffer(SomeoneElse, Offer(BasisModelShareTestIds.Make(1), Vector3.zero));
            Assert.That(client.PendingOfferCount, Is.EqualTo(0));

            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(1), Vector3.zero));
            Assert.That(client.PendingOfferCount, Is.EqualTo(1));
        }

        [Test]
        public void OffersAreIgnoredForItemsWeAlreadyKnow()
        {
            BasisModelServerCacheClient client = Make();
            Guid id = BasisModelShareTestIds.Make(1);
            _known.Add(id);

            client.HandleOffer(OurId, Offer(id, Vector3.zero));

            Assert.That(client.PendingOfferCount, Is.EqualTo(0));
        }

        [Test]
        public void TruncatedAndBadPoseOffersKeepNothing()
        {
            BasisModelServerCacheClient client = Make();
            byte[] offer = Offer(BasisModelShareTestIds.Make(1), Vector3.zero);
            client.HandleOffer(OurId, offer.AsSpan(0, 30).ToArray());
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(2), new Vector3(float.NaN, 0f, 0f)));
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(3), new Vector3(0f, 1e9f, 0f)));

            Assert.That(client.PendingOfferCount, Is.EqualTo(0));
        }

        [Test]
        public void OfferPositionsFollowBroadcastTransforms()
        {
            BasisModelServerCacheClient client = Make();
            Guid id = BasisModelShareTestIds.Make(1);
            client.HandleOffer(OurId, Offer(id, new Vector3(50f, 0f, 0f)));

            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.Empty);

            client.OnTransform(id, new Vector3(5f, 0f, 0f));
            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.EqualTo(new[] { id }));
        }

        [Test]
        public void InRangeOffersAreRequestedOnceAndOutOfRangeOnesWait()
        {
            BasisModelServerCacheClient client = Make();
            Guid near = BasisModelShareTestIds.Make(1);
            Guid far = BasisModelShareTestIds.Make(2);
            client.HandleOffer(OurId, Offer(near, new Vector3(0f, 0f, RangeMeters)));
            client.HandleOffer(OurId, Offer(far, new Vector3(0f, 0f, RangeMeters + 1f)));

            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.EqualTo(new[] { near }), "the range is inclusive");
            Assert.That(client.PendingOfferCount, Is.EqualTo(1));

            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.EqualTo(new[] { near }), "nothing is requested twice");

            _viewer = new Vector3(0f, 0f, 2f);
            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.EqualTo(new[] { near, far }));
            Assert.That(client.PendingOfferCount, Is.EqualTo(0));
        }

        [Test]
        public void ChecksWaitForTheirInterval()
        {
            BasisModelServerCacheClient client = Make();
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(1), new Vector3(50f, 0f, 0f)));
            RunRangeCheck(client);

            Guid id = BasisModelShareTestIds.Make(2);
            client.HandleOffer(OurId, Offer(id, Vector3.zero));
            client.ScheduleRangeCheck(_now + Interval * 0.5f);
            client.CompleteRangeCheck();
            Assert.That(RequestedIds(), Is.Empty);

            client.ResetTimers();
            client.ScheduleRangeCheck(_now + Interval * 0.5f);
            client.CompleteRangeCheck();
            Assert.That(RequestedIds(), Is.EqualTo(new[] { id }));
        }

        [Test]
        public void RequestsAreAddressedToOurselvesAlone()
        {
            BasisModelServerCacheClient client = Make();
            Guid id = BasisModelShareTestIds.Make(1);
            client.HandleOffer(OurId, Offer(id, Vector3.zero));

            RunRangeCheck(client);

            Assert.That(_sink.Count, Is.EqualTo(1));
            Assert.That(_sink.Packets[0].Length, Is.EqualTo(BasisModelShareWire.IdMessageBytes));
            Assert.That(_sink.Packets[0][0], Is.EqualTo(BasisModelShareWire.OpServerCacheRequest));
            Assert.That(_sink.Recipients[0], Is.EqualTo(new[] { OurId }));
        }

        [Test]
        public void CanRequestHoldsOffersBack()
        {
            BasisModelServerCacheClient client = Make();
            bool allowed = false;
            client.CanRequest = (id, offer) => allowed;
            Guid id = BasisModelShareTestIds.Make(1);
            client.HandleOffer(OurId, Offer(id, Vector3.zero));

            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.Empty);
            Assert.That(client.PendingOfferCount, Is.EqualTo(1), "a refused offer stays pending rather than being lost");

            allowed = true;
            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.EqualTo(new[] { id }));
        }

        [Test]
        public void OfferParserRejectionKeepsNothing()
        {
            BasisModelServerCacheClient client = Make();
            client.ParseOffer = (in BasisModelSpawnHeader header, ReadOnlySpan<byte> message, out BasisModelOfferInfo offer) =>
            {
                offer = default;
                return false;
            };

            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(1), Vector3.zero));

            Assert.That(client.PendingOfferCount, Is.EqualTo(0));
        }

        [Test]
        public void CanRequestSeesTheParsedOffer()
        {
            BasisModelServerCacheClient client = Make();
            client.ParseOffer = (in BasisModelSpawnHeader header, ReadOnlySpan<byte> message, out BasisModelOfferInfo offer) =>
            {
                offer = new BasisModelOfferInfo
                {
                    TotalBytes = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(header.TailOffset, header.TailLength)),
                };
                return true;
            };
            var seen = new List<int>();
            client.CanRequest = (id, offer) =>
            {
                seen.Add(offer.TotalBytes);
                return true;
            };
            Guid id = BasisModelShareTestIds.Make(1);

            client.HandleOffer(OurId, Offer(id, Vector3.zero, 4242));
            Assert.That(client.TryGetOffer(id, out BasisModelOfferInfo stored), Is.True);
            Assert.That(stored.TotalBytes, Is.EqualTo(4242));

            RunRangeCheck(client);
            Assert.That(seen, Is.EqualTo(new[] { 4242 }));
            Assert.That(client.TryGetOffer(id, out _), Is.False);
        }

        [Test]
        public void MaxPendingOffersRefusesNewIdsOnly()
        {
            BasisModelServerCacheClient client = Make(2);
            Guid first = BasisModelShareTestIds.Make(1);
            client.HandleOffer(OurId, Offer(first, new Vector3(50f, 0f, 0f)));
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(2), new Vector3(50f, 0f, 0f)));
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(3), Vector3.zero));
            Assert.That(client.PendingOfferCount, Is.EqualTo(2));

            // A known id still updates in place while the cap is reached.
            client.HandleOffer(OurId, Offer(first, Vector3.zero));
            Assert.That(client.PendingOfferCount, Is.EqualTo(2));
            RunRangeCheck(client);
            Assert.That(RequestedIds(), Is.EqualTo(new[] { first }));

            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(3), Vector3.zero));
            Assert.That(client.PendingOfferCount, Is.EqualTo(2), "a freed slot is usable again");
        }

        [Test]
        public void RequestedReplaysAreFlaggedUntilForgotten()
        {
            BasisModelServerCacheClient client = Make();
            Guid arrives = BasisModelShareTestIds.Make(1);
            Guid vanishes = BasisModelShareTestIds.Make(2);
            client.HandleOffer(OurId, Offer(arrives, Vector3.zero));
            client.HandleOffer(OurId, Offer(vanishes, Vector3.zero));
            Assert.That(client.WasRequested(arrives), Is.False);

            RunRangeCheck(client);
            Assert.That(client.WasRequested(arrives), Is.True);
            Assert.That(client.WasRequested(vanishes), Is.True);

            client.RemoveOffer(arrives);
            client.Forget(vanishes);
            Assert.That(client.WasRequested(arrives), Is.False);
            Assert.That(client.WasRequested(vanishes), Is.False);

            client.HandleOffer(OurId, Offer(arrives, Vector3.zero));
            RunRangeCheck(client);
            client.ClearOffers();
            Assert.That(client.WasRequested(arrives), Is.False);
        }

        [Test]
        public void ScheduleCompletesAnOutstandingHandle()
        {
            BasisModelServerCacheClient client = Make();
            Guid id = BasisModelShareTestIds.Make(1);
            client.HandleOffer(OurId, Offer(id, Vector3.zero));

            // A tick that threw between schedule and complete leaves the job outstanding; the next schedule
            // collects it before touching the buffers the job reads.
            _now += Interval;
            client.ScheduleRangeCheck(_now);
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(2), Vector3.zero));
            _now += Interval;
            Assert.DoesNotThrow(() => client.ScheduleRangeCheck(_now));
            client.CompleteRangeCheck();

            Assert.That(RequestedIds(), Has.Member(id));
            Assert.That(RequestedIds(), Has.Member(BasisModelShareTestIds.Make(2)));
            Assert.That(RequestedIds().Count, Is.EqualTo(2));
        }

        [Test]
        public void ReleaseResourcesIsIdempotent()
        {
            BasisModelServerCacheClient client = Make();
            client.HandleOffer(OurId, Offer(BasisModelShareTestIds.Make(1), new Vector3(50f, 0f, 0f)));
            _now += Interval;
            client.ScheduleRangeCheck(_now);

            Assert.DoesNotThrow(client.ReleaseResources);
            Assert.DoesNotThrow(client.ReleaseResources);
            Assert.DoesNotThrow(client.CompleteRangeCheck);

            client.OnTransform(BasisModelShareTestIds.Make(1), Vector3.zero);
            RunRangeCheck(client);
            Assert.That(RequestedIds().Count, Is.EqualTo(1), "the buffers come back on the next check");
        }

        [Test]
        public void ForgetAndClearHeldDropTheServersHolds()
        {
            BasisModelServerCacheClient client = Make();
            Guid first = BasisModelShareTestIds.Make(1);
            Guid second = BasisModelShareTestIds.Make(2);
            client.HandleState(OurId, State(first, true));
            client.HandleState(OurId, State(second, true));

            client.Forget(first);
            Assert.That(client.IsHeld(first), Is.False);
            Assert.That(client.IsHeld(second), Is.True);

            client.ClearHeld();
            Assert.That(client.IsHeld(second), Is.False);
        }

        [Test]
        public void TheOfferRangeJobTreatsZeroAsUnlimited()
        {
            var positions = new NativeArray<Vector3>(2, Allocator.TempJob);
            var results = new NativeArray<byte>(2, Allocator.TempJob);
            var distances = new NativeArray<float>(2, Allocator.TempJob);
            try
            {
                positions[0] = new Vector3(3f, 4f, 0f);
                positions[1] = new Vector3(1e6f, 0f, 0f);
                var job = new BasisModelOfferRangeJob
                {
                    Positions = positions,
                    Viewer = Vector3.zero,
                    InRange = results,
                    DistanceSquared = distances,
                };

                job.RangeSquared = 25f;
                job.Execute(0);
                job.Execute(1);
                Assert.That(results[0], Is.EqualTo((byte)1), "exactly on the radius is in range");
                Assert.That(results[1], Is.EqualTo((byte)0));
                Assert.That(distances[0], Is.EqualTo(25f), "the caller reads the distance instead of measuring again");
                Assert.That(distances[1], Is.EqualTo(1e12f));

                job.RangeSquared = 0f;
                job.Execute(1);
                Assert.That(results[1], Is.EqualTo((byte)1));
            }
            finally
            {
                positions.Dispose();
                results.Dispose();
                distances.Dispose();
            }
        }
    }
}
