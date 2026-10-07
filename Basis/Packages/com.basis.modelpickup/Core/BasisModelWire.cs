using System;
using System.Buffers.Binary;
using System.Globalization;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    /// <summary>How the sharer chose to size a model. Informational on the wire: receivers apply the base scale, not this.</summary>
    public enum BasisModelSizeMode : byte
    {
        Fit = 0,
        Original = 1,
    }

    /// <summary>The model-specific bytes after the common spawn prefix and pose.</summary>
    public struct BasisModelSpawnTail
    {
        public BasisModelSizeMode SizeMode;

        /// <summary>Scale on the model holder (glTF metres to world metres). The root's own scale is the user's gesture scale.</summary>
        public float BaseScale;

        public BasisGlbClaims Claims;
    }

    /// <summary>What a receiver keeps about a server-cache offer until it decides to request the model.</summary>
    public struct BasisModelOfferInfo
    {
        public BasisModelSpawnTail Tail;
        public int TotalBytes;

        /// <summary>The header's owner field. The server stamps offers with the recipient's id, so this is only the sharer's claim.</summary>
        public ushort ClaimedOwnerId;
    }

    /// <summary>
    /// Model wire format on top of <see cref="BasisModelShareWire"/>. A spawn is the common prefix with FieldA =
    /// header version and FieldB = tail length, then the pose, then the tail:
    /// <code>
    /// tail 0  u8  sizeMode (0 Fit, 1 Original; anything else reads as Fit)
    /// tail 1  u8×3 reserved, written 0, ignored
    /// tail 4  f32 baseScale
    /// tail 8  88-byte BasisGlbClaims
    /// tail 96 appendix, ignored (later versions may add fields here without breaking v1 receivers)
    /// </code>
    /// The server cache walks the prefix without knowing any of this and replays the tail untouched.
    /// </summary>
    public static class BasisModelWire
    {
        public const string FixedNetworkIdentifier = "BasisModelPickupManager";

        public const int HeaderVersion = 1;
        public const int TailV1Bytes = ClaimsOffset + BasisGlbClaims.EncodedSize;
        public const int SizeModeOffset = 0;
        public const int BaseScaleOffset = 4;
        public const int ClaimsOffset = 8;

        /// <summary>Longest spawn a v1 sender writes: prefix, two-byte name length, a 256-byte name and the tail.</summary>
        public const int MaxV1SpawnBytes = BasisModelShareWire.SpawnFixedBytes + 2 + BasisModelShareWire.MaxOwnerNameUtf8Bytes + TailV1Bytes;

        /// <summary>
        /// Chunk payload for model transfers. Both ends derive chunk offsets from it, so it is a wire constant. 16 KiB
        /// keeps every relayed chunk inside the server's pooled writers.
        /// </summary>
        public const int ChunkPayloadBytes = BasisModelShareSettings.ChunkPayloadBytes;

        /// <summary>Capability announcement: [u8 11][u8 version][u8 flags]. Extra bytes are ignored.</summary>
        public const byte OpHello = 11;
        public const byte HelloVersion = 1;
        public const int HelloBytes = 3;
        public const byte HelloFlagReply = 1 << 0;

        public static bool IsKnownSizeMode(byte value)
        {
            return value == (byte)BasisModelSizeMode.Fit || value == (byte)BasisModelSizeMode.Original;
        }

        /// <summary>Writes the 96-byte v1 tail, zeroing the reserved bytes. Throws if <paramref name="destination"/> is shorter.</summary>
        public static void WriteTail(Span<byte> destination, in BasisModelSpawnTail tail)
        {
            if (destination.Length < TailV1Bytes)
            {
                throw new ArgumentException("A model spawn tail needs " + TailV1Bytes.ToString(CultureInfo.InvariantCulture) + " bytes.",
                    nameof(destination));
            }
            var writer = new BasisModelSpanWriter(destination);
            writer.WriteByte((byte)tail.SizeMode);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteSingle(tail.BaseScale);
            BasisGlbClaims claims = tail.Claims;
            claims.TryWrite(destination.Slice(ClaimsOffset));
        }

        /// <summary>
        /// Decodes the tail of a spawn or offer that <see cref="BasisModelShareWire.TryReadSpawn"/> already parsed, checking in
        /// order: header version, declared tail length against the bytes actually after the pose, base scale finite and
        /// positive, claims well formed. The base scale comes back clamped by <see cref="BasisModelSizing.ClampBaseScale"/>,
        /// so a sender cannot make a model larger or smaller than an honest Fit or Original choice would.
        /// Limits (size, chunk count, claims against the tier, pose) are <see cref="BasisModelAdmission"/>'s job.
        /// </summary>
        public static bool TryReadTail(in BasisModelSpawnHeader header, ReadOnlySpan<byte> message,
            out BasisModelSpawnTail tail, out string error)
        {
            tail = default;
            if (header.TailOffset < 0 || header.TailLength < 0 || header.TailOffset > message.Length - header.TailLength)
            {
                error = "the spawn header does not describe this message";
                return false;
            }
            if (header.FieldA != HeaderVersion)
            {
                error = "unsupported header version " + header.FieldA.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            int declared = header.FieldB;
            if (declared < TailV1Bytes)
            {
                error = "tail is " + declared.ToString(CultureInfo.InvariantCulture) + " bytes; at least "
                    + TailV1Bytes.ToString(CultureInfo.InvariantCulture) + " are required";
                return false;
            }
            if (declared != header.TailLength)
            {
                error = "tail length " + declared.ToString(CultureInfo.InvariantCulture) + " does not match the "
                    + header.TailLength.ToString(CultureInfo.InvariantCulture) + " bytes after the pose";
                return false;
            }

            ReadOnlySpan<byte> bytes = message.Slice(header.TailOffset, TailV1Bytes);
            float baseScale = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(BaseScaleOffset)));
            if (!(baseScale > 0f) || float.IsInfinity(baseScale))
            {
                error = "base scale is not a finite positive number";
                return false;
            }
            if (!BasisGlbClaims.TryRead(bytes.Slice(ClaimsOffset), out BasisGlbClaims claims, out error))
                return false;

            byte sizeMode = bytes[SizeModeOffset];
            tail.SizeMode = IsKnownSizeMode(sizeMode) ? (BasisModelSizeMode)sizeMode : BasisModelSizeMode.Fit;
            tail.BaseScale = BasisModelSizing.ClampBaseScale(baseScale, claims.Bounds.MaxExtent);
            tail.Claims = claims;
            error = null;
            return true;
        }

        /// <summary>Builds a v1 spawn (opcode 1) in one allocation; the tail is staged on the stack.</summary>
        public static byte[] EncodeSpawn(Guid id, ushort ownerId, string ownerName, int totalBytes, int totalChunks,
            in BasisModelPose pose, in BasisModelSpawnTail tail)
        {
            Span<byte> tailBytes = stackalloc byte[TailV1Bytes];
            WriteTail(tailBytes, tail);
            return BasisModelShareWire.EncodeSpawn(BasisModelShareWire.OpSpawn, id, ownerId, ownerName, HeaderVersion, TailV1Bytes,
                totalBytes, totalChunks, pose, tailBytes);
        }

        public static void WriteHello(Span<byte> destination, bool reply)
        {
            if (destination.Length < HelloBytes)
            {
                throw new ArgumentException("A hello needs " + HelloBytes.ToString(CultureInfo.InvariantCulture) + " bytes.",
                    nameof(destination));
            }
            destination[0] = OpHello;
            destination[1] = HelloVersion;
            destination[2] = reply ? HelloFlagReply : (byte)0;
        }

        /// <summary>False for anything shorter than three bytes or with another opcode. Unknown flag bits are ignored.</summary>
        public static bool TryReadHello(ReadOnlySpan<byte> message, out byte version, out bool reply)
        {
            if (message.Length < HelloBytes || message[0] != OpHello)
            {
                version = 0;
                reply = false;
                return false;
            }
            version = message[1];
            reply = (message[2] & HelloFlagReply) != 0;
            return true;
        }
    }
}
