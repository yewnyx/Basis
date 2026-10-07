using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Basis.ModelPickup
{
    /// <summary>Why <see cref="BasisModelShareWire.TryReadSpawn"/> refused a message.</summary>
    public enum BasisModelShareWireError : byte
    {
        None = 0,
        Truncated = 1,

        /// <summary>
        /// The owner-name prefix is malformed or longer than <see cref="BasisModelShareWire.MaxIgnoredOwnerNameBytes"/>.
        /// Receivers drop these silently.
        /// </summary>
        InvalidOwnerName = 2,
    }

    /// <summary>
    /// Everything in a spawn (or server-cache offer) up to and including the pose, plus where the
    /// model's tail (<see cref="BasisModelSpawnTail"/>) sits. The owner name is skipped, never decoded: receivers name the owner from the
    /// transport sender, so nothing a peer writes there can impersonate anyone.
    /// </summary>
    public struct BasisModelSpawnHeader
    {
        public byte Opcode;
        public Guid Id;

        /// <summary>Informational only. Receivers trust the transport sender, not this field.</summary>
        public ushort OwnerId;

        /// <summary>The model sends its header version here (<see cref="BasisModelWire.HeaderVersion"/>).</summary>
        public int FieldA;

        /// <summary>The model sends its tail length here (<see cref="BasisModelWire.TailV1Bytes"/>).</summary>
        public int FieldB;

        public int TotalBytes;
        public int TotalChunks;
        public BasisModelPose Pose;

        /// <summary>Byte offset of the pose. The server cache patches the latest pose over these 28 bytes.</summary>
        public int PoseOffset;

        /// <summary><see cref="PoseOffset"/> + 28.</summary>
        public int TailOffset;

        /// <summary>Bytes after the pose, which the server stores and replays untouched.</summary>
        public int TailLength;
    }

    /// <summary>
    /// The model pickup's message layouts below the model-specific tail, little-endian, GUIDs in
    /// <see cref="Guid.ToByteArray"/> order. They are the image pickup's layouts, so a server cache that walks
    /// one walks the other; the server parses several (the spawn walker, 49-byte transforms, 17-byte
    /// requests), and the compatibility tests pin them byte for byte against a BinaryWriter oracle.
    ///
    /// Spawn / offer: opcode, guid, u16 ownerId, 7-bit length + UTF-8 owner name, i32 fieldA, i32 fieldB,
    /// i32 totalBytes, i32 totalChunks, 7 × f32 pose, then the opaque model tail.
    /// Chunk: opcode, guid, i32 index, i32 length, payload. Transform: opcode, guid, pose, f32 scale.
    /// Despawn, claim, cache request: opcode, guid. Cache state: opcode, guid, u8 held.
    /// </summary>
    public static class BasisModelShareWire
    {
        public const byte OpSpawn = 1;
        public const byte OpChunk = 2;
        public const byte OpTransform = 3;
        public const byte OpDespawn = 4;
        public const byte OpClaim = 5;

        // 6 and 7 are left unused: the image pickup's animation spawn and chunk, so the layouts never disagree.

        /// <summary>Server to owner only, stamped with the owner's own id: whether the server holds the item.</summary>
        public const byte OpServerCacheState = 8;

        /// <summary>Server to client, stamped with the recipient's id: the sharer's spawn bytes with this opcode.</summary>
        public const byte OpServerCacheOffer = 9;

        /// <summary>Client asking the server for an offered item, addressed to the client itself.</summary>
        public const byte OpServerCacheRequest = 10;

        public const int GuidBytes = 16;
        public const int PoseBytes = 7 * sizeof(float);
        public const int IdMessageBytes = 1 + GuidBytes;
        public const int CacheStateBytes = IdMessageBytes + 1;
        public const int TransformBytes = IdMessageBytes + PoseBytes + sizeof(float);
        public const int ChunkHeaderBytes = IdMessageBytes + sizeof(int) * 2;

        /// <summary>Opcode, guid, ownerId, the four i32 fields and the pose; excludes the name prefix, name and tail.</summary>
        public const int SpawnFixedBytes = IdMessageBytes + sizeof(ushort) + SpawnFieldsBytes + PoseBytes;

        /// <summary>Longest owner name a sender writes, in UTF-8 bytes.</summary>
        public const int MaxOwnerNameUtf8Bytes = 256;

        /// <summary>Longest owner name a receiver (or the server) will skip over before calling the message invalid.</summary>
        public const int MaxIgnoredOwnerNameBytes = 1024;

        private const int OwnerIdOffset = IdMessageBytes;
        private const int OwnerNameOffset = OwnerIdOffset + sizeof(ushort);
        private const int SpawnFieldsBytes = sizeof(int) * 4;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // ── Owner names ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Trims a display name to at most <see cref="MaxOwnerNameUtf8Bytes"/> UTF-8 bytes without splitting a
        /// surrogate pair. Null becomes empty.
        /// </summary>
        public static string NormalizeOwnerName(string ownerName)
        {
            string value = ownerName ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= MaxOwnerNameUtf8Bytes)
                return value;

            int characterCount = Math.Min(value.Length, MaxOwnerNameUtf8Bytes);
            while (characterCount > 0)
            {
                if (char.IsHighSurrogate(value[characterCount - 1]))
                    characterCount--;
                string candidate = value.Substring(0, characterCount);
                if (Encoding.UTF8.GetByteCount(candidate) <= MaxOwnerNameUtf8Bytes)
                    return candidate;
                characterCount--;
            }
            return string.Empty;
        }

        /// <summary>
        /// Strict decode of an owner name for tests and diagnostics: throws on an oversized length, a malformed
        /// prefix, truncation, or invalid UTF-8. Receivers never decode names; they skip them.
        /// </summary>
        public static string ReadBoundedOwnerName(BinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            uint byteLength = 0;
            for (int byteIndex = 0; byteIndex < 5; byteIndex++)
            {
                byte value = reader.ReadByte();
                if (byteIndex == 4 && (value & 0xF8) != 0)
                    throw new InvalidDataException("Owner name length prefix is invalid.");
                byteLength |= (uint)(value & 0x7F) << (byteIndex * 7);
                if ((value & 0x80) != 0)
                    continue;

                if (byteLength > MaxOwnerNameUtf8Bytes)
                {
                    throw new InvalidDataException($"Owner name exceeds {MaxOwnerNameUtf8Bytes:N0} UTF-8 bytes.");
                }
                byte[] bytes = reader.ReadBytes((int)byteLength);
                if (bytes.Length != (int)byteLength)
                    throw new EndOfStreamException("Owner name is truncated.");
                return StrictUtf8.GetString(bytes);
            }

            throw new InvalidDataException("Owner name length prefix is invalid.");
        }

        /// <summary>
        /// Reads a BinaryWriter 7-bit length at <paramref name="offset"/>. At most five bytes; a fifth byte with
        /// any of its top four bits set, or running out of bytes, fails. <paramref name="offset"/> moves only on
        /// success. A fifth byte may still set bit 31, so callers must reject negative lengths themselves.
        /// </summary>
        public static bool TryRead7BitEncodedInt(ReadOnlySpan<byte> buffer, ref int offset, out int value)
        {
            int cursor = offset;
            int result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if ((uint)cursor >= (uint)buffer.Length)
                    break;

                byte part = buffer[cursor++];
                if (shift == 28 && (part & 0xF0) != 0)
                    break;

                result |= (part & 0x7F) << shift;
                if ((part & 0x80) == 0)
                {
                    offset = cursor;
                    value = result;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        /// <summary>
        /// Skips a 7-bit length-prefixed string without decoding it. Fails on a malformed prefix, a negative
        /// length, a length above <paramref name="maxByteLength"/>, or a length past the end of the buffer.
        /// </summary>
        public static bool TrySkipString(ReadOnlySpan<byte> buffer, ref int offset, int maxByteLength)
        {
            int cursor = offset;
            if (!TryRead7BitEncodedInt(buffer, ref cursor, out int byteLength))
                return false;
            if (byteLength < 0 || byteLength > maxByteLength)
                return false;
            if (buffer.Length - cursor < byteLength)
                return false;
            offset = cursor + byteLength;
            return true;
        }

        public static int Measure7BitEncodedInt(int value)
        {
            uint remaining = (uint)value;
            int count = 1;
            while (remaining >= 0x80)
            {
                remaining >>= 7;
                count++;
            }
            return count;
        }

        // ── Spawn ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Encoded length of a spawn for an already normalised owner name and a tail of <paramref name="tailLength"/> bytes.</summary>
        public static int MeasureSpawn(string normalizedOwnerName, int tailLength)
        {
            if (tailLength < 0)
                throw new ArgumentOutOfRangeException(nameof(tailLength));
            int nameBytes = Encoding.UTF8.GetByteCount(normalizedOwnerName ?? string.Empty);
            return SpawnFixedBytes + Measure7BitEncodedInt(nameBytes) + nameBytes + tailLength;
        }

        /// <summary>
        /// Builds a spawn (opcode 1) or anything shaped like one, in one allocation. The owner name is
        /// normalised first and encoded with <see cref="Encoding.UTF8"/>, which replaces lone surrogates the
        /// same way BinaryWriter did, so the bytes match the old encoder exactly.
        /// </summary>
        public static byte[] EncodeSpawn(
            byte opcode,
            Guid id,
            ushort ownerId,
            string ownerName,
            int fieldA,
            int fieldB,
            int totalBytes,
            int totalChunks,
            in BasisModelPose pose,
            ReadOnlySpan<byte> tail
        )
        {
            string name = NormalizeOwnerName(ownerName);
            int nameBytes = Encoding.UTF8.GetByteCount(name);
            byte[] message = new byte[SpawnFixedBytes + Measure7BitEncodedInt(nameBytes) + nameBytes + tail.Length];

            var writer = new BasisModelSpanWriter(message);
            writer.WriteByte(opcode);
            writer.WriteGuid(id);
            writer.WriteUInt16(ownerId);
            writer.Write7BitEncodedInt(nameBytes);
            writer.Position += Encoding.UTF8.GetBytes(name, 0, name.Length, message, writer.Position);
            writer.WriteInt32(fieldA);
            writer.WriteInt32(fieldB);
            writer.WriteInt32(totalBytes);
            writer.WriteInt32(totalChunks);
            writer.WritePose(pose);
            writer.WriteBytes(tail);
            return message;
        }

        /// <summary>
        /// Parses a spawn header. <paramref name="message"/> includes the opcode byte, which is not checked,
        /// so the same reader serves spawns and server-cache offers. Checks run in this order:
        /// fewer than 19 bytes is <see cref="BasisModelShareWireError.Truncated"/>; a bad owner-name prefix is
        /// <see cref="BasisModelShareWireError.InvalidOwnerName"/>; fewer than 44 bytes after the name is
        /// <see cref="BasisModelShareWireError.Truncated"/>.
        /// </summary>
        public static bool TryReadSpawn(
            ReadOnlySpan<byte> message,
            out BasisModelSpawnHeader header,
            out BasisModelShareWireError error
        )
        {
            header = default;
            if (message.Length < OwnerNameOffset)
            {
                error = BasisModelShareWireError.Truncated;
                return false;
            }

            int offset = OwnerNameOffset;
            if (!TrySkipString(message, ref offset, MaxIgnoredOwnerNameBytes))
            {
                error = BasisModelShareWireError.InvalidOwnerName;
                return false;
            }
            if (message.Length - offset < SpawnFieldsBytes + PoseBytes)
            {
                error = BasisModelShareWireError.Truncated;
                return false;
            }

            header.Opcode = message[0];
            header.Id = new Guid(message.Slice(1, GuidBytes));
            header.OwnerId = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(OwnerIdOffset));
            header.FieldA = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(offset));
            header.FieldB = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(offset + 4));
            header.TotalBytes = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(offset + 8));
            header.TotalChunks = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(offset + 12));
            header.PoseOffset = offset + SpawnFieldsBytes;
            header.Pose = ReadPose(message.Slice(header.PoseOffset));
            header.TailOffset = header.PoseOffset + PoseBytes;
            header.TailLength = message.Length - header.TailOffset;
            error = BasisModelShareWireError.None;
            return true;
        }

        // ── Chunks ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Chunks needed for <paramref name="totalBytes"/>, rounded up and computed in 64 bits so no input
        /// overflows. Non-positive inputs return -1, which no header can legitimately claim.
        /// </summary>
        public static int ExpectedChunkCount(int totalBytes, int chunkPayloadBytes)
        {
            if (totalBytes <= 0 || chunkPayloadBytes <= 0)
                return -1;
            return (int)(((long)totalBytes + chunkPayloadBytes - 1) / chunkPayloadBytes);
        }

        /// <summary>
        /// Writes one chunk packet into <paramref name="destination"/>, which must be exactly
        /// <see cref="ChunkHeaderBytes"/> + <paramref name="length"/> long. The transports copy the buffer
        /// before returning, so a caller may reuse one array for every chunk of a transfer.
        /// </summary>
        public static void WriteChunk(byte[] destination, Guid id, int chunkIndex, byte[] source, int offset, int length)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (length < 0 || destination.Length != ChunkHeaderBytes + length)
            {
                throw new ArgumentException(
                    $"A chunk of {length} bytes needs a {ChunkHeaderBytes + length}-byte packet, not {destination.Length}.",
                    nameof(destination)
                );
            }

            destination[0] = OpChunk;
            id.TryWriteBytes(new Span<byte>(destination, 1, GuidBytes));
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(destination, IdMessageBytes, sizeof(int)), chunkIndex);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(destination, IdMessageBytes + sizeof(int), sizeof(int)), length);
            Buffer.BlockCopy(source, offset, destination, ChunkHeaderBytes, length);
        }

        /// <summary>Reads a chunk header. Ignores the opcode byte; anything shorter than 25 bytes fails.</summary>
        public static bool TryReadChunkHeader(byte[] buffer, out Guid id, out int chunkIndex, out int length)
        {
            if (buffer == null || buffer.Length < ChunkHeaderBytes)
            {
                id = default;
                chunkIndex = 0;
                length = 0;
                return false;
            }
            id = new Guid(new ReadOnlySpan<byte>(buffer, 1, GuidBytes));
            chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buffer, IdMessageBytes, sizeof(int)));
            length = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buffer, IdMessageBytes + sizeof(int), sizeof(int)));
            return true;
        }

        // ── Fixed-size messages ───────────────────────────────────────────────────────────────────

        /// <summary>Writes a 49-byte transform at the start of <paramref name="destination"/>. Scale is the uniform <c>localScale.x</c>.</summary>
        public static void WriteTransform(Span<byte> destination, Guid id, in BasisModelPose pose, float scale)
        {
            if (destination.Length < TransformBytes)
                throw new ArgumentException($"A transform needs {TransformBytes} bytes.", nameof(destination));

            var writer = new BasisModelSpanWriter(destination);
            writer.WriteByte(OpTransform);
            writer.WriteGuid(id);
            writer.WritePose(pose);
            writer.WriteSingle(scale);
        }

        /// <summary>Reads a transform. At least 49 bytes; trailing bytes are tolerated, as BinaryReader did.</summary>
        public static bool TryReadTransform(ReadOnlySpan<byte> message, out Guid id, out BasisModelPose pose, out float scale)
        {
            if (message.Length < TransformBytes)
            {
                id = default;
                pose = default;
                scale = 0f;
                return false;
            }
            id = new Guid(message.Slice(1, GuidBytes));
            pose = ReadPose(message.Slice(IdMessageBytes));
            scale = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(message.Slice(IdMessageBytes + PoseBytes))
            );
            return true;
        }

        /// <summary>Writes a 17-byte opcode + GUID message (despawn, claim, cache request).</summary>
        public static void WriteIdMessage(Span<byte> destination, byte opcode, Guid id)
        {
            if (destination.Length < IdMessageBytes)
                throw new ArgumentException($"An id message needs {IdMessageBytes} bytes.", nameof(destination));
            destination[0] = opcode;
            id.TryWriteBytes(destination.Slice(1, GuidBytes));
        }

        public static bool TryReadIdMessage(ReadOnlySpan<byte> message, out Guid id)
        {
            if (message.Length < IdMessageBytes)
            {
                id = default;
                return false;
            }
            id = new Guid(message.Slice(1, GuidBytes));
            return true;
        }

        /// <summary>Writes an 18-byte cache-state message. Clients never send one; this mirrors the server for tests.</summary>
        public static void WriteCacheState(Span<byte> destination, Guid id, bool held)
        {
            if (destination.Length < CacheStateBytes)
                throw new ArgumentException($"A cache state needs {CacheStateBytes} bytes.", nameof(destination));
            destination[0] = OpServerCacheState;
            id.TryWriteBytes(destination.Slice(1, GuidBytes));
            destination[IdMessageBytes] = held ? (byte)1 : (byte)0;
        }

        /// <summary>Reads a cache state. Any non-zero held byte means held, as BinaryReader.ReadBoolean did.</summary>
        public static bool TryReadCacheState(ReadOnlySpan<byte> message, out Guid id, out bool held)
        {
            if (message.Length < CacheStateBytes)
            {
                id = default;
                held = false;
                return false;
            }
            id = new Guid(message.Slice(1, GuidBytes));
            held = message[IdMessageBytes] != 0;
            return true;
        }

        /// <summary>Reads 28 pose bytes; the caller has already checked the length.</summary>
        public static BasisModelPose ReadPose(ReadOnlySpan<byte> source)
        {
            return new BasisModelPose(
                new BasisModelVec3(ReadSingle(source, 0), ReadSingle(source, 4), ReadSingle(source, 8)),
                new BasisModelQuat(
                    ReadSingle(source, 12),
                    ReadSingle(source, 16),
                    ReadSingle(source, 20),
                    ReadSingle(source, 24)
                )
            );
        }

        private static float ReadSingle(ReadOnlySpan<byte> source, int offset)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset)));
        }
    }
}
