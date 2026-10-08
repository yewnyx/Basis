using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelSpawnPoseTests
    {
        private const float Tolerance = 1e-4f;

        private static void AssertClose(Vector3 actual, Vector3 expected, string message)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(Tolerance), message + ": " + actual + " vs " + expected);
        }

        [Test]
        public void LevelTowardViewerFacesTheCameraAndLevels()
        {
            Vector3 camera = new Vector3(1f, 1.6f, -2f);
            Quaternion look = Quaternion.Euler(35f, 60f, 10f);

            BasisModelSpawnPose.GetSpawnPoseFrom(
                camera,
                look,
                1.5f,
                out Vector3 position,
                out Quaternion rotation,
                out Vector3 viewRight
            );

            Vector3 forward = look * Vector3.forward;
            AssertClose(position, camera + forward * 1.5f, "the spawn point is along the full view, pitch included");

            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            AssertClose(rotation * Vector3.up, Vector3.up, "upright");
            AssertClose(rotation * Vector3.forward, -flat, "its front (+Z) faces back toward the viewer");
            AssertClose(viewRight, Vector3.Cross(Vector3.up, flat).normalized, "columns run to the viewer's right");
            Assert.That(viewRight.y, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void LevelTowardViewerSurvivesLookingStraightDownOrUp()
        {
            Quaternion yaw = Quaternion.Euler(0f, 90f, 0f);
            foreach (float pitch in new[] { 90f, -90f })
            {
                Quaternion look = yaw * Quaternion.Euler(pitch, 0f, 0f);
                BasisModelSpawnPose.GetSpawnPoseFrom(
                    Vector3.zero,
                    look,
                    1f,
                    out _,
                    out Quaternion rotation,
                    out Vector3 viewRight
                );

                // Facing +X: the front points back along -X, and right is -Z.
                AssertClose(rotation * Vector3.up, Vector3.up, "upright at pitch " + pitch);
                AssertClose(rotation * Vector3.forward, Vector3.left, "faces the viewer at pitch " + pitch);
                AssertClose(viewRight, Vector3.back, "right of a viewer facing +X at pitch " + pitch);
            }
        }

        [Test]
        public void TheBatchFloorFallsBackBelowTheBatchWithoutAPlayer()
        {
            Assume.That(Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.Instance == null, "a local player exists");
            Assert.That(BasisModelSpawnPose.MinimumBatchCenterY(3f, 0.25f, 0.05f), Is.EqualTo(3f - 1.5f + 0.25f + 0.05f).Within(1e-6f));
        }
    }
}
