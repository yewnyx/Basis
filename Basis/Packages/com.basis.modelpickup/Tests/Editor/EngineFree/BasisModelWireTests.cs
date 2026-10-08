using System;
using System.Buffers.Binary;
using Basis.ModelPickup.Validation;
using NUnit.Framework;
using static Basis.ModelPickup.Tests.BasisModelCoreTestData;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelWireTests
    {
        [Test]
        public void NetworkIdentifierIsPinned()
        {
            // The server's BasisNetworkModelCache.ModelManagerIdentifier pins the same string.
            Assert.That(BasisModelWire.FixedNetworkIdentifier, Is.EqualTo("BasisModelPickupManager"));
        }

        [Test]
        public void WireConstantsArePinned()
        {
            Assert.That(BasisModelWire.HeaderVersion, Is.EqualTo(1));
            Assert.That(BasisModelWire.TailV1Bytes, Is.EqualTo(96));
            Assert.That(BasisModelWire.SizeModeOffset, Is.EqualTo(0));
            Assert.That(BasisModelWire.BaseScaleOffset, Is.EqualTo(4));
            Assert.That(BasisModelWire.ClaimsOffset, Is.EqualTo(8));
            Assert.That(BasisModelWire.ClaimsOffset + BasisGlbClaims.EncodedSize, Is.EqualTo(BasisModelWire.TailV1Bytes));
            Assert.That(BasisModelWire.ChunkPayloadBytes, Is.EqualTo(16384));
            Assert.That(BasisModelWire.MaxV1SpawnBytes, Is.EqualTo(417));
            Assert.That(BasisModelWire.OpHello, Is.EqualTo(11));
            Assert.That(BasisModelWire.HelloVersion, Is.EqualTo(1));
            Assert.That(BasisModelWire.HelloBytes, Is.EqualTo(3));
        }

        [Test]
        public void TailV1LayoutIsPinnedByteForByte()
        {
            byte[] expected =
            {
                // sizeMode = Original, three reserved zeros, baseScale 0.75f.
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x3F,
                // BasisGlbClaims at tail offset 8 (its own layout is pinned by BasisGlbClaimsTests).
                0x01, 0x05,
                0x02, 0x01, 0x04, 0x03, 0x06, 0x05, 0x08, 0x07, 0x0A, 0x09,
                0x0E, 0x0D, 0x0C, 0x0B, 0x12, 0x11, 0x10, 0x0F, 0x16, 0x15, 0x14, 0x13,
                0x1E, 0x1D, 0x1C, 0x1B, 0x1A, 0x19, 0x18, 0x17,
                0x26, 0x25, 0x24, 0x23, 0x22, 0x21, 0x20, 0x1F,
                0x00, 0x00, 0x80, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F,
                0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x40, 0x40, 0x00, 0x00, 0x80, 0x40,
                0x2E, 0x2D, 0x2C, 0x2B, 0x2A, 0x29, 0x28, 0x27,
                0x36, 0x35, 0x34, 0x33, 0x32, 0x31, 0x30, 0x2F,
                0x38, 0x37, 0x3A, 0x39, 0x3C, 0x3B, 0x3E, 0x3D,
            };
            Assert.That(expected.Length, Is.EqualTo(96));
            var tail = new BasisModelSpawnTail { SizeMode = BasisModelSizeMode.Original, BaseScale = 0.75f, Claims = DistinctClaims() };

            // A dirty buffer: reserved bytes must be zeroed and nothing past 96 touched.
            var written = new byte[100];
            for (int i = 0; i < written.Length; i++)
                written[i] = 0xEE;
            BasisModelWire.WriteTail(written, tail);

            CollectionAssert.AreEqual(expected, new ArraySegment<byte>(written, 0, 96));
            CollectionAssert.AreEqual(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE }, new ArraySegment<byte>(written, 96, 4));
        }

        [Test]
        public void SpawnRoundTripsEveryTailField()
        {
            var tail = new BasisModelSpawnTail { SizeMode = BasisModelSizeMode.Original, BaseScale = 0.75f, Claims = DistinctClaims() };
            byte[] message = BasisModelWire.EncodeSpawn(SampleId, SampleOwnerId, "Zoë", 70000, 5, SamplePose(), tail);

            BasisModelSpawnHeader header = ReadHeader(message);
            Assert.That(header.Opcode, Is.EqualTo(BasisModelShareWire.OpSpawn));
            Assert.That(header.Id, Is.EqualTo(SampleId));
            Assert.That(header.OwnerId, Is.EqualTo(SampleOwnerId));
            Assert.That(header.FieldA, Is.EqualTo(BasisModelWire.HeaderVersion));
            Assert.That(header.FieldB, Is.EqualTo(BasisModelWire.TailV1Bytes));
            Assert.That(header.TotalBytes, Is.EqualTo(70000));
            Assert.That(header.TotalChunks, Is.EqualTo(5));
            Assert.That(header.TailLength, Is.EqualTo(96));

            Assert.That(BasisModelWire.TryReadTail(header, message, out BasisModelSpawnTail read, out string error), Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(read.SizeMode, Is.EqualTo(BasisModelSizeMode.Original));
            Assert.That(read.BaseScale, Is.EqualTo(0.75f));
            CollectionAssert.AreEqual(ClaimsBytes(tail.Claims), ClaimsBytes(read.Claims));
        }

        [Test]
        public void AV1SpawnIsAtMost417Bytes()
        {
            byte[] longest = BasisModelWire.EncodeSpawn(SampleId, 1, new string('x', 256), 1000, 1, SamplePose(), SmallTail());
            Assert.That(longest.Length, Is.EqualTo(BasisModelWire.MaxV1SpawnBytes));

            byte[] trimmed = BasisModelWire.EncodeSpawn(SampleId, 1, new string('x', 300), 1000, 1, SamplePose(), SmallTail());
            Assert.That(trimmed.Length, Is.EqualTo(BasisModelWire.MaxV1SpawnBytes));

            byte[] unnamed = BasisModelWire.EncodeSpawn(SampleId, 1, null, 1000, 1, SamplePose(), SmallTail());
            Assert.That(unnamed.Length, Is.EqualTo(BasisModelShareWire.SpawnFixedBytes + 1 + 96));
        }

        [Test]
        public void ATailShorterThanV1IsRejected()
        {
            byte[] message = RawSpawn(1, 95, new byte[95]);
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("tail is 95 bytes; at least 96 are required"));
        }

        [Test]
        public void ALongerTailIgnoresTheAppendix()
        {
            byte[] tail = TailBytes(SmallTail(), 130);
            for (int i = 96; i < tail.Length; i++)
                tail[i] = 0xAB;
            byte[] message = RawSpawn(1, 130, tail);

            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out BasisModelSpawnTail read, out string error), Is.True, error);
            CollectionAssert.AreEqual(ClaimsBytes(SmallClaims()), ClaimsBytes(read.Claims));
            Assert.That(read.BaseScale, Is.EqualTo(0.5f));
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(-1)]
        public void AnUnknownHeaderVersionIsRejected(int version)
        {
            byte[] message = RawSpawn(version, 96, TailBytes(SmallTail(), 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("unsupported header version " + version));
        }

        [TestCase(96, 100)]
        [TestCase(100, 96)]
        [TestCase(97, 96)]
        public void TheDeclaredTailLengthMustEqualTheBytesAfterThePose(int declared, int actual)
        {
            byte[] message = RawSpawn(1, declared, TailBytes(SmallTail(), actual));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("tail length " + declared + " does not match the " + actual + " bytes after the pose"));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(0f)]
        [TestCase(-0f)]
        [TestCase(-1f)]
        public void ANonFiniteOrNonPositiveBaseScaleIsRejected(float baseScale)
        {
            BasisModelSpawnTail tail = SmallTail();
            tail.BaseScale = baseScale;
            byte[] message = RawSpawn(1, 96, TailBytes(tail, 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("base scale is not a finite positive number"));
        }

        [TestCase(1e6f, 4f)]
        [TestCase(1e-9f, 0.02f)]
        public void ALyingBaseScaleIsClampedToWhatAnHonestSenderCouldChoose(float claimed, float largestSideMeters)
        {
            BasisModelSpawnTail tail = SmallTail();
            tail.BaseScale = claimed;
            byte[] message = RawSpawn(1, 96, TailBytes(tail, 96));

            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out BasisModelSpawnTail read, out string error), Is.True, error);
            // SmallClaims' largest side is 1 m.
            Assert.That(read.BaseScale, Is.EqualTo(largestSideMeters).Within(1e-6f));
        }

        [Test]
        public void AnUnknownSizeModeReadsAsFit()
        {
            byte[] tail = TailBytes(SmallTail(), 96);
            tail[BasisModelWire.SizeModeOffset] = 7;
            byte[] message = RawSpawn(1, 96, tail);
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out BasisModelSpawnTail read, out string error), Is.True, error);
            Assert.That(read.SizeMode, Is.EqualTo(BasisModelSizeMode.Fit));

            Assert.That(BasisModelWire.IsKnownSizeMode(0), Is.True);
            Assert.That(BasisModelWire.IsKnownSizeMode(1), Is.True);
            Assert.That(BasisModelWire.IsKnownSizeMode(2), Is.False);
            Assert.That(BasisModelWire.IsKnownSizeMode(7), Is.False);

            tail[BasisModelWire.SizeModeOffset] = 1;
            message = RawSpawn(1, 96, tail);
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out read, out _), Is.True);
            Assert.That(read.SizeMode, Is.EqualTo(BasisModelSizeMode.Original));
        }

        [Test]
        public void ReservedBytesAreIgnored()
        {
            byte[] tail = TailBytes(SmallTail(), 96);
            tail[1] = 0xFF;
            tail[2] = 0x80;
            tail[3] = 0x01;
            byte[] message = RawSpawn(1, 96, tail);
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.True, error);
        }

        [Test]
        public void MalformedClaimsAreRejectedWithTheClaimsReason()
        {
            BasisModelSpawnTail tail = SmallTail();
            tail.Claims.FormatVersion = 2;
            byte[] message = RawSpawn(1, 96, TailBytes(tail, 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.False);
            StringAssert.Contains("version 2", error);

            tail = SmallTail();
            tail.Claims.Vertices = -1;
            message = RawSpawn(1, 96, TailBytes(tail, 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out error), Is.False);
            StringAssert.Contains("negative", error);
        }

        [Test]
        public void RulesRunInTableOrder()
        {
            BasisModelSpawnTail nanScale = SmallTail();
            nanScale.BaseScale = float.NaN;
            nanScale.Claims.FormatVersion = 9;

            byte[] message = RawSpawn(2, 10, new byte[10]);
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out string error), Is.False);
            StringAssert.StartsWith("unsupported header version", error);

            message = RawSpawn(1, 90, TailBytes(nanScale, 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out error), Is.False);
            StringAssert.StartsWith("tail is 90 bytes", error);

            message = RawSpawn(1, 100, TailBytes(nanScale, 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out error), Is.False);
            StringAssert.StartsWith("tail length 100", error);

            message = RawSpawn(1, 96, TailBytes(nanScale, 96));
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(message), message, out _, out error), Is.False);
            StringAssert.StartsWith("base scale", error);
        }

        [Test]
        public void AHeaderFromAnotherMessageIsRejected()
        {
            byte[] longMessage = BasisModelWire.EncodeSpawn(SampleId, 1, new string('n', 200), 1000, 1, SamplePose(), SmallTail());
            byte[] shortMessage = BasisModelWire.EncodeSpawn(SampleId, 1, "n", 1000, 1, SamplePose(), SmallTail());
            Assert.That(BasisModelWire.TryReadTail(ReadHeader(longMessage), shortMessage, out _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("the spawn header does not describe this message"));
        }

        [TestCase("")]
        [TestCase("Alice")]
        [TestCase("Zoë 名前")]
        [TestCase("a name long enough to need a two-byte length prefix once it has been repeated a few times over")]
        public void ServerCacheWalkerFindsTheTotalsAndPoseWithTheTailPresent(string name)
        {
            string ownerName = name.Length > 50 ? name + name : name;
            byte[] message = BasisModelWire.EncodeSpawn(SampleId, SampleOwnerId, ownerName, 33554432, 2048, SamplePose(), SmallTail());
            BasisModelSpawnHeader header = ReadHeader(message);

            Assert.That(ServerTryReadSpawnHeader(message, message.Length, out int totalBytes, out int totalChunks, out int poseOffset), Is.True);
            Assert.That(totalBytes, Is.EqualTo(33554432));
            Assert.That(totalChunks, Is.EqualTo(2048));
            Assert.That(poseOffset, Is.EqualTo(header.PoseOffset));
            Assert.That(poseOffset + 28 + BasisModelWire.TailV1Bytes, Is.EqualTo(message.Length));

            // The server reads the pose at that offset with BitConverter and checks it against its 1e5 m model bound.
            Assert.That(BitConverter.ToSingle(message, poseOffset), Is.EqualTo(SamplePose().Position.X));
            Assert.That(BitConverter.ToSingle(message, poseOffset + 24), Is.EqualTo(SamplePose().Rotation.W));
            Assert.That(message.Length, Is.LessThanOrEqualTo(4096), "BasisNetworkModelCache.MaxSpawnHeaderBytes");
        }

        [Test]
        public void AnOfferWithAPatchedPoseDecodesTheSameTail()
        {
            byte[] spawn = BasisModelWire.EncodeSpawn(SampleId, SampleOwnerId, "Alice", 1000, 1, SamplePose(), SmallTail());
            Assert.That(ServerTryReadSpawnHeader(spawn, spawn.Length, out _, out _, out int poseOffset), Is.True);

            // What the server does to build an offer: copy, set opcode 9, patch the latest pose over the 28 pose bytes.
            byte[] offer = (byte[])spawn.Clone();
            offer[0] = BasisModelShareWire.OpServerCacheOffer;
            float[] latest = { -4f, 0.5f, 12f, 0f, 0f, 0f, 1f };
            for (int i = 0; i < latest.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(offer, poseOffset + i * 4, 4), BitConverter.SingleToInt32Bits(latest[i]));

            BasisModelSpawnHeader header = ReadHeader(offer);
            Assert.That(header.Opcode, Is.EqualTo(BasisModelShareWire.OpServerCacheOffer));
            Assert.That(header.Pose.Position.X, Is.EqualTo(-4f));
            Assert.That(header.Pose.Position.Z, Is.EqualTo(12f));
            Assert.That(header.Pose.Rotation.W, Is.EqualTo(1f));
            Assert.That(BasisModelWire.TryReadTail(header, offer, out BasisModelSpawnTail tail, out string error), Is.True, error);
            CollectionAssert.AreEqual(ClaimsBytes(SmallClaims()), ClaimsBytes(tail.Claims));
            Assert.That(tail.BaseScale, Is.EqualTo(0.5f));
        }

        [Test]
        public void ChunkCountsUseSixteenKibibyteChunks()
        {
            Assert.That(BasisModelShareWire.ExpectedChunkCount(16384, BasisModelWire.ChunkPayloadBytes), Is.EqualTo(1));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(16385, BasisModelWire.ChunkPayloadBytes), Is.EqualTo(2));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(32 * 1024 * 1024, BasisModelWire.ChunkPayloadBytes), Is.EqualTo(2048));
        }

        [Test]
        public void WriteTailRejectsAShortDestination()
        {
            Assert.Throws<ArgumentException>(() => BasisModelWire.WriteTail(new byte[95], SmallTail()));
        }

        [Test]
        public void HelloLayoutIsPinned()
        {
            var hello = new byte[3];
            BasisModelWire.WriteHello(hello, false);
            CollectionAssert.AreEqual(new byte[] { 11, 1, 0 }, hello);
            BasisModelWire.WriteHello(hello, true);
            CollectionAssert.AreEqual(new byte[] { 11, 1, 1 }, hello);
            Assert.Throws<ArgumentException>(() => BasisModelWire.WriteHello(new byte[2], false));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HelloRoundTrips(bool reply)
        {
            var hello = new byte[BasisModelWire.HelloBytes];
            BasisModelWire.WriteHello(hello, reply);
            Assert.That(BasisModelWire.TryReadHello(hello, out byte version, out bool readReply), Is.True);
            Assert.That(version, Is.EqualTo(BasisModelWire.HelloVersion));
            Assert.That(readReply, Is.EqualTo(reply));
        }

        [Test]
        public void HelloIgnoresExtraBytesAndUnknownFlagBits()
        {
            Assert.That(BasisModelWire.TryReadHello(new byte[] { 11, 3, 0xFE, 9, 9 }, out byte version, out bool reply), Is.True);
            Assert.That(version, Is.EqualTo(3));
            Assert.That(reply, Is.False);

            Assert.That(BasisModelWire.TryReadHello(new byte[] { 11, 1, 0xFF }, out _, out reply), Is.True);
            Assert.That(reply, Is.True);
        }

        [Test]
        public void HelloRejectsShortOrForeignMessages()
        {
            Assert.That(BasisModelWire.TryReadHello(new byte[] { 11, 1 }, out _, out _), Is.False);
            Assert.That(BasisModelWire.TryReadHello(ReadOnlySpan<byte>.Empty, out _, out _), Is.False);
            Assert.That(BasisModelWire.TryReadHello(new byte[] { 1, 1, 0 }, out byte version, out bool reply), Is.False);
            Assert.That(version, Is.EqualTo(0));
            Assert.That(reply, Is.False);
        }

        /// <summary>
        /// Copy of the server's walker (BasisNetworkBlobCache.TryReadSpawnHeader and TrySkipWireString), which finds the
        /// totals and the pose it patches without any model knowledge. It must agree with every spawn a model client sends.
        /// </summary>
        private static bool ServerTryReadSpawnHeader(byte[] payload, int payloadLength, out int totalBytes, out int totalChunks, out int poseOffset)
        {
            const int HeaderBytes = 1 + 16;
            totalBytes = 0;
            totalChunks = 0;
            poseOffset = 0;
            if (payload == null || payloadLength > payload.Length)
                return false;

            int offset = HeaderBytes + 2;
            if (!ServerTrySkipWireString(payload, payloadLength, ref offset))
                return false;

            offset += 4 + 4;
            if (offset + 4 + 4 > payloadLength)
                return false;

            totalBytes = BitConverter.ToInt32(payload, offset);
            totalChunks = BitConverter.ToInt32(payload, offset + 4);
            poseOffset = offset + 8;
            return true;
        }

        private static bool ServerTrySkipWireString(byte[] payload, int payloadLength, ref int offset)
        {
            const int MaxOwnerNameBytes = 1024;
            int length = 0;
            int shift = 0;
            while (true)
            {
                if (offset >= payloadLength || shift > 4 * 7)
                    return false;

                byte piece = payload[offset++];
                length |= (piece & 0x7F) << shift;
                if ((piece & 0x80) == 0)
                    break;
                shift += 7;
            }

            if (length < 0 || length > MaxOwnerNameBytes || offset + length > payloadLength)
                return false;

            offset += length;
            return true;
        }
    }
}
