using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Interactions
{
    public class BasisPickupAxisConstraintTests
    {
        private GameObject _parent;
        private GameObject _pickup;

        [TearDown]
        public void TearDown()
        {
            if (_pickup != null) Object.DestroyImmediate(_pickup);
            if (_parent != null) Object.DestroyImmediate(_parent);
        }

        [Test]
        public void RotatedParent_KeepsWorldRotationWhenAxisIsLocked()
        {
            _parent = new GameObject("rotated-parent");
            _parent.transform.SetPositionAndRotation(new Vector3(2f, 0f, -3f), Quaternion.Euler(0f, 180f, 0f));

            _pickup = new GameObject("pickup");
            _pickup.transform.SetParent(_parent.transform, false);
            _pickup.transform.SetLocalPositionAndRotation(new Vector3(0f, 1f, 0.5f), Quaternion.Euler(10f, 20f, 30f));

            Vector3 startLocalPosition = _pickup.transform.localPosition;
            Quaternion expectedWorldRotation = _pickup.transform.rotation;
            Vector3 proposedPosition = _parent.transform.TransformPoint(startLocalPosition + new Vector3(0.1f, 2f, 3f));
            Quaternion proposedRotation = Quaternion.identity;

            BasisPickupInteractable.ConstrainPoseToAxis(_pickup.transform, BasisAxisType.X, startLocalPosition,
                0.2f, 0.2f, ref proposedPosition, ref proposedRotation);

            Vector3 constrainedLocalPosition = _parent.transform.InverseTransformPoint(proposedPosition);
            Assert.That(constrainedLocalPosition.x, Is.EqualTo(startLocalPosition.x + 0.1f).Within(0.0001f));
            Assert.That(constrainedLocalPosition.y, Is.EqualTo(startLocalPosition.y).Within(0.0001f));
            Assert.That(constrainedLocalPosition.z, Is.EqualTo(startLocalPosition.z).Within(0.0001f));
            Assert.That(Quaternion.Angle(proposedRotation, expectedWorldRotation), Is.LessThan(0.001f));
        }
    }
}
