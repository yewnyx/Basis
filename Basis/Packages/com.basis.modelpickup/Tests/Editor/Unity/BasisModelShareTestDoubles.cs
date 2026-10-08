using System;
using System.Collections.Generic;
using Basis.Network.Core;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// Records every send, copying the buffer: callers reuse scratch and chunk buffers across sends, exactly as
    /// the real transport lets them.
    /// </summary>
    internal sealed class RecordingPacketSink : IBasisModelPacketSink
    {
        public readonly List<byte[]> Packets = new List<byte[]>();
        public readonly List<ushort[]> Recipients = new List<ushort[]>();
        public readonly List<DeliveryMethod> Methods = new List<DeliveryMethod>();

        public int Count => Packets.Count;

        public void Send(byte[] buffer, DeliveryMethod deliveryMethod, ushort[] recipients)
        {
            Packets.Add(buffer != null ? (byte[])buffer.Clone() : null);
            Recipients.Add(recipients != null ? (ushort[])recipients.Clone() : null);
            Methods.Add(deliveryMethod);
        }

        public void Clear()
        {
            Packets.Clear();
            Recipients.Clear();
            Methods.Clear();
        }

        public int CountOpcode(byte opcode)
        {
            int count = 0;
            for (int i = 0; i < Packets.Count; i++)
            {
                if (Packets[i] != null && Packets[i].Length > 0 && Packets[i][0] == opcode)
                    count++;
            }
            return count;
        }
    }

    internal static class BasisModelShareTestIds
    {
        /// <summary>Distinct, recognisable GUIDs so a failure message names which item went wrong.</summary>
        public static Guid Make(int index)
        {
            return new Guid(index, 0x5ead, 0x0bee, 1, 2, 3, 4, 5, 6, 7, 8);
        }
    }
}
