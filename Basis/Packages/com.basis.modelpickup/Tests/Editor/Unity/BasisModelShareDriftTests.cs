using System;
using Basis.Network.Core;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    /// <summary>Engine-side facts the engine-free core mirrors, and the conversions at the edge.</summary>
    public sealed class BasisModelShareDriftTests
    {
        [Test]
        public void UnfragmentedPayloadMirrorMatchesTheTransport()
        {
            Assert.That(
                BasisModelShareSettings.MaxUnfragmentedPayloadBytes,
                Is.EqualTo(BasisNetworkCommons.MaxUnfragmentedPayload)
            );
        }

        [Test]
        public void ConvertRoundTripsVectorsAndQuaternions()
        {
            var position = new Vector3(1.25f, -2.5f, 1e-7f);
            Quaternion rotation = Quaternion.Euler(10f, 200f, -35f);

            Vector3 positionBack = position.ToShare().ToUnity();
            Quaternion rotationBack = rotation.ToShare().ToUnity();
            Assert.That(positionBack.x, Is.EqualTo(position.x));
            Assert.That(positionBack.y, Is.EqualTo(position.y));
            Assert.That(positionBack.z, Is.EqualTo(position.z));
            Assert.That(rotationBack.x, Is.EqualTo(rotation.x));
            Assert.That(rotationBack.y, Is.EqualTo(rotation.y));
            Assert.That(rotationBack.z, Is.EqualTo(rotation.z));
            Assert.That(rotationBack.w, Is.EqualTo(rotation.w));

            BasisModelPose pose = BasisModelShareConvert.ToSharePose(position, rotation);
            Assert.That(pose.Position.X, Is.EqualTo(position.x));
            Assert.That(pose.Rotation.W, Is.EqualTo(rotation.w));
            pose.ToUnity(out Vector3 posePosition, out Quaternion poseRotation);
            Assert.That(posePosition, Is.EqualTo(position));
            Assert.That(poseRotation.w, Is.EqualTo(rotation.w));
        }

        [Test]
        public void SendsUseTheSharedWireLayout()
        {
            var sink = new RecordingPacketSink();
            var id = BasisModelShareTestIds.Make(1);
            ushort[] recipients = { 3, 4 };

            sink.SendTransform(id, new Vector3(1f, 2f, 3f), Quaternion.identity, 1.5f, recipients);
            sink.SendIdMessage(BasisModelShareWire.OpDespawn, id, null);

            Assert.That(sink.Count, Is.EqualTo(2));
            Assert.That(sink.Packets[0].Length, Is.EqualTo(BasisModelShareWire.TransformBytes));
            Assert.That(sink.Methods[0], Is.EqualTo(DeliveryMethod.ReliableOrdered));
            Assert.That(sink.Recipients[0], Is.EqualTo(recipients));
            Assert.That(BasisModelShareWire.TryReadTransform(sink.Packets[0], out Guid readId, out BasisModelPose pose, out float scale), Is.True);
            Assert.That(readId, Is.EqualTo(id));
            Assert.That(pose.Position.Y, Is.EqualTo(2f));
            Assert.That(scale, Is.EqualTo(1.5f));

            Assert.That(sink.Packets[1].Length, Is.EqualTo(BasisModelShareWire.IdMessageBytes));
            Assert.That(sink.Packets[1][0], Is.EqualTo(BasisModelShareWire.OpDespawn));
            Assert.That(BasisModelShareWire.TryReadIdMessage(sink.Packets[1], out Guid despawned), Is.True);
            Assert.That(despawned, Is.EqualTo(id));
            Assert.That(sink.Recipients[1], Is.Null);
        }
    }
}
