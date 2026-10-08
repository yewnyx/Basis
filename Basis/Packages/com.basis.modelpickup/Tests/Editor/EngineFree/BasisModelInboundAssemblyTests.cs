using System;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelInboundAssemblyTests
    {
        private const ushort Owner = 7;
        private const int ChunkSize = 64;

        /// <summary>Three full chunks and a ten-byte tail chunk.</summary>
        private const int TotalBytes = ChunkSize * 3 + 10;

        private const int TotalChunks = 4;
        private const float Timeout = 30f;

        private static readonly Guid Id = new Guid("11223344-5566-7788-99aa-bbccddeeff00");

        private byte[] _source;

        [SetUp]
        public void SetUp()
        {
            _source = new byte[TotalBytes];
            for (int i = 0; i < _source.Length; i++)
                _source[i] = (byte)(i * 13 + 1);
            BasisModelInboundReservations.ReleaseAll();
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelInboundReservations.ReleaseAll();
        }

        private static BasisModelInboundAssembly NewTransfer(float now = 100f)
        {
            var transfer = new BasisModelInboundAssembly();
            transfer.Initialize(Owner, Id, TotalBytes, TotalChunks, ChunkSize, now, Timeout);
            return transfer;
        }

        private byte[] Packet(int index)
        {
            int offset = index * ChunkSize;
            int length = Math.Min(ChunkSize, TotalBytes - offset);
            byte[] packet = new byte[BasisModelShareWire.ChunkHeaderBytes + length];
            BasisModelShareWire.WriteChunk(packet, Id, index, _source, offset, length);
            return packet;
        }

        private BasisModelChunkStatus Offer(BasisModelInboundAssembly transfer, int index, float now, ushort sender = Owner)
        {
            byte[] packet = Packet(index);
            BasisModelShareWire.TryReadChunkHeader(packet, out _, out int chunkIndex, out int length);
            return transfer.Accept(sender, packet, chunkIndex, length, now, Timeout);
        }

        [Test]
        public void AChunkFromAnotherSenderIsRejected()
        {
            BasisModelInboundAssembly transfer = NewTransfer();

            Assert.That(Offer(transfer, 0, 101f, 8), Is.EqualTo(BasisModelChunkStatus.WrongSender));
            // The sender check comes first, so even an otherwise invalid chunk reports the sender.
            Assert.That(transfer.Accept(8, Packet(0), -1, 0, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.WrongSender));
            Assert.That(transfer.ReceivedCount, Is.EqualTo(0));
        }

        [Test]
        public void IndicesOutsideTheTransferAreRejected()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            byte[] packet = Packet(0);

            Assert.That(transfer.Accept(Owner, packet, -1, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.IndexOutOfRange));
            Assert.That(transfer.Accept(Owner, packet, TotalChunks, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.IndexOutOfRange));
            Assert.That(transfer.Accept(Owner, packet, int.MaxValue, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.IndexOutOfRange));
        }

        [Test]
        public void ImpossibleLengthsAreRejected()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            byte[] packet = Packet(0);

            Assert.That(transfer.Accept(Owner, packet, 0, 0, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.ImpossibleLength));
            Assert.That(transfer.Accept(Owner, packet, 0, -1, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.ImpossibleLength));
            Assert.That(transfer.Accept(Owner, packet, 0, ChunkSize + 1, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.ImpossibleLength));
        }

        [Test]
        public void AnOffsetPastTheEndIsRejected()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            // Only reachable if a subclass widens the chunk table; the check still has to hold.
            transfer.TotalChunks = TotalChunks + 1;
            transfer.Received = new bool[TotalChunks + 1];

            Assert.That(transfer.Accept(Owner, Packet(0), TotalChunks, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.OffsetOutOfRange));
        }

        [Test]
        public void OnlyTheExactExpectedLengthIsAcceptedIncludingTheShortLastChunk()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            byte[] full = new byte[BasisModelShareWire.ChunkHeaderBytes + ChunkSize];

            Assert.That(transfer.Accept(Owner, full, 0, ChunkSize - 1, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.LengthMismatch));
            Assert.That(transfer.Accept(Owner, full, 3, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.LengthMismatch));
            Assert.That(transfer.Accept(Owner, full, 3, 9, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.LengthMismatch));
            Assert.That(Offer(transfer, 3, 101f), Is.EqualTo(BasisModelChunkStatus.Accepted));
            Assert.That(Offer(transfer, 0, 101f), Is.EqualTo(BasisModelChunkStatus.Accepted));
        }

        [Test]
        public void TruncatedPacketsAreRejected()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            byte[] packet = Packet(0);
            byte[] cut = new byte[packet.Length - 1];
            Buffer.BlockCopy(packet, 0, cut, 0, cut.Length);

            Assert.That(transfer.Accept(Owner, cut, 0, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.Truncated));
            Assert.That(transfer.Accept(Owner, null, 0, ChunkSize, 101f, Timeout), Is.EqualTo(BasisModelChunkStatus.Truncated));
            Assert.That(transfer.ReceivedCount, Is.EqualTo(0));
        }

        [Test]
        public void RejectedChunksChangeNothing()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            float deadline = transfer.Deadline;

            Offer(transfer, 0, 110f, 8);
            transfer.Accept(Owner, Packet(0), 9, ChunkSize, 110f, Timeout);
            transfer.Accept(Owner, Packet(0), 0, 1, 110f, Timeout);

            Assert.That(transfer.Buffer, Is.Null);
            Assert.That(transfer.ReceivedCount, Is.EqualTo(0));
            Assert.That(transfer.Deadline, Is.EqualTo(deadline));
            Assert.That(transfer.LastProgressTime, Is.EqualTo(100f));
            Assert.That(transfer.Rate.MovedBytes, Is.EqualTo(0));
        }

        [Test]
        public void DuplicatesRefreshNothing()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            Assert.That(Offer(transfer, 1, 110f), Is.EqualTo(BasisModelChunkStatus.Accepted));
            float deadline = transfer.Deadline;

            Assert.That(Offer(transfer, 1, 120f), Is.EqualTo(BasisModelChunkStatus.Duplicate));

            Assert.That(transfer.Deadline, Is.EqualTo(deadline));
            Assert.That(transfer.LastProgressTime, Is.EqualTo(110f));
            Assert.That(transfer.ReceivedCount, Is.EqualTo(1));
            Assert.That(transfer.Rate.MovedBytes, Is.EqualTo(ChunkSize));
        }

        [Test]
        public void NewChunksRefreshTheDeadline()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            Assert.That(transfer.Deadline, Is.EqualTo(100f + Timeout));

            Offer(transfer, 0, 110f);
            Assert.That(transfer.Deadline, Is.EqualTo(110f + Timeout));
            Assert.That(transfer.LastProgressTime, Is.EqualTo(110f));

            Offer(transfer, 2, 125f);
            Assert.That(transfer.Deadline, Is.EqualTo(125f + Timeout));
        }

        [Test]
        public void CompletesWhenEveryChunkArrivesInAnyOrder()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            int[] order = { 3, 1, 0, 2 };
            for (int i = 0; i < order.Length; i++)
            {
                Assert.That(transfer.IsComplete, Is.False);
                Assert.That(Offer(transfer, order[i], 101f + i), Is.EqualTo(BasisModelChunkStatus.Accepted));
            }

            Assert.That(transfer.IsComplete, Is.True);
            Assert.That(transfer.Buffer, Is.EqualTo(_source));
            Assert.That(transfer.Rate.MovedBytes, Is.EqualTo(TotalBytes));
        }

        [Test]
        public void BufferIsAllocatedOnFirstAcceptedChunk()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            Assert.That(transfer.Buffer, Is.Null);
            Assert.That(transfer.TotalBytes, Is.EqualTo(TotalBytes));

            Offer(transfer, 2, 101f);

            Assert.That(transfer.Buffer, Is.Not.Null);
            Assert.That(transfer.Buffer.Length, Is.EqualTo(TotalBytes));
        }

        [Test]
        public void StallIsReportedOnceThenExpiryWins()
        {
            BasisModelInboundAssembly transfer = NewTransfer();
            Offer(transfer, 0, 101f);

            Assert.That(transfer.CheckExpiry(105.5f, 5f), Is.EqualTo(BasisModelTransferHealth.Active));
            Assert.That(transfer.CheckExpiry(106f, 5f), Is.EqualTo(BasisModelTransferHealth.StallWarning));
            Assert.That(transfer.StallLogged, Is.True);
            Assert.That(transfer.CheckExpiry(107f, 5f), Is.EqualTo(BasisModelTransferHealth.Active));
            Assert.That(transfer.CheckExpiry(130.5f, 5f), Is.EqualTo(BasisModelTransferHealth.Active));
            Assert.That(transfer.CheckExpiry(131f, 5f), Is.EqualTo(BasisModelTransferHealth.Expired));
        }

        [Test]
        public void ATransferWithNoChunksExpiresAtTheZeroChunkTimeout()
        {
            BasisModelInboundAssembly quiet = NewTransfer();
            quiet.ZeroChunkTimeoutSeconds = 10f;
            Assert.That(quiet.CheckExpiry(109.5f, 60f), Is.EqualTo(BasisModelTransferHealth.Active));
            Assert.That(quiet.CheckExpiry(110f, 60f), Is.EqualTo(BasisModelTransferHealth.Expired));

            // Once anything has arrived only the ordinary deadline applies.
            BasisModelInboundAssembly started = NewTransfer();
            started.ZeroChunkTimeoutSeconds = 10f;
            Offer(started, 0, 105f);
            Assert.That(started.CheckExpiry(120f, 60f), Is.EqualTo(BasisModelTransferHealth.Active));
            Assert.That(started.CheckExpiry(135f, 60f), Is.EqualTo(BasisModelTransferHealth.Expired));
        }

        [Test]
        public void TheDefaultZeroChunkTimeoutCoincidesWithTheImageDeadline()
        {
            BasisModelInboundAssembly transfer = NewTransfer();

            Assert.That(transfer.ZeroChunkTimeoutSeconds, Is.EqualTo(30f));
            Assert.That(transfer.StartTime + transfer.ZeroChunkTimeoutSeconds, Is.EqualTo(transfer.Deadline));
            Assert.That(transfer.CheckExpiry(129.9f, 60f), Is.EqualTo(BasisModelTransferHealth.Active));
            Assert.That(transfer.CheckExpiry(130f, 60f), Is.EqualTo(BasisModelTransferHealth.Expired));
        }

        [Test]
        public void RejectionTextMatchesTheImageWordingExactly()
        {
            BasisModelInboundAssembly transfer = NewTransfer();

            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.WrongSender, 9, 0, ChunkSize, 89, "image"),
                Is.EqualTo("it came from 9, not the owner")
            );
            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.IndexOutOfRange, Owner, 4, ChunkSize, 89, "image"),
                Is.EqualTo("the index is outside 0..3")
            );
            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.ImpossibleLength, Owner, 0, 65, 90, "image"),
                Is.EqualTo("it claims an impossible length of 65")
            );
            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.OffsetOutOfRange, Owner, 5, ChunkSize, 89, "image"),
                Is.EqualTo("offset 320 falls outside the 202-byte image")
            );
            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.LengthMismatch, Owner, 3, ChunkSize, 89, "image"),
                Is.EqualTo("it claims 64 bytes where 10 were expected")
            );
            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.Truncated, Owner, 3, 10, 30, "image"),
                Is.EqualTo("the packet carries 5 of the 10 bytes it claims")
            );
            Assert.That(
                transfer.DescribeRejection(BasisModelChunkStatus.OffsetOutOfRange, Owner, 5, ChunkSize, 89, "model"),
                Is.EqualTo("offset 320 falls outside the 202-byte model")
            );
            Assert.That(transfer.DescribeRejection(BasisModelChunkStatus.Accepted, Owner, 0, ChunkSize, 89, "image"), Is.Empty);
        }

        [Test]
        public void InitializeRefusesAMismatchedChunkCount()
        {
            var transfer = new BasisModelInboundAssembly();

            Assert.Throws<ArgumentOutOfRangeException>(() => transfer.Initialize(Owner, Id, TotalBytes, TotalChunks - 1, ChunkSize, 0f, Timeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => transfer.Initialize(Owner, Id, TotalBytes, TotalChunks + 1, ChunkSize, 0f, Timeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => transfer.Initialize(Owner, Id, 0, 0, ChunkSize, 0f, Timeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => transfer.Initialize(Owner, Id, TotalBytes, TotalChunks, 0, 0f, Timeout));
        }

        [Test]
        public void InitializeLeavesTheReservationAndZeroChunkTimeoutAlone()
        {
            var transfer = new BasisModelInboundAssembly { ReservedBytes = 55, ZeroChunkTimeoutSeconds = 10f };

            transfer.Initialize(Owner, Id, TotalBytes, TotalChunks, ChunkSize, 3f, Timeout);

            Assert.That(transfer.ReservedBytes, Is.EqualTo(55));
            Assert.That(transfer.ZeroChunkTimeoutSeconds, Is.EqualTo(10f));
            Assert.That(transfer.StartTime, Is.EqualTo(3f));
            transfer.ReservedBytes = 0;
        }

        [Test]
        public void ReleaseReservationIsIdempotent()
        {
            long before = BasisModelInboundReservations.Reserved;
            Assert.That(BasisModelInboundReservations.TryReserve(TotalBytes, long.MaxValue, out _), Is.True);
            BasisModelInboundAssembly transfer = NewTransfer();
            transfer.ReservedBytes = TotalBytes;

            transfer.ReleaseReservation();
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(before));
            Assert.That(transfer.ReservedBytes, Is.EqualTo(0));

            // A second holder must not lose its bytes to a repeated release.
            Assert.That(BasisModelInboundReservations.TryReserve(1000, long.MaxValue, out _), Is.True);
            transfer.ReleaseReservation();
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(before + 1000));
        }
    }
}
