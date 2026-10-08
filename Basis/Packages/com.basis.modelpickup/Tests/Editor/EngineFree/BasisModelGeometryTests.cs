using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelGeometryTests
    {
        [Test]
        public void DistanceSquaredSumsTheAxesAndIgnoresOrder()
        {
            var a = new BasisModelVec3(1f, 2f, 3f);
            var b = new BasisModelVec3(4f, 6f, 15f);

            Assert.That(BasisModelVec3.DistanceSquared(a, b), Is.EqualTo(9f + 16f + 144f));
            Assert.That(BasisModelVec3.DistanceSquared(b, a), Is.EqualTo(BasisModelVec3.DistanceSquared(a, b)));
            Assert.That(BasisModelVec3.DistanceSquared(a, a), Is.EqualTo(0f));
        }

        [Test]
        public void IdentityIsAUnitQuaternion()
        {
            BasisModelQuat identity = BasisModelQuat.Identity;

            Assert.That(identity.X, Is.EqualTo(0f));
            Assert.That(identity.Y, Is.EqualTo(0f));
            Assert.That(identity.Z, Is.EqualTo(0f));
            Assert.That(identity.W, Is.EqualTo(1f));
            Assert.That(identity.LengthSquared, Is.EqualTo(1f));
            Assert.That(new BasisModelQuat(1f, 2f, 3f, 4f).LengthSquared, Is.EqualTo(30f));
        }

        [Test]
        public void PoseKeepsBothParts()
        {
            var pose = new BasisModelPose(new BasisModelVec3(1f, 2f, 3f), new BasisModelQuat(0f, 1f, 0f, 0f));

            Assert.That(pose.Position.Z, Is.EqualTo(3f));
            Assert.That(pose.Rotation.Y, Is.EqualTo(1f));
        }
    }
}
