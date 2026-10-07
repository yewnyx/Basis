using System;
using System.Collections.Generic;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// One queued send of a model's payload to one cohort. The manager subclasses it to carry the spawn tail
    /// its header needs.
    /// </summary>
    public class BasisModelOutboundTransfer
    {
        public Guid Id;
        public ushort OwnerId;
        public string OwnerName;
        public byte[] Payload;

        /// <summary>Per transfer; both ends must agree, since the receiver derives offsets from it.</summary>
        public int ChunkPayloadBytes;

        public int NextChunkIndex;

        /// <summary>Refreshed from the live root the moment the header goes out, not when the transfer is queued.</summary>
        public Vector3 Position;
        public Quaternion Rotation;

        public ushort[] Recipients;
        public bool HeaderSent;
        public BasisModelTransferRate Rate;

        /// <summary>
        /// Reused chunk packet. The transports copy before returning, so one array serves every chunk of a
        /// transfer; a fresh one per chunk is not affordable at the rates a fast link allows.
        /// </summary>
        public byte[] ChunkBuffer;

        public int TotalChunks =>
            BasisModelShareWire.ExpectedChunkCount(Payload != null ? Payload.Length : 0, ChunkPayloadBytes);
    }

    /// <summary>The manager's half of an outbound transfer: whether it is still wanted, and its header bytes.</summary>
    public interface IBasisModelOutboundSource<T>
        where T : BasisModelOutboundTransfer
    {
        /// <summary>
        /// The pickup's root if the transfer is still current. False drops it silently: the item is gone, or its
        /// payload was replaced and a newer transfer carries the right bytes.
        /// </summary>
        bool TryGetLiveRoot(T transfer, out Transform liveRoot);

        /// <summary>The spawn header. <c>Position</c> and <c>Rotation</c> have just been read from the live root.</summary>
        byte[] EncodeHeader(T transfer, int totalChunks);
    }

    /// <summary>
    /// First-in-first-out chunked sender. Each transfer goes out as an unmetered header, then chunks metered
    /// against the model's uplink budget, then one final transform so receivers place the item
    /// where it is now rather than where it was when the header left. Head-of-line: a transfer finishes before
    /// the next one starts, so a receiver sees whole items appear one at a time.
    /// </summary>
    public sealed class BasisModelOutboundQueue<T>
        where T : BasisModelOutboundTransfer
    {
        /// <summary>The queued transfers, head first. Change it through this class's methods, which keep its order.</summary>
        public readonly Queue<T> Transfers = new Queue<T>();

        public int Count => Transfers.Count;

        /// <summary>Struct enumerator, so progress-label loops allocate nothing.</summary>
        public Queue<T>.Enumerator GetEnumerator()
        {
            return Transfers.GetEnumerator();
        }

        /// <summary>Queues a transfer. Refuses one with no payload, no recipients or no chunk size.</summary>
        public bool Enqueue(T transfer)
        {
            if (
                transfer == null
                || transfer.Payload == null
                || transfer.Payload.Length == 0
                || transfer.Recipients == null
                || transfer.Recipients.Length == 0
                || transfer.ChunkPayloadBytes <= 0
            )
                return false;
            Transfers.Enqueue(transfer);
            return true;
        }

        /// <summary>
        /// Sends up to <paramref name="maxChunks"/> chunk packets. The first refusal from the budget ends the
        /// outbound work for the frame: the bucket is spent, and trying the next transfer would only be refused
        /// too.
        /// </summary>
        public void Process(int maxChunks, IBasisModelPacketSink sink, IBasisModelOutboundSource<T> source)
        {
            if (sink == null)
                throw new ArgumentNullException(nameof(sink));
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int chunksRemaining = maxChunks;
            while (chunksRemaining > 0 && Transfers.Count > 0)
            {
                T transfer = Transfers.Peek();
                if (!source.TryGetLiveRoot(transfer, out Transform root) || root == null)
                {
                    Transfers.Dequeue();
                    continue;
                }

                byte[] payload = transfer.Payload;
                int chunkSize = transfer.ChunkPayloadBytes;
                int totalChunks = BasisModelShareWire.ExpectedChunkCount(payload.Length, chunkSize);
                if (!transfer.HeaderSent)
                {
                    root.GetPositionAndRotation(out transfer.Position, out transfer.Rotation);
                    sink.Send(source.EncodeHeader(transfer, totalChunks), DeliveryMethod.ReliableOrdered, transfer.Recipients);
                    transfer.HeaderSent = true;
                }

                // The cohort's split between direct links and the relay, and its framing, are the same for every
                // chunk of this transfer, so they are worked out once per transfer per frame, not once per chunk
                // (a dictionary lookup per recipient). A P2P session that connects or drops is picked up next
                // frame, which is soon enough for pacing.
                bool split = false;
                int directCount = 0;
                int relayCount = 0;
                int framingBytes = 0;
                while (chunksRemaining > 0 && transfer.NextChunkIndex < totalChunks)
                {
                    int offset = transfer.NextChunkIndex * chunkSize;
                    int length = Math.Min(chunkSize, payload.Length - offset);
                    int packetLength = BasisModelShareWire.ChunkHeaderBytes + length;
                    if (!split)
                    {
                        BasisModelUplink.CountRecipients(transfer.Recipients, out directCount, out relayCount);
                        framingBytes = BasisNetworkGenericMessages.SceneDataFramingBytes(transfer.Recipients);
                        split = true;
                    }
                    if (!BasisModelUplink.TryReserveSendBandwidth(packetLength + framingBytes, directCount, relayCount))
                        return;
                    if (transfer.ChunkBuffer == null || transfer.ChunkBuffer.Length != packetLength)
                        transfer.ChunkBuffer = new byte[packetLength];
                    BasisModelShareWire.WriteChunk(transfer.ChunkBuffer, transfer.Id, transfer.NextChunkIndex, payload, offset, length);
                    sink.Send(transfer.ChunkBuffer, DeliveryMethod.ReliableOrdered, transfer.Recipients);
                    transfer.NextChunkIndex++;
                    transfer.Rate.MovedBytes += length;
                    chunksRemaining--;
                }

                if (transfer.NextChunkIndex >= totalChunks)
                {
                    root.GetPositionAndRotation(out Vector3 finalPosition, out Quaternion finalRotation);
                    sink.SendTransform(transfer.Id, finalPosition, finalRotation, root.localScale.x, transfer.Recipients);
                    Transfers.Dequeue();
                }
            }
        }

        /// <summary>True when a transfer of <paramref name="id"/> to exactly this sorted cohort is still queued.</summary>
        public bool HasPending(Guid id, ushort[] recipients)
        {
            foreach (T transfer in Transfers)
            {
                if (transfer.Id == id && BasisModelReplication.SnapshotsMatch(transfer.Recipients, recipients))
                    return true;
            }
            return false;
        }

        public void Remove(Guid id)
        {
            int count = Transfers.Count;
            for (int i = 0; i < count; i++)
            {
                T transfer = Transfers.Dequeue();
                if (transfer.Id != id)
                    Transfers.Enqueue(transfer);
            }
        }

        /// <summary>Drops a departed player from every cohort, and any transfer left with nobody to send to.</summary>
        public void RemoveRecipient(ushort recipient)
        {
            int count = Transfers.Count;
            for (int i = 0; i < count; i++)
            {
                T transfer = Transfers.Dequeue();
                ushort[] reduced = BasisModelReplication.RemoveRecipient(transfer.Recipients, recipient);
                if (reduced.Length == 0)
                    continue;
                transfer.Recipients = reduced;
                Transfers.Enqueue(transfer);
            }
        }

        public void Clear()
        {
            Transfers.Clear();
        }
    }
}
