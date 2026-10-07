using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Where dropped models appear, and how low a batch may reach. A model stands upright with its front (+Z)
    /// turned back toward the viewer: glTF's front is +Z, so 3D content stands level and shows its front.
    /// </summary>
    public static class BasisModelSpawnPose
    {
        private const float DegenerateSquared = 1e-6f;

        /// <summary>
        /// <paramref name="distanceMeters"/> in front of the local camera. <paramref name="viewRight"/> is the
        /// horizontal right of the viewer, along which a batch lays out its columns. With no camera: the origin,
        /// identity and world right.
        /// </summary>
        public static void GetSpawnPose(float distanceMeters, out Vector3 position, out Quaternion rotation, out Vector3 viewRight)
        {
            // The driver's own pose API (its cached transform) rather than Instance.transform. The no-camera branch
            // stays separate: its fallback is not what GetSpawnPoseFrom(zero, identity) would give.
            if (BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.Instance != null)
            {
                BasisLocalCameraDriver.GetPositionAndRotation(out Vector3 cameraPosition, out Quaternion cameraRotation);
                GetSpawnPoseFrom(cameraPosition, cameraRotation, distanceMeters, out position, out rotation, out viewRight);
                return;
            }

            position = Vector3.zero;
            rotation = Quaternion.identity;
            viewRight = Vector3.right;
        }

        /// <summary><see cref="GetSpawnPose"/> for a given camera pose.</summary>
        public static void GetSpawnPoseFrom(
            Vector3 cameraPosition,
            Quaternion cameraRotation,
            float distanceMeters,
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 viewRight
        )
        {
            Vector3 forward = cameraRotation * Vector3.forward;
            position = cameraPosition + forward * distanceMeters;

            Vector3 up = Vector3.up;
            Vector3 flat = Vector3.ProjectOnPlane(forward, up);
            if (flat.sqrMagnitude < DegenerateSquared)
            {
                // Looking straight down or up: the head's top (or chin) still points the way the player faces.
                flat = Vector3.ProjectOnPlane(cameraRotation * (forward.y > 0f ? Vector3.down : Vector3.up), up);
            }
            if (flat.sqrMagnitude < DegenerateSquared)
                flat = Vector3.forward;
            flat.Normalize();

            rotation = Quaternion.LookRotation(-flat, up);
            viewRight = Vector3.Cross(up, flat).normalized;
        }

        /// <summary>
        /// The lowest centre a batch item may have: the player's feet, plus half the item, plus clearance, so
        /// the bottom row of a big drop never lands in the floor. With no local player, 1.5 m below the batch.
        /// </summary>
        public static float MinimumBatchCenterY(float batchCenterY, float halfItemHeightMeters, float groundClearanceMeters)
        {
            float playerGroundY =
                BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.transform.position.y : batchCenterY - 1.5f;
            return playerGroundY + halfItemHeightMeters + groundClearanceMeters;
        }
    }
}
