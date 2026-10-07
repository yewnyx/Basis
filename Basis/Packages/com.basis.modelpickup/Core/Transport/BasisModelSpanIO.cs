using System;
using System.Buffers.Binary;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Little-endian writer over a caller-owned span. Callers measure first, so running out of room is a
    /// programmer error and throws <see cref="ArgumentException"/> rather than returning a status.
    ///
    /// Floats are written as their raw IEEE bits through <see cref="BitConverter.SingleToInt32Bits"/>:
    /// netstandard2.1 has no <c>BinaryPrimitives.WriteSingleLittleEndian</c>, and the bit route keeps NaN
    /// payloads exactly as BinaryWriter did.
    /// </summary>
    public ref struct BasisModelSpanWriter
    {
        private readonly Span<byte> _destination;

        public int Position;

        public BasisModelSpanWriter(Span<byte> destination)
        {
            _destination = destination;
            Position = 0;
        }

        public int Remaining => _destination.Length - Position;

        public void WriteByte(byte value)
        {
            Reserve(1);
            _destination[Position++] = value;
        }

        public void WriteUInt16(ushort value)
        {
            Reserve(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(_destination.Slice(Position), value);
            Position += sizeof(ushort);
        }

        public void WriteInt32(int value)
        {
            Reserve(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(_destination.Slice(Position), value);
            Position += sizeof(int);
        }

        public void WriteUInt32(uint value)
        {
            Reserve(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(_destination.Slice(Position), value);
            Position += sizeof(uint);
        }

        public void WriteInt64(long value)
        {
            Reserve(sizeof(long));
            BinaryPrimitives.WriteInt64LittleEndian(_destination.Slice(Position), value);
            Position += sizeof(long);
        }

        public void WriteSingle(float value)
        {
            WriteInt32(BitConverter.SingleToInt32Bits(value));
        }

        /// <summary>
        /// The <see cref="Guid.ToByteArray"/> layout, which is also BasisGuid128's Low/High little-endian
        /// pair, so every model GUID on the wire matches what the server cache reads.
        /// </summary>
        public void WriteGuid(Guid value)
        {
            Reserve(BasisModelShareWire.GuidBytes);
            value.TryWriteBytes(_destination.Slice(Position, BasisModelShareWire.GuidBytes));
            Position += BasisModelShareWire.GuidBytes;
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            Reserve(value.Length);
            value.CopyTo(_destination.Slice(Position));
            Position += value.Length;
        }

        /// <summary>BinaryWriter's 7-bit length prefix: low groups first, high bit set on every byte but the last.</summary>
        public void Write7BitEncodedInt(int value)
        {
            Reserve(BasisModelShareWire.Measure7BitEncodedInt(value));
            uint remaining = (uint)value;
            while (remaining >= 0x80)
            {
                _destination[Position++] = (byte)(remaining | 0x80);
                remaining >>= 7;
            }
            _destination[Position++] = (byte)remaining;
        }

        /// <summary>Position x, y, z then rotation x, y, z, w — the order every model message uses.</summary>
        public void WritePose(in BasisModelPose pose)
        {
            Reserve(BasisModelShareWire.PoseBytes);
            WriteSingle(pose.Position.X);
            WriteSingle(pose.Position.Y);
            WriteSingle(pose.Position.Z);
            WriteSingle(pose.Rotation.X);
            WriteSingle(pose.Rotation.Y);
            WriteSingle(pose.Rotation.Z);
            WriteSingle(pose.Rotation.W);
        }

        private void Reserve(int count)
        {
            if ((uint)Position > (uint)_destination.Length || count > _destination.Length - Position)
            {
                throw new ArgumentException(
                    $"Writing {count} bytes at {Position} overruns a {_destination.Length}-byte destination."
                );
            }
        }
    }

    /// <summary>
    /// Little-endian reader over untrusted bytes. Every read is a Try: a short buffer returns false and
    /// leaves <see cref="Position"/> where it was, so malformed input never throws.
    /// </summary>
    public ref struct BasisModelSpanReader
    {
        private readonly ReadOnlySpan<byte> _source;

        public int Position;

        public BasisModelSpanReader(ReadOnlySpan<byte> source, int position = 0)
        {
            if ((uint)position > (uint)source.Length)
                throw new ArgumentOutOfRangeException(nameof(position));
            _source = source;
            Position = position;
        }

        public int Remaining => _source.Length - Position;

        public bool TryReadByte(out byte value)
        {
            if (!Has(1))
            {
                value = 0;
                return false;
            }
            value = _source[Position++];
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (!Has(sizeof(ushort)))
            {
                value = 0;
                return false;
            }
            value = BinaryPrimitives.ReadUInt16LittleEndian(_source.Slice(Position));
            Position += sizeof(ushort);
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (!Has(sizeof(int)))
            {
                value = 0;
                return false;
            }
            value = BinaryPrimitives.ReadInt32LittleEndian(_source.Slice(Position));
            Position += sizeof(int);
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (!Has(sizeof(uint)))
            {
                value = 0;
                return false;
            }
            value = BinaryPrimitives.ReadUInt32LittleEndian(_source.Slice(Position));
            Position += sizeof(uint);
            return true;
        }

        public bool TryReadInt64(out long value)
        {
            if (!Has(sizeof(long)))
            {
                value = 0;
                return false;
            }
            value = BinaryPrimitives.ReadInt64LittleEndian(_source.Slice(Position));
            Position += sizeof(long);
            return true;
        }

        public bool TryReadSingle(out float value)
        {
            if (!TryReadInt32(out int bits))
            {
                value = 0f;
                return false;
            }
            value = BitConverter.Int32BitsToSingle(bits);
            return true;
        }

        public bool TryReadGuid(out Guid value)
        {
            if (!Has(BasisModelShareWire.GuidBytes))
            {
                value = default;
                return false;
            }
            value = new Guid(_source.Slice(Position, BasisModelShareWire.GuidBytes));
            Position += BasisModelShareWire.GuidBytes;
            return true;
        }

        public bool TryReadPose(out BasisModelPose pose)
        {
            if (!Has(BasisModelShareWire.PoseBytes))
            {
                pose = default;
                return false;
            }
            pose = BasisModelShareWire.ReadPose(_source.Slice(Position));
            Position += BasisModelShareWire.PoseBytes;
            return true;
        }

        public bool TrySkip(int count)
        {
            if (!Has(count))
                return false;
            Position += count;
            return true;
        }

        /// <summary>See <see cref="BasisModelShareWire.TryRead7BitEncodedInt"/>; Position moves only on success.</summary>
        public bool TryRead7BitEncodedInt(out int value)
        {
            int offset = Position;
            if (!BasisModelShareWire.TryRead7BitEncodedInt(_source, ref offset, out value))
                return false;
            Position = offset;
            return true;
        }

        private bool Has(int count)
        {
            return count >= 0 && (uint)Position <= (uint)_source.Length && count <= _source.Length - Position;
        }
    }
}
