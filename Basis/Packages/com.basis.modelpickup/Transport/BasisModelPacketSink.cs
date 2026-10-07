using System;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Somewhere the model's packets go. In play this is the manager's <see cref="BasisModelNetworkIdentity"/>;
    /// tests record instead. An implementation must be done with <c>buffer</c> before it returns: callers reuse
    /// scratch buffers across sends, which the real transport allows because it copies before returning.
    /// </summary>
    public interface IBasisModelPacketSink
    {
        void Send(byte[] buffer, DeliveryMethod deliveryMethod, ushort[] recipients);
    }

    /// <summary>
    /// Small fixed-size sends, written into static scratch buffers so a transform at 15 Hz per held pickup
    /// costs no allocation. Main thread only.
    /// </summary>
    public static class BasisModelSends
    {
        private static readonly byte[] _transform = new byte[BasisModelShareWire.TransformBytes];
        private static readonly byte[] _idMessage = new byte[BasisModelShareWire.IdMessageBytes];

        /// <summary>Sends a 49-byte transform. <paramref name="scale"/> is the uniform <c>localScale.x</c>.</summary>
        public static void SendTransform(
            this IBasisModelPacketSink sink,
            Guid id,
            Vector3 position,
            Quaternion rotation,
            float scale,
            ushort[] recipients
        )
        {
            BasisModelShareWire.WriteTransform(_transform, id, BasisModelShareConvert.ToSharePose(position, rotation), scale);
            sink.Send(_transform, DeliveryMethod.ReliableOrdered, recipients);
        }

        /// <summary>Sends a 17-byte opcode + GUID message: despawn, claim, or server-cache request.</summary>
        public static void SendIdMessage(this IBasisModelPacketSink sink, byte opcode, Guid id, ushort[] recipients)
        {
            BasisModelShareWire.WriteIdMessage(_idMessage, opcode, id);
            sink.Send(_idMessage, DeliveryMethod.ReliableOrdered, recipients);
        }
    }
}
