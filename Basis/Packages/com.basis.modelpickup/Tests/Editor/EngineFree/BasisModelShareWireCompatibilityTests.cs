using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// Pins the codecs byte for byte against BinaryWriter-based encoders (the image pickup's layout, which the
    /// server's caches walk), reproduced below as the oracle. Other clients and the server parse these bytes,
    /// so any difference here is a protocol break, not a refactor.
    /// </summary>
    public sealed class BasisModelShareWireCompatibilityTests
    {
        private static readonly Guid SampleId = new Guid("89abcdef-0123-4567-89ab-cdef01234567");

        private static string OwnerNameCase(int index)
        {
            switch (index)
            {
                case 0:
                    return string.Empty;
                case 1:
                    return "Alice";
                case 2:
                    return "Zoë";
                case 3:
                    return Repeat("😀", 100);
                case 4:
                    return Repeat("x", 300);
                case 5:
                    return null;
                case 6:
                    // A lone surrogate: both encoders must replace it the same way.
                    return "bad\uD800name";
                default:
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void SpawnBytesMatchTheLegacyEncoder(int nameCase)
        {
            string ownerName = OwnerNameCase(nameCase);
            BasisModelPose pose = AwkwardPose();

            byte[] legacy = LegacyEncodeSpawn(SampleId, 513, ownerName, 2048, 1536, 3_000_123, 184, pose);
            byte[] current = BasisModelShareWire.EncodeSpawn(
                BasisModelShareWire.OpSpawn,
                SampleId,
                513,
                ownerName,
                2048,
                1536,
                3_000_123,
                184,
                pose,
                ReadOnlySpan<byte>.Empty
            );

            Assert.That(current, Is.EqualTo(legacy));
            Assert.That(
                current.Length,
                Is.EqualTo(BasisModelShareWire.MeasureSpawn(BasisModelShareWire.NormalizeOwnerName(ownerName), 0))
            );
        }

        [Test]
        public void OwnerNameNormalisationMatchesTheLegacyNormaliser()
        {
            for (int i = 0; i <= 6; i++)
            {
                string name = OwnerNameCase(i);
                Assert.That(BasisModelShareWire.NormalizeOwnerName(name), Is.EqualTo(LegacyNormalizeOwnerNameForNetwork(name)));
            }
        }

        [Test]
        public void TransformDespawnClaimAndRequestBytesMatchTheLegacyEncoders()
        {
            BasisModelPose pose = AwkwardPose();
            float[] scales = { 1f, 0.015625f, 64f, -0f, BitConverter.Int32BitsToSingle(0x7FC00001) };
            byte[] transform = new byte[BasisModelShareWire.TransformBytes];
            foreach (float scale in scales)
            {
                BasisModelShareWire.WriteTransform(transform, SampleId, pose, scale);
                Assert.That(transform, Is.EqualTo(LegacyEncodeTransform(SampleId, pose, scale)));
            }

            byte[] id = new byte[BasisModelShareWire.IdMessageBytes];
            BasisModelShareWire.WriteIdMessage(id, BasisModelShareWire.OpDespawn, SampleId);
            Assert.That(id, Is.EqualTo(LegacyEncodeIdOnly(4, SampleId)));

            BasisModelShareWire.WriteIdMessage(id, BasisModelShareWire.OpClaim, SampleId);
            Assert.That(id, Is.EqualTo(LegacyEncodeIdOnly(5, SampleId)));

            BasisModelShareWire.WriteIdMessage(id, BasisModelShareWire.OpServerCacheRequest, SampleId);
            Assert.That(id, Is.EqualTo(LegacyEncodeServerCacheRequest(SampleId)));
        }

        [Test]
        public void CacheStateBytesMatchTheServerEncoderAndTheLegacyReader()
        {
            byte[] state = new byte[BasisModelShareWire.CacheStateBytes];
            foreach (bool held in new[] { true, false })
            {
                BasisModelShareWire.WriteCacheState(state, SampleId, held);
                Assert.That(state, Is.EqualTo(ServerEncodeCacheState(SampleId, held)));

                using var stream = new MemoryStream(state, false);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                Assert.That(reader.ReadByte(), Is.EqualTo(8));
                Assert.That(new Guid(reader.ReadBytes(16)), Is.EqualTo(SampleId));
                Assert.That(reader.ReadBoolean(), Is.EqualTo(held));
            }
        }

        [Test]
        public void ChunkBytesMatchTheLegacyEncoder()
        {
            const int Chunk = BasisModelShareSettings.ChunkPayloadBytes;
            byte[] payload = new byte[Chunk * 2 + 123];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i * 31 + 7);

            // A full chunk, then the short tail chunk.
            int[] indices = { 1, 2 };
            foreach (int index in indices)
            {
                int offset = index * Chunk;
                int length = Math.Min(Chunk, payload.Length - offset);
                byte[] current = new byte[BasisModelShareWire.ChunkHeaderBytes + length];
                byte[] legacy = new byte[BasisModelShareWire.ChunkHeaderBytes + length];
                BasisModelShareWire.WriteChunk(current, SampleId, index, payload, offset, length);
                LegacyEncodeChunkInto(legacy, SampleId, index, payload, offset, length);
                Assert.That(current, Is.EqualTo(legacy), $"chunk {index}");
            }
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(4)]
        public void LegacyBinaryReaderParsesNewSpawnBytes(int nameCase)
        {
            BasisModelPose pose = AwkwardPose();
            byte[] current = BasisModelShareWire.EncodeSpawn(
                BasisModelShareWire.OpSpawn,
                SampleId,
                77,
                OwnerNameCase(nameCase),
                320,
                240,
                40_000,
                3,
                pose,
                ReadOnlySpan<byte>.Empty
            );

            // The old HandleSpawn read order, after the dispatcher consumed the opcode.
            using var stream = new MemoryStream(current, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            Assert.That(reader.ReadByte(), Is.EqualTo(1));
            Assert.That(new Guid(reader.ReadBytes(16)), Is.EqualTo(SampleId));
            Assert.That(reader.ReadUInt16(), Is.EqualTo((ushort)77));
            Assert.That(LegacyTrySkipWireString(reader, 1024), Is.True);
            Assert.That(reader.ReadInt32(), Is.EqualTo(320));
            Assert.That(reader.ReadInt32(), Is.EqualTo(240));
            Assert.That(reader.ReadInt32(), Is.EqualTo(40_000));
            Assert.That(reader.ReadInt32(), Is.EqualTo(3));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Position.X)));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Position.Y)));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Position.Z)));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Rotation.X)));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Rotation.Y)));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Rotation.Z)));
            Assert.That(BitConverter.SingleToInt32Bits(reader.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(pose.Rotation.W)));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }

        [Test]
        public void NewReaderParsesLegacySpawnBytes()
        {
            BasisModelPose pose = AwkwardPose();
            byte[] legacy = LegacyEncodeSpawn(SampleId, 9, "Zoë", 11, 22, 33, 1, pose);

            Assert.That(BasisModelShareWire.TryReadSpawn(legacy, out BasisModelSpawnHeader header, out _), Is.True);
            Assert.That(header.Id, Is.EqualTo(SampleId));
            Assert.That(header.OwnerId, Is.EqualTo((ushort)9));
            Assert.That(header.FieldA, Is.EqualTo(11));
            Assert.That(header.FieldB, Is.EqualTo(22));
            Assert.That(header.TotalBytes, Is.EqualTo(33));
            Assert.That(header.TotalChunks, Is.EqualTo(1));
            Assert.That(header.TailLength, Is.EqualTo(0));
            BasisModelShareWireTests.AssertPoseBitsEqual(header.Pose, pose);

            byte[] legacyTransform = LegacyEncodeTransform(SampleId, pose, 2.5f);
            Assert.That(BasisModelShareWire.TryReadTransform(legacyTransform, out Guid id, out BasisModelPose read, out float scale), Is.True);
            Assert.That(id, Is.EqualTo(SampleId));
            BasisModelShareWireTests.AssertPoseBitsEqual(read, pose);
            Assert.That(scale, Is.EqualTo(2.5f));
        }

        /// <summary>Negative zero, a subnormal, a large value and a non-unit rotation, so float bits are really compared.</summary>
        private static BasisModelPose AwkwardPose()
        {
            return new BasisModelPose(
                new BasisModelVec3(-0f, BitConverter.Int32BitsToSingle(1), 123456.789f),
                new BasisModelQuat(0.70710677f, -0.0001f, 1e-30f, 0.70710677f)
            );
        }

        private static string Repeat(string value, int count)
        {
            var builder = new StringBuilder(value.Length * count);
            for (int i = 0; i < count; i++)
                builder.Append(value);
            return builder.ToString();
        }

        // ── Oracle: the image pickup's BinaryWriter encoders, whose layout the server caches walk ────

        private const int LegacyMaxOwnerNameUtf8Bytes = 256;
        private const int LegacyChunkHeaderBytes = 1 + 16 + sizeof(int) * 2;

        /// <summary>BasisAnimatedImageNetworkCodec.WriteGuid: BasisGuid128 Low then High, each a little-endian u64.</summary>
        private static void LegacyWriteGuid(BinaryWriter writer, Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8));
            writer.Write(low);
            writer.Write(high);
        }

        private static void LegacyWritePose(BinaryWriter writer, in BasisModelPose pose)
        {
            writer.Write(pose.Position.X);
            writer.Write(pose.Position.Y);
            writer.Write(pose.Position.Z);
            writer.Write(pose.Rotation.X);
            writer.Write(pose.Rotation.Y);
            writer.Write(pose.Rotation.Z);
            writer.Write(pose.Rotation.W);
        }

        private static string LegacyNormalizeOwnerNameForNetwork(string ownerName)
        {
            string value = ownerName ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= LegacyMaxOwnerNameUtf8Bytes)
                return value;

            int characterCount = Math.Min(value.Length, LegacyMaxOwnerNameUtf8Bytes);
            while (characterCount > 0)
            {
                if (char.IsHighSurrogate(value[characterCount - 1]))
                    characterCount--;
                string candidate = value.Substring(0, characterCount);
                if (Encoding.UTF8.GetByteCount(candidate) <= LegacyMaxOwnerNameUtf8Bytes)
                    return candidate;
                characterCount--;
            }
            return string.Empty;
        }

        private static byte[] LegacyEncodeSpawn(
            Guid id,
            ushort ownerId,
            string ownerName,
            int width,
            int height,
            int totalBytes,
            int totalChunks,
            in BasisModelPose pose
        )
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write((byte)1);
            LegacyWriteGuid(writer, id);
            writer.Write(ownerId);
            writer.Write(LegacyNormalizeOwnerNameForNetwork(ownerName));
            writer.Write(width);
            writer.Write(height);
            writer.Write(totalBytes);
            writer.Write(totalChunks);
            LegacyWritePose(writer, pose);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] LegacyEncodeTransform(Guid id, in BasisModelPose pose, float scale)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write((byte)3);
            LegacyWriteGuid(writer, id);
            LegacyWritePose(writer, pose);
            writer.Write(scale);
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>EncodeDespawn and EncodeClaim: opcode then GUID.</summary>
        private static byte[] LegacyEncodeIdOnly(byte opcode, Guid id)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(opcode);
            LegacyWriteGuid(writer, id);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] LegacyEncodeServerCacheRequest(Guid id)
        {
            byte[] payload = new byte[1 + 16];
            payload[0] = 10;
            Buffer.BlockCopy(id.ToByteArray(), 0, payload, 1, 16);
            return payload;
        }

        /// <summary>BasisNetworkImageCache's cache-state notice, as the server builds it.</summary>
        private static byte[] ServerEncodeCacheState(Guid id, bool held)
        {
            byte[] payload = new byte[1 + 16 + 1];
            payload[0] = 8;
            Buffer.BlockCopy(id.ToByteArray(), 0, payload, 1, 16);
            payload[17] = held ? (byte)1 : (byte)0;
            return payload;
        }

        private static void LegacyEncodeChunkInto(byte[] destination, Guid id, int chunkIndex, byte[] source, int offset, int length)
        {
            Span<byte> guid = stackalloc byte[16];
            id.TryWriteBytes(guid);
            destination[0] = 2;
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(destination, 1, 8), BinaryPrimitives.ReadUInt64LittleEndian(guid));
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(destination, 9, 8), BinaryPrimitives.ReadUInt64LittleEndian(guid.Slice(8)));
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(destination, 17, sizeof(int)), chunkIndex);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(destination, 17 + sizeof(int), sizeof(int)), length);
            Buffer.BlockCopy(source, offset, destination, LegacyChunkHeaderBytes, length);
        }

        private static bool LegacyTrySkipWireString(BinaryReader reader, int maxByteLength)
        {
            if (!LegacyTryRead7BitEncodedInt(reader, out int byteLength))
                return false;
            if (byteLength < 0 || byteLength > maxByteLength)
                return false;

            long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
            if (remainingBytes < byteLength)
                return false;

            reader.BaseStream.Position += byteLength;
            return true;
        }

        private static bool LegacyTryRead7BitEncodedInt(BinaryReader reader, out int value)
        {
            value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (reader.BaseStream.Length - reader.BaseStream.Position < 1)
                    return false;

                byte b = reader.ReadByte();
                if (shift == 28 && (b & 0xF0) != 0)
                    return false;

                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return true;
            }
            return false;
        }
    }
}
