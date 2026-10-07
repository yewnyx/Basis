using System;

namespace Basis.ModelPickup
{
    /// <summary>Outcome of offering one chunk to a <see cref="BasisModelInboundAssembly"/>.</summary>
    public enum BasisModelChunkStatus : byte
    {
        Accepted,
        Duplicate,
        WrongSender,
        IndexOutOfRange,
        ImpossibleLength,
        OffsetOutOfRange,
        LengthMismatch,
        Truncated,
    }

    /// <summary>What <see cref="BasisModelInboundAssembly.CheckExpiry"/> wants the owning manager to do.</summary>
    public enum BasisModelTransferHealth : byte
    {
        Active,

        /// <summary>No new chunk for the stall interval. Reported once per transfer; log it and keep waiting.</summary>
        StallWarning,

        /// <summary>Drop the transfer (and whatever placeholder it raised).</summary>
        Expired,
    }

    /// <summary>
    /// Reassembly state for one inbound chunked transfer. The manager subclasses it with the model's own
    /// fields and keeps the logging and teardown; the acceptance rules live here, engine-free and tested:
    /// only the owner's chunks count, each index lands once at exactly the expected length (the last chunk
    /// short), and only a chunk never seen before refreshes the deadline — so a peer replaying one chunk
    /// cannot keep a dead transfer alive.
    ///
    /// The payload buffer is allocated on the first accepted chunk, not at the header, so headers that never
    /// send data cost a reservation but no memory, and <see cref="ZeroChunkTimeoutSeconds"/> retires them.
    /// </summary>
    public class BasisModelInboundAssembly
    {
        public ushort Sender;
        public Guid Id;

        /// <summary>Null until the first chunk is accepted; <see cref="TotalBytes"/> long after that.</summary>
        public byte[] Buffer;

        public bool[] Received;
        public int TotalBytes;
        public int ReceivedCount;
        public int TotalChunks;
        public int ChunkPayloadBytes;

        /// <summary>When <see cref="Initialize"/> ran; the zero-chunk timeout counts from here.</summary>
        public float StartTime;

        /// <summary>
        /// How long a transfer may wait for its first chunk. Defaults to 30 s, the ordinary deadline; the manager
        /// sets its own.
        /// </summary>
        public float ZeroChunkTimeoutSeconds = 30f;

        public float Deadline;
        public float LastProgressTime;
        public BasisModelTransferRate Rate;
        public bool RejectionLogged;
        public bool StallLogged;

        /// <summary>Bytes this transfer holds in <see cref="BasisModelInboundReservations"/>; see its ownership rule.</summary>
        public long ReservedBytes;

        public bool IsComplete => ReceivedCount >= TotalChunks;

        /// <summary>
        /// Arms the transfer. Leaves <see cref="ReservedBytes"/> and <see cref="ZeroChunkTimeoutSeconds"/> alone,
        /// so they may be set before or after.
        /// </summary>
        public void Initialize(
            ushort sender,
            Guid id,
            int totalBytes,
            int totalChunks,
            int chunkPayloadBytes,
            float now,
            float timeoutSeconds
        )
        {
            if (totalBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, "A transfer needs at least one byte.");
            if (chunkPayloadBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkPayloadBytes), chunkPayloadBytes, "Chunks need a positive size.");
            int expectedChunks = BasisModelShareWire.ExpectedChunkCount(totalBytes, chunkPayloadBytes);
            if (totalChunks != expectedChunks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(totalChunks),
                    totalChunks,
                    $"{totalBytes} bytes in {chunkPayloadBytes}-byte chunks is {expectedChunks} chunks."
                );
            }

            Sender = sender;
            Id = id;
            Buffer = null;
            Received = new bool[totalChunks];
            TotalBytes = totalBytes;
            ReceivedCount = 0;
            TotalChunks = totalChunks;
            ChunkPayloadBytes = chunkPayloadBytes;
            StartTime = now;
            Deadline = now + timeoutSeconds;
            LastProgressTime = now;
            Rate = default;
            RejectionLogged = false;
            StallLogged = false;
        }

        /// <summary>
        /// Offers one chunk. <paramref name="packet"/> is the whole chunk message; its payload starts at
        /// <see cref="BasisModelShareWire.ChunkHeaderBytes"/>. Sender, index, length and offset are checked
        /// before truncation and duplicates, and only <see cref="BasisModelChunkStatus.Accepted"/> changes any state. Check <see cref="IsComplete"/>
        /// after Accepted or Duplicate.
        /// </summary>
        public BasisModelChunkStatus Accept(
            ushort senderId,
            byte[] packet,
            int chunkIndex,
            int length,
            float now,
            float timeoutSeconds
        )
        {
            if (senderId != Sender)
                return BasisModelChunkStatus.WrongSender;
            if (chunkIndex < 0 || chunkIndex >= TotalChunks)
                return BasisModelChunkStatus.IndexOutOfRange;
            if (length <= 0 || length > ChunkPayloadBytes)
                return BasisModelChunkStatus.ImpossibleLength;

            long offset = (long)chunkIndex * ChunkPayloadBytes;
            if (offset < 0 || offset >= TotalBytes)
                return BasisModelChunkStatus.OffsetOutOfRange;
            if (length != ExpectedLength(offset))
                return BasisModelChunkStatus.LengthMismatch;

            int packetLength = packet != null ? packet.Length : 0;
            if (packetLength - BasisModelShareWire.ChunkHeaderBytes < length)
                return BasisModelChunkStatus.Truncated;

            if (Received[chunkIndex])
                return BasisModelChunkStatus.Duplicate;

            if (Buffer == null)
                Buffer = new byte[TotalBytes];
            Deadline = now + timeoutSeconds;
            LastProgressTime = now;
            System.Buffer.BlockCopy(packet, BasisModelShareWire.ChunkHeaderBytes, Buffer, (int)offset, length);
            Received[chunkIndex] = true;
            ReceivedCount++;
            Rate.MovedBytes += length;
            return BasisModelChunkStatus.Accepted;
        }

        /// <summary>
        /// Expired once the deadline passes, or when no chunk has arrived <see cref="ZeroChunkTimeoutSeconds"/>
        /// after <see cref="Initialize"/>. Otherwise a stall is reported once, the first time no chunk has
        /// arrived for <paramref name="stallWarningSeconds"/>.
        /// </summary>
        public BasisModelTransferHealth CheckExpiry(float now, float stallWarningSeconds)
        {
            if (now >= Deadline)
                return BasisModelTransferHealth.Expired;
            if (ReceivedCount == 0 && now >= StartTime + ZeroChunkTimeoutSeconds)
                return BasisModelTransferHealth.Expired;
            if (!StallLogged && now - LastProgressTime >= stallWarningSeconds)
            {
                StallLogged = true;
                return BasisModelTransferHealth.StallWarning;
            }
            return BasisModelTransferHealth.Active;
        }

        /// <summary>
        /// The reason clause for a rejected chunk, naming the item as <paramref name="itemNoun"/>. Built only
        /// when a caller is about to log it.
        /// Returns an empty string for <see cref="BasisModelChunkStatus.Accepted"/> and
        /// <see cref="BasisModelChunkStatus.Duplicate"/>, which are not rejections.
        /// </summary>
        public string DescribeRejection(
            BasisModelChunkStatus status,
            ushort senderId,
            int chunkIndex,
            int length,
            int packetLength,
            string itemNoun
        )
        {
            long offset = (long)chunkIndex * ChunkPayloadBytes;
            switch (status)
            {
                case BasisModelChunkStatus.WrongSender:
                    return $"it came from {senderId}, not the owner";
                case BasisModelChunkStatus.IndexOutOfRange:
                    return $"the index is outside 0..{TotalChunks - 1}";
                case BasisModelChunkStatus.ImpossibleLength:
                    return $"it claims an impossible length of {length}";
                case BasisModelChunkStatus.OffsetOutOfRange:
                    return $"offset {offset} falls outside the {TotalBytes}-byte {itemNoun}";
                case BasisModelChunkStatus.LengthMismatch:
                    return $"it claims {length} bytes where {ExpectedLength(offset)} were expected";
                case BasisModelChunkStatus.Truncated:
                    return $"the packet carries {(long)packetLength - BasisModelShareWire.ChunkHeaderBytes} of the {length} bytes it claims";
                default:
                    return string.Empty;
            }
        }

        /// <summary>Returns this transfer's reservation, once; later calls do nothing.</summary>
        public void ReleaseReservation()
        {
            if (ReservedBytes > 0)
                BasisModelInboundReservations.Release(ReservedBytes);
            ReservedBytes = 0;
        }

        private int ExpectedLength(long offset)
        {
            return (int)Math.Min(ChunkPayloadBytes, TotalBytes - offset);
        }
    }
}
