using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.ModelPickup
{
    /// <summary>What the follow pass does with one pickup root this frame.</summary>
    public enum BasisModelFollowMode : byte
    {
        /// <summary>Untouched: a settled follower, a controller not due to send, or a destroyed pickup.</summary>
        Idle = 0,

        /// <summary>A follower: eased toward its target, then its pose read back.</summary>
        Follow = 1,

        /// <summary>A controller due to send: its pose is read and nothing is written.</summary>
        Sample = 2,
    }

    /// <summary>
    /// One pickup's slot in the follow pass. The manager stages the mode and target every frame; the job writes
    /// back the pose the root has after the pass, and for a follower whether it has settled.
    /// </summary>
    public struct BasisModelFollowSlot
    {
        public BasisModelFollowMode Mode;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public float TargetScale;

        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;

        /// <summary>1 when a follower reached its target this frame and was snapped onto it.</summary>
        public byte Settled;
    }

    /// <summary>
    /// Eases every following pickup toward its latest remote pose and samples every controller that is due to
    /// send, as one Burst job over all pickup roots. Each pickup root is its own scene root, so the job runs one
    /// hierarchy per worker. Top level and non-generic so Burst and IL2CPP compile it without a
    /// <c>RegisterGenericJobType</c>.
    /// </summary>
    [BurstCompile]
    public struct BasisModelFollowJob : IJobParallelForTransform
    {
        /// <summary>Within this distance squared (1 mm) a follower counts as arrived.</summary>
        public const float SettlePositionSquared = 1e-6f;

        /// <summary>|dot| of the two rotations above cos(0.05°): within a tenth of a degree of the target.</summary>
        public const float SettleRotationDot = 0.99999962f;

        /// <summary>Within this much uniform scale a follower counts as arrived.</summary>
        public const float SettleScale = 1e-4f;

        /// <summary>Indexed like the roots; at least as long. Read and written at the job's own index only.</summary>
        public NativeArray<BasisModelFollowSlot> Slots;

        /// <summary><see cref="BasisModelMath.RemoteTransformLerpFactor"/> for this frame, computed once on the main thread.</summary>
        public float LerpFactor;

        public void Execute(int index, TransformAccess transform)
        {
            BasisModelFollowSlot slot = Slots[index];
            if (slot.Mode == BasisModelFollowMode.Idle)
                return;
            if (!transform.isValid)
            {
                // Nothing was read, so nothing may be sent or settled from this slot.
                slot.Mode = BasisModelFollowMode.Idle;
                Slots[index] = slot;
                return;
            }

            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            float scale = transform.localScale.x;
            if (slot.Mode == BasisModelFollowMode.Follow)
            {
                float before = scale;
                bool settled = Step(
                    ref position,
                    ref rotation,
                    ref scale,
                    slot.TargetPosition,
                    slot.TargetRotation,
                    slot.TargetScale,
                    LerpFactor
                );
                transform.SetPositionAndRotation(position, rotation);
                if (scale != before)
                    transform.localScale = new Vector3(scale, scale, scale);
                slot.Settled = settled ? (byte)1 : (byte)0;
            }

            slot.Position = position;
            slot.Rotation = rotation;
            slot.Scale = scale;
            Slots[index] = slot;
        }

        /// <summary>
        /// One easing step toward the target, frame-rate independent through <paramref name="lerpFactor"/>. Returns
        /// true, with the pose snapped exactly onto the target, once it is within a millimetre, a tenth of a degree
        /// and 1e-4 of scale: from there the follower is left alone instead of being written every frame for
        /// ever-smaller steps. Rotation uses Unity.Mathematics' slerp, since the engine's Quaternion.Slerp is native
        /// and Burst cannot call it; the two agree to float precision.
        /// </summary>
        public static bool Step(
            ref Vector3 position,
            ref Quaternion rotation,
            ref float scale,
            Vector3 targetPosition,
            Quaternion targetRotation,
            float targetScale,
            float lerpFactor
        )
        {
            position = Vector3.Lerp(position, targetPosition, lerpFactor);
            rotation = math.slerp(rotation, targetRotation, math.saturate(lerpFactor));
            scale = Mathf.Lerp(scale, targetScale, lerpFactor);

            if ((position - targetPosition).sqrMagnitude >= SettlePositionSquared)
                return false;
            if (math.abs(Quaternion.Dot(rotation, targetRotation)) <= SettleRotationDot)
                return false;
            if (math.abs(scale - targetScale) >= SettleScale)
                return false;

            position = targetPosition;
            rotation = targetRotation;
            scale = targetScale;
            return true;
        }
    }
}
