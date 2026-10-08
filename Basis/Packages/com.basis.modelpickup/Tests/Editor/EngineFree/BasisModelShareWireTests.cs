using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelShareWireTests
    {
        private static readonly Guid SampleId = new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

        private static BasisModelPose SamplePose()
        {
            return new BasisModelPose(
                new BasisModelVec3(1.5f, -2.25f, 300.125f),
                new BasisModelQuat(0.1f, -0.2f, 0.3f, 0.927f)
            );
        }

        [Test]
        public void OwnerNameReaderRejectsOversizedUtf8Length()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(new string('x', BasisModelShareWire.MaxOwnerNameUtf8Bytes + 1));
            }
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            Assert.Throws<InvalidDataException>(() => BasisModelShareWire.ReadBoundedOwnerName(reader));
        }

        [Test]
        public void OwnerNameNormalizationPreservesValidUtf8Boundary()
        {
            var source = new StringBuilder();
            for (int i = 0; i < 100; i++)
                source.Append("😀");

            string normalized = BasisModelShareWire.NormalizeOwnerName(source.ToString());
            Assert.That(
                Encoding.UTF8.GetByteCount(normalized),
                Is.LessThanOrEqualTo(BasisModelShareWire.MaxOwnerNameUtf8Bytes)
            );
            Assert.That(normalized, Is.Not.Empty);
            Assert.That(char.IsHighSurrogate(normalized[normalized.Length - 1]), Is.False);

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                writer.Write(normalized);
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            Assert.That(BasisModelShareWire.ReadBoundedOwnerName(reader), Is.EqualTo(normalized));
        }

        [Test]
        public void ChunkPacketsParseWithoutAStreamAndMatchTheBinaryReaderLayout()
        {
            Guid id = Guid.NewGuid();
            byte[] source = new byte[64];
            for (int i = 0; i < source.Length; i++)
                source[i] = (byte)(i * 7);
            byte[] packet = new byte[25 + 40];
            BasisModelShareWire.WriteChunk(packet, id, 3, source, 10, 40);

            Assert.That(
                BasisModelShareWire.TryReadChunkHeader(packet, out Guid parsedId, out int chunkIndex, out int length),
                Is.True
            );
            Assert.That(parsedId, Is.EqualTo(id));
            Assert.That(chunkIndex, Is.EqualTo(3));
            Assert.That(length, Is.EqualTo(40));

            using var stream = new MemoryStream(packet, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            Assert.That(reader.ReadByte(), Is.EqualTo(2));
            Assert.That(new Guid(reader.ReadBytes(16)), Is.EqualTo(id));
            Assert.That(reader.ReadInt32(), Is.EqualTo(3));
            Assert.That(reader.ReadInt32(), Is.EqualTo(40));
            Assert.That(reader.ReadBytes(40), Is.EqualTo(new ArraySegment<byte>(source, 10, 40).ToArray()));

            Assert.That(BasisModelShareWire.TryReadChunkHeader(new byte[24], out _, out _, out _), Is.False);
            Assert.That(BasisModelShareWire.TryReadChunkHeader(null, out _, out _, out _), Is.False);
        }

        [Test]
        public void WriteChunkRefusesAPacketOfTheWrongLength()
        {
            byte[] source = new byte[64];
            Assert.Throws<ArgumentException>(() => BasisModelShareWire.WriteChunk(new byte[25 + 39], SampleId, 0, source, 0, 40));
            Assert.Throws<ArgumentException>(() => BasisModelShareWire.WriteChunk(new byte[25 + 41], SampleId, 0, source, 0, 40));
        }

        [Test]
        public void SpawnHeaderRoundTripsEveryField()
        {
            BasisModelPose pose = SamplePose();
            byte[] message = BasisModelShareWire.EncodeSpawn(
                BasisModelShareWire.OpSpawn, SampleId, 4321, "Alice", 640, -480, 123456, 8, pose, ReadOnlySpan<byte>.Empty
            );

            Assert.That(BasisModelShareWire.TryReadSpawn(message, out BasisModelSpawnHeader header, out BasisModelShareWireError error), Is.True);
            Assert.That(error, Is.EqualTo(BasisModelShareWireError.None));
            Assert.That(header.Opcode, Is.EqualTo(BasisModelShareWire.OpSpawn));
            Assert.That(header.Id, Is.EqualTo(SampleId));
            Assert.That(header.OwnerId, Is.EqualTo((ushort)4321));
            Assert.That(header.FieldA, Is.EqualTo(640));
            Assert.That(header.FieldB, Is.EqualTo(-480));
            Assert.That(header.TotalBytes, Is.EqualTo(123456));
            Assert.That(header.TotalChunks, Is.EqualTo(8));
            AssertPoseBitsEqual(header.Pose, pose);
            Assert.That(header.PoseOffset, Is.EqualTo(19 + 1 + 5 + 16));
            Assert.That(header.TailLength, Is.EqualTo(0));
            Assert.That(message.Length, Is.EqualTo(BasisModelShareWire.SpawnFixedBytes + 1 + 5));
            Assert.That(message.Length, Is.EqualTo(BasisModelShareWire.MeasureSpawn("Alice", 0)));
        }

        [Test]
        public void SpawnTailFollowsThePoseAndRoundTrips()
        {
            byte[] tail = { 9, 8, 7, 6, 5, 4, 3, 2, 1 };
            byte[] message = BasisModelShareWire.EncodeSpawn(
                BasisModelShareWire.OpServerCacheOffer, SampleId, 7, "Zoë", 1, 72, 99, 1, SamplePose(), tail
            );

            Assert.That(BasisModelShareWire.TryReadSpawn(message, out BasisModelSpawnHeader header, out _), Is.True);
            Assert.That(header.Opcode, Is.EqualTo(BasisModelShareWire.OpServerCacheOffer));
            Assert.That(header.TailOffset, Is.EqualTo(header.PoseOffset + BasisModelShareWire.PoseBytes));
            Assert.That(header.TailLength, Is.EqualTo(tail.Length));
            Assert.That(header.TailOffset + header.TailLength, Is.EqualTo(message.Length));
            Assert.That(
                new ArraySegment<byte>(message, header.TailOffset, header.TailLength).ToArray(),
                Is.EqualTo(tail)
            );
            Assert.That(message.Length, Is.EqualTo(BasisModelShareWire.MeasureSpawn("Zoë", tail.Length)));
        }

        [Test]
        public void SpawnNormalisesTheOwnerNameAndNullBecomesEmpty()
        {
            byte[] nullName = BasisModelShareWire.EncodeSpawn(1, SampleId, 0, null, 0, 0, 1, 1, SamplePose(), ReadOnlySpan<byte>.Empty);
            Assert.That(nullName.Length, Is.EqualTo(BasisModelShareWire.SpawnFixedBytes + 1));
            Assert.That(nullName[19], Is.EqualTo(0));

            byte[] longName = BasisModelShareWire.EncodeSpawn(
                1, SampleId, 0, new string('x', 300), 0, 0, 1, 1, SamplePose(), ReadOnlySpan<byte>.Empty
            );
            Assert.That(longName.Length, Is.EqualTo(BasisModelShareWire.SpawnFixedBytes + 2 + 256));
            Assert.That(BasisModelShareWire.TryReadSpawn(longName, out BasisModelSpawnHeader header, out _), Is.True);
            Assert.That(header.PoseOffset, Is.EqualTo(19 + 2 + 256 + 16));
        }

        [Test]
        public void SpawnSkipsOwnerNamesUpTo1024BytesAndReportsLongerAsInvalidOwnerName()
        {
            byte[] atLimit = HandBuiltSpawn(BasisModelShareWire.MaxIgnoredOwnerNameBytes);
            Assert.That(BasisModelShareWire.TryReadSpawn(atLimit, out BasisModelSpawnHeader header, out BasisModelShareWireError error), Is.True);
            Assert.That(error, Is.EqualTo(BasisModelShareWireError.None));
            Assert.That(header.TotalBytes, Is.EqualTo(777));
            Assert.That(header.PoseOffset, Is.EqualTo(19 + 2 + 1024 + 16));

            byte[] overLimit = HandBuiltSpawn(BasisModelShareWire.MaxIgnoredOwnerNameBytes + 1);
            Assert.That(BasisModelShareWire.TryReadSpawn(overLimit, out _, out error), Is.False);
            Assert.That(error, Is.EqualTo(BasisModelShareWireError.InvalidOwnerName));
        }

        [Test]
        public void SpawnTruncationIsReportedSeparatelyFromABadOwnerName()
        {
            byte[] message = BasisModelShareWire.EncodeSpawn(1, SampleId, 3, "Alice", 4, 4, 64, 1, SamplePose(), ReadOnlySpan<byte>.Empty);
            Assert.That(message.Length, Is.EqualTo(69));

            // Before the name prefix, and between the name and the end of the pose: truncated.
            AssertSpawnError(message, 10, BasisModelShareWireError.Truncated);
            AssertSpawnError(message, 18, BasisModelShareWireError.Truncated);
            AssertSpawnError(message, 30, BasisModelShareWireError.Truncated);
            AssertSpawnError(message, 60, BasisModelShareWireError.Truncated);
            AssertSpawnError(message, 68, BasisModelShareWireError.Truncated);

            // A missing prefix or a name running past the end is a bad name, which receivers drop silently.
            AssertSpawnError(message, 19, BasisModelShareWireError.InvalidOwnerName);
            AssertSpawnError(message, 22, BasisModelShareWireError.InvalidOwnerName);

            Assert.That(BasisModelShareWire.TryReadSpawn(message, out _, out _), Is.True);
        }

        [TestCase("")]
        [TestCase("Alice")]
        [TestCase("Zoë")]
        [TestCase("a name that is long enough to need more than one byte of length prefix once it is repeated")]
        public void SpawnPoseOffsetMatchesTheServerCacheWalker(string name)
        {
            string ownerName = name.Length > 50 ? name + name + name : name;
            byte[] message = BasisModelShareWire.EncodeSpawn(1, SampleId, 3, ownerName, 4, 4, 70000, 5, SamplePose(), new byte[] { 1, 2, 3 });

            Assert.That(BasisModelShareWire.TryReadSpawn(message, out BasisModelSpawnHeader header, out _), Is.True);
            Assert.That(ServerTryReadSpawnHeader(message, message.Length, out int totalChunks, out int poseOffset), Is.True);
            Assert.That(totalChunks, Is.EqualTo(header.TotalChunks));
            Assert.That(poseOffset, Is.EqualTo(header.PoseOffset));
        }

        [Test]
        public void TransformIsExactly49BytesAndToleratesTrailingBytes()
        {
            Assert.That(BasisModelShareWire.TransformBytes, Is.EqualTo(49));

            byte[] message = new byte[49];
            BasisModelShareWire.WriteTransform(message, SampleId, SamplePose(), 1.75f);
            Assert.That(message[0], Is.EqualTo(BasisModelShareWire.OpTransform));

            Assert.That(BasisModelShareWire.TryReadTransform(message, out Guid id, out BasisModelPose pose, out float scale), Is.True);
            Assert.That(id, Is.EqualTo(SampleId));
            AssertPoseBitsEqual(pose, SamplePose());
            Assert.That(scale, Is.EqualTo(1.75f));

            byte[] padded = new byte[60];
            Buffer.BlockCopy(message, 0, padded, 0, 49);
            Assert.That(BasisModelShareWire.TryReadTransform(padded, out Guid paddedId, out _, out float paddedScale), Is.True);
            Assert.That(paddedId, Is.EqualTo(SampleId));
            Assert.That(paddedScale, Is.EqualTo(1.75f));

            Assert.That(BasisModelShareWire.TryReadTransform(new byte[48], out _, out _, out _), Is.False);
            Assert.Throws<ArgumentException>(() => BasisModelShareWire.WriteTransform(new byte[48], SampleId, SamplePose(), 1f));
        }

        [Test]
        public void IdMessagesAre17BytesAndCacheStateIs18()
        {
            Assert.That(BasisModelShareWire.IdMessageBytes, Is.EqualTo(17));
            Assert.That(BasisModelShareWire.CacheStateBytes, Is.EqualTo(18));
            Assert.That(BasisModelShareWire.ChunkHeaderBytes, Is.EqualTo(25));
            Assert.That(BasisModelShareWire.SpawnFixedBytes, Is.EqualTo(63));

            byte[] id = new byte[17];
            BasisModelShareWire.WriteIdMessage(id, BasisModelShareWire.OpDespawn, SampleId);
            Assert.That(id[0], Is.EqualTo(BasisModelShareWire.OpDespawn));
            Assert.That(BasisModelShareWire.TryReadIdMessage(id, out Guid parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(SampleId));
            Assert.That(BasisModelShareWire.TryReadIdMessage(new byte[16], out _), Is.False);
            Assert.Throws<ArgumentException>(() => BasisModelShareWire.WriteIdMessage(new byte[16], 4, SampleId));

            byte[] state = new byte[18];
            BasisModelShareWire.WriteCacheState(state, SampleId, true);
            Assert.That(state[0], Is.EqualTo(BasisModelShareWire.OpServerCacheState));
            Assert.That(state[17], Is.EqualTo(1));
            Assert.That(BasisModelShareWire.TryReadCacheState(state, out Guid stateId, out bool held), Is.True);
            Assert.That(stateId, Is.EqualTo(SampleId));
            Assert.That(held, Is.True);

            // Any non-zero byte reads as held, which is what BinaryReader.ReadBoolean did.
            state[17] = 0x80;
            Assert.That(BasisModelShareWire.TryReadCacheState(state, out _, out held), Is.True);
            Assert.That(held, Is.True);
            BasisModelShareWire.WriteCacheState(state, SampleId, false);
            Assert.That(BasisModelShareWire.TryReadCacheState(state, out _, out held), Is.True);
            Assert.That(held, Is.False);
            Assert.That(BasisModelShareWire.TryReadCacheState(new byte[17], out _, out _), Is.False);
        }

        [Test]
        public void ExpectedChunkCountIsTheCeilingAndRejectsNonPositive()
        {
            Assert.That(BasisModelShareWire.ExpectedChunkCount(1, 16384), Is.EqualTo(1));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(16384, 16384), Is.EqualTo(1));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(16385, 16384), Is.EqualTo(2));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(32 * 1024 * 1024, 16384), Is.EqualTo(2048));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(int.MaxValue, 16384), Is.EqualTo(131072));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(0, 16384), Is.EqualTo(-1));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(-5, 16384), Is.EqualTo(-1));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(100, 0), Is.EqualTo(-1));
            Assert.That(BasisModelShareWire.ExpectedChunkCount(100, -1), Is.EqualTo(-1));
        }

        [Test]
        public void SevenBitLengthRejectsAFifthByteWithHighBits()
        {
            int offset = 0;
            Assert.That(BasisModelShareWire.TryRead7BitEncodedInt(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 }, ref offset, out int value), Is.True);
            Assert.That(value, Is.EqualTo(int.MaxValue));
            Assert.That(offset, Is.EqualTo(5));

            // The low nibble of a fifth byte can still set bit 31; callers see a negative length and refuse it.
            offset = 0;
            Assert.That(BasisModelShareWire.TryRead7BitEncodedInt(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 }, ref offset, out value), Is.True);
            Assert.That(value, Is.LessThan(0));
            offset = 0;
            Assert.That(BasisModelShareWire.TrySkipString(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 }, ref offset, 1024), Is.False);

            offset = 0;
            Assert.That(BasisModelShareWire.TryRead7BitEncodedInt(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 }, ref offset, out _), Is.False);
            Assert.That(offset, Is.EqualTo(0));
            offset = 0;
            Assert.That(BasisModelShareWire.TryRead7BitEncodedInt(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x81 }, ref offset, out _), Is.False);
            offset = 0;
            Assert.That(BasisModelShareWire.TryRead7BitEncodedInt(new byte[] { 0x80, 0x80 }, ref offset, out _), Is.False);
            Assert.That(offset, Is.EqualTo(0));
        }

        [Test]
        public void SevenBitLengthsMatchBinaryWriter()
        {
            int[] values = { 0, 1, 127, 128, 255, 256, 1024, 16383, 16384, 2097151, 2097152, int.MaxValue, -1 };
            byte[] buffer = new byte[5];
            foreach (int value in values)
            {
                using var stream = new MemoryStream();
                using (var writer = new LegacySevenBitWriter(stream))
                    writer.WriteLength(value);
                byte[] expected = stream.ToArray();

                var spanWriter = new BasisModelSpanWriter(buffer);
                spanWriter.Write7BitEncodedInt(value);
                Assert.That(spanWriter.Position, Is.EqualTo(expected.Length), $"length of {value}");
                Assert.That(BasisModelShareWire.Measure7BitEncodedInt(value), Is.EqualTo(expected.Length), $"measure of {value}");
                Assert.That(new ArraySegment<byte>(buffer, 0, expected.Length).ToArray(), Is.EqualTo(expected), $"bytes of {value}");

                int offset = 0;
                Assert.That(BasisModelShareWire.TryRead7BitEncodedInt(expected, ref offset, out int read), Is.True);
                Assert.That(read, Is.EqualTo(value));
            }
        }

        [Test]
        public void FloatsKeepTheirExactBitsIncludingNaNPayloads()
        {
            int[] patterns = { 0x7FC00001, unchecked((int)0xFFC12345), unchecked((int)0x80000000), 0x00000001, 0x7F7FFFFF };
            byte[] buffer = new byte[4];
            foreach (int bits in patterns)
            {
                float value = BitConverter.Int32BitsToSingle(bits);
                var writer = new BasisModelSpanWriter(buffer);
                writer.WriteSingle(value);
                Assert.That(BitConverter.ToInt32(buffer, 0), Is.EqualTo(bits));

                var reader = new BasisModelSpanReader(buffer);
                Assert.That(reader.TryReadSingle(out float read), Is.True);
                Assert.That(BitConverter.SingleToInt32Bits(read), Is.EqualTo(bits));
            }
        }

        [Test]
        public void SignalingNaNStillReadsBackAsNaN()
        {
            // Unity's Mono quiets a signaling NaN as it passes through a float local (0x7F800001 comes
            // back 0x7FC00001) while .NET keeps the payload, so only NaN-ness is part of the contract.
            // Receivers reject non-finite poses anyway.
            byte[] buffer = new byte[4];
            var writer = new BasisModelSpanWriter(buffer);
            writer.WriteSingle(BitConverter.Int32BitsToSingle(0x7F800001));
            var reader = new BasisModelSpanReader(buffer);
            Assert.That(reader.TryReadSingle(out float read), Is.True);
            Assert.That(float.IsNaN(read), Is.True);
        }

        [Test]
        public void SpanWriterRefusesToOverrun()
        {
            byte[] buffer = new byte[3];
            Assert.Throws<ArgumentException>(() =>
            {
                var writer = new BasisModelSpanWriter(buffer);
                writer.WriteInt32(1);
            });
            Assert.Throws<ArgumentException>(() =>
            {
                var writer = new BasisModelSpanWriter(buffer);
                writer.WriteUInt16(1);
                writer.WriteUInt16(2);
            });
            Assert.Throws<ArgumentException>(() =>
            {
                var writer = new BasisModelSpanWriter(new byte[15]);
                writer.WriteGuid(SampleId);
            });
            Assert.Throws<ArgumentException>(() =>
            {
                var writer = new BasisModelSpanWriter(new byte[27]);
                writer.WritePose(SamplePose());
            });
            Assert.Throws<ArgumentException>(() =>
            {
                var writer = new BasisModelSpanWriter(new byte[1]);
                writer.Write7BitEncodedInt(128);
            });

            var exact = new BasisModelSpanWriter(buffer);
            exact.WriteByte(1);
            exact.WriteUInt16(0x0302);
            Assert.That(exact.Remaining, Is.EqualTo(0));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void SpanReaderFailsWithoutMovingOnShortInput()
        {
            byte[] buffer = new byte[16 + 28 + 8 + 4 + 2 + 1];
            var writer = new BasisModelSpanWriter(buffer);
            writer.WriteGuid(SampleId);
            writer.WritePose(SamplePose());
            writer.WriteInt64(-1234567890123L);
            writer.WriteUInt32(0xDEADBEEF);
            writer.WriteUInt16(65535);
            writer.WriteByte(42);
            Assert.That(writer.Remaining, Is.EqualTo(0));

            var reader = new BasisModelSpanReader(buffer);
            Assert.That(reader.TryReadGuid(out Guid id), Is.True);
            Assert.That(id, Is.EqualTo(SampleId));
            Assert.That(reader.TryReadPose(out BasisModelPose pose), Is.True);
            AssertPoseBitsEqual(pose, SamplePose());
            Assert.That(reader.TryReadInt64(out long longValue), Is.True);
            Assert.That(longValue, Is.EqualTo(-1234567890123L));
            Assert.That(reader.TryReadUInt32(out uint uintValue), Is.True);
            Assert.That(uintValue, Is.EqualTo(0xDEADBEEF));
            Assert.That(reader.TryReadUInt16(out ushort ushortValue), Is.True);
            Assert.That(ushortValue, Is.EqualTo((ushort)65535));

            int before = reader.Position;
            Assert.That(reader.TryReadInt32(out _), Is.False);
            Assert.That(reader.TryReadPose(out _), Is.False);
            Assert.That(reader.TryReadGuid(out _), Is.False);
            Assert.That(reader.TrySkip(2), Is.False);
            Assert.That(reader.TrySkip(-1), Is.False);
            Assert.That(reader.Position, Is.EqualTo(before));
            Assert.That(reader.TryReadByte(out byte last), Is.True);
            Assert.That(last, Is.EqualTo(42));
            Assert.That(reader.Remaining, Is.EqualTo(0));
            Assert.That(reader.TryReadByte(out _), Is.False);

            Assert.That(RemainingFrom(buffer, buffer.Length), Is.EqualTo(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RemainingFrom(buffer, buffer.Length + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RemainingFrom(buffer, -1));
        }

        private static int RemainingFrom(byte[] buffer, int position)
        {
            return new BasisModelSpanReader(buffer, position).Remaining;
        }

        private static void AssertSpawnError(byte[] message, int length, BasisModelShareWireError expected)
        {
            ReadOnlySpan<byte> cut = new ReadOnlySpan<byte>(message, 0, length);
            Assert.That(BasisModelShareWire.TryReadSpawn(cut, out _, out BasisModelShareWireError error), Is.False, $"cut at {length}");
            Assert.That(error, Is.EqualTo(expected), $"cut at {length}");
        }

        internal static void AssertPoseBitsEqual(in BasisModelPose actual, in BasisModelPose expected)
        {
            Assert.That(Bits(actual.Position.X), Is.EqualTo(Bits(expected.Position.X)));
            Assert.That(Bits(actual.Position.Y), Is.EqualTo(Bits(expected.Position.Y)));
            Assert.That(Bits(actual.Position.Z), Is.EqualTo(Bits(expected.Position.Z)));
            Assert.That(Bits(actual.Rotation.X), Is.EqualTo(Bits(expected.Rotation.X)));
            Assert.That(Bits(actual.Rotation.Y), Is.EqualTo(Bits(expected.Rotation.Y)));
            Assert.That(Bits(actual.Rotation.Z), Is.EqualTo(Bits(expected.Rotation.Z)));
            Assert.That(Bits(actual.Rotation.W), Is.EqualTo(Bits(expected.Rotation.W)));
        }

        private static int Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        /// <summary>A spawn whose owner name is <paramref name="nameBytes"/> ASCII bytes, beyond what EncodeSpawn would send.</summary>
        private static byte[] HandBuiltSpawn(int nameBytes)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write((byte)1);
                writer.Write(SampleId.ToByteArray());
                writer.Write((ushort)5);
                writer.Write(new string('n', nameBytes));
                writer.Write(16);
                writer.Write(16);
                writer.Write(777);
                writer.Write(1);
                for (int i = 0; i < 7; i++)
                    writer.Write(0.5f);
            }
            return stream.ToArray();
        }

        /// <summary>
        /// Copy of the server caches' spawn walker (TryReadSpawnHeader and TrySkipWireString in
        /// BasisNetworkImageCache and BasisNetworkModelCache), which finds totalChunks and the pose it patches
        /// without the client's code. The two must agree on
        /// every header a client sends.
        /// </summary>
        private static bool ServerTryReadSpawnHeader(byte[] payload, int payloadLength, out int totalChunks, out int poseOffset)
        {
            const int HeaderBytes = 1 + 16;
            totalChunks = 0;
            poseOffset = 0;

            int offset = HeaderBytes + 2;
            if (!ServerTrySkipWireString(payload, payloadLength, ref offset))
                return false;

            offset += 4 + 4 + 4;
            if (offset + 4 > payloadLength)
                return false;

            totalChunks = BitConverter.ToInt32(payload, offset);
            poseOffset = offset + 4;
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

        /// <summary>Exposes BinaryWriter's own 7-bit encoder, the reference the span writer must match.</summary>
        private sealed class LegacySevenBitWriter : BinaryWriter
        {
            public LegacySevenBitWriter(Stream output)
                : base(output, Encoding.UTF8, true) { }

            public void WriteLength(int value)
            {
                Write7BitEncodedInt(value);
            }
        }
    }
}
