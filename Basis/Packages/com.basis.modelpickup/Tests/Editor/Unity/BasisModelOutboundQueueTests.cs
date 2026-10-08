using System;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelOutboundQueueTests
    {
        private const byte HeaderOpcode = 0xA1;

        private sealed class TestTransfer : BasisModelOutboundTransfer
        {
            public int Tag;
        }

        /// <summary>A header carrying the pose it was encoded with, so a test can see when it was read.</summary>
        private sealed class TestSource : IBasisModelOutboundSource<TestTransfer>
        {
            public Transform Root;
            public bool Live = true;
            public Vector3 EncodedPosition;
            public int EncodedTotalChunks;
            public int HeadersEncoded;

            public bool TryGetLiveRoot(TestTransfer transfer, out Transform liveRoot)
            {
                liveRoot = Live ? Root : null;
                return Live;
            }

            public byte[] EncodeHeader(TestTransfer transfer, int totalChunks)
            {
                HeadersEncoded++;
                EncodedPosition = transfer.Position;
                EncodedTotalChunks = totalChunks;
                return new[] { HeaderOpcode, (byte)transfer.Tag };
            }
        }

        private GameObject _root;
        private TestSource _source;
        private RecordingPacketSink _sink;

        [SetUp]
        public void SetUp()
        {
            BasisModelUplink.ResetForNewConnection();
            _root = new GameObject("OutboundQueueTestRoot");
            _source = new TestSource { Root = _root.transform };
            _sink = new RecordingPacketSink();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                UnityEngine.Object.DestroyImmediate(_root);
            BasisModelUplink.ResetForNewConnection();
        }

        private static TestTransfer Transfer(int payloadBytes, int chunkBytes, params ushort[] recipients)
        {
            var payload = new byte[payloadBytes];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i * 7 + 3);
            return new TestTransfer
            {
                Id = BasisModelShareTestIds.Make(payloadBytes),
                Payload = payload,
                ChunkPayloadBytes = chunkBytes,
                Recipients = recipients,
                Tag = payloadBytes,
            };
        }

        /// <summary>Pushes the uplink into deficit so every metered send this frame is refused.</summary>
        private static void DrainUplink()
        {
            BasisModelBandwidth.TryConsume(1 << 24, 1, 0);
            Assert.That(BasisModelBandwidth.UplinkTokens, Is.LessThanOrEqualTo(0d));
        }

        [Test]
        public void HeaderGoesOutFirstUnmeteredThenChunksThenAFinalTransform()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            TestTransfer transfer = Transfer(10, 4, 60001);
            Assert.That(queue.Enqueue(transfer), Is.True);
            _root.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);

            queue.Process(16, _sink, _source);

            Assert.That(_sink.Count, Is.EqualTo(1 + 3 + 1));
            Assert.That(_sink.Packets[0][0], Is.EqualTo(HeaderOpcode));
            Assert.That(_source.EncodedTotalChunks, Is.EqualTo(3));
            for (int chunk = 0; chunk < 3; chunk++)
            {
                byte[] packet = _sink.Packets[1 + chunk];
                Assert.That(BasisModelShareWire.TryReadChunkHeader(packet, out Guid id, out int index, out int length), Is.True);
                Assert.That(packet[0], Is.EqualTo(BasisModelShareWire.OpChunk));
                Assert.That(id, Is.EqualTo(transfer.Id));
                Assert.That(index, Is.EqualTo(chunk));
                Assert.That(length, Is.EqualTo(chunk < 2 ? 4 : 2), "the last chunk is the short remainder");
                for (int b = 0; b < length; b++)
                    Assert.That(packet[BasisModelShareWire.ChunkHeaderBytes + b], Is.EqualTo(transfer.Payload[chunk * 4 + b]));
            }
            byte[] final = _sink.Packets[4];
            Assert.That(final.Length, Is.EqualTo(BasisModelShareWire.TransformBytes));
            Assert.That(BasisModelShareWire.TryReadTransform(final, out Guid finalId, out _, out float scale), Is.True);
            Assert.That(finalId, Is.EqualTo(transfer.Id));
            Assert.That(scale, Is.EqualTo(2.5f), "the final transform carries the root's uniform scale");
            Assert.That(_sink.Recipients[4], Is.EqualTo(new ushort[] { 60001 }));
            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(transfer.Rate.MovedBytes, Is.EqualTo(10));
        }

        [Test]
        public void HeaderPoseIsReadWhenSentNotWhenQueued()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            TestTransfer transfer = Transfer(4, 4, 60001);
            transfer.Position = Vector3.zero;
            queue.Enqueue(transfer);

            _root.transform.position = new Vector3(1f, 2f, 3f);
            queue.Process(16, _sink, _source);

            Assert.That(_source.EncodedPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [Test]
        public void StaleTransfersAreDroppedSilently()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            queue.Enqueue(Transfer(10, 4, 60001));
            _source.Live = false;

            queue.Process(16, _sink, _source);

            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(_source.HeadersEncoded, Is.EqualTo(0));
        }

        [Test]
        public void AnEmptyBucketStopsOutboundWorkForTheFrame()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            queue.Enqueue(Transfer(10, 4, 60001));
            queue.Enqueue(Transfer(6, 4, 60002));
            DrainUplink();

            queue.Process(16, _sink, _source);

            Assert.That(_sink.Count, Is.EqualTo(1), "only the unmetered header of the head transfer");
            Assert.That(_sink.Packets[0][0], Is.EqualTo(HeaderOpcode));
            Assert.That(queue.Count, Is.EqualTo(2));

            BasisModelBandwidth.Reset();
            queue.Process(16, _sink, _source);

            Assert.That(_source.HeadersEncoded, Is.EqualTo(2), "the head's header is not sent twice");
            Assert.That(_sink.CountOpcode(BasisModelShareWire.OpChunk), Is.EqualTo(3 + 2));
            Assert.That(_sink.CountOpcode(BasisModelShareWire.OpTransform), Is.EqualTo(2));
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void TheChunkBudgetCapsPacketsPerFrame()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            queue.Enqueue(Transfer(20, 4, 60001));

            queue.Process(2, _sink, _source);
            Assert.That(_sink.CountOpcode(BasisModelShareWire.OpChunk), Is.EqualTo(2));
            Assert.That(queue.Count, Is.EqualTo(1));

            queue.Process(2, _sink, _source);
            queue.Process(2, _sink, _source);
            Assert.That(_sink.CountOpcode(BasisModelShareWire.OpChunk), Is.EqualTo(5));
            Assert.That(_sink.CountOpcode(BasisModelShareWire.OpTransform), Is.EqualTo(1));
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void RemovingTheLastRecipientDropsTheTransfer()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            TestTransfer shared = Transfer(10, 4, 60001, 60002);
            queue.Enqueue(shared);
            queue.Enqueue(Transfer(6, 4, 60001));

            queue.RemoveRecipient(60001);

            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(shared.Recipients, Is.EqualTo(new ushort[] { 60002 }));
        }

        [Test]
        public void HasPendingMatchesOnIdAndCohort()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            TestTransfer transfer = Transfer(10, 4, 60001, 60002);
            queue.Enqueue(transfer);

            Assert.That(queue.HasPending(transfer.Id, new ushort[] { 60001, 60002 }), Is.True);
            Assert.That(queue.HasPending(transfer.Id, new ushort[] { 60001 }), Is.False);
            Assert.That(queue.HasPending(BasisModelShareTestIds.Make(999), new ushort[] { 60001, 60002 }), Is.False);

            queue.Remove(transfer.Id);
            Assert.That(queue.HasPending(transfer.Id, new ushort[] { 60001, 60002 }), Is.False);
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void EnqueueRefusesTransfersWithNothingToSend()
        {
            var queue = new BasisModelOutboundQueue<TestTransfer>();
            Assert.That(queue.Enqueue(null), Is.False);
            Assert.That(queue.Enqueue(Transfer(0, 4, 60001)), Is.False);
            Assert.That(queue.Enqueue(Transfer(4, 4)), Is.False);
            Assert.That(queue.Enqueue(Transfer(4, 0, 60001)), Is.False);
            Assert.That(queue.Count, Is.EqualTo(0));
        }
    }
}
