using Basis.Scripts.BasisSdk.Interactions;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Movement authority for one networked pickup. The controller (whoever grabbed it last) sends its pose;
    /// everyone else follows the latest pose they were sent, eased so a 15 Hz stream looks continuous.
    /// Callers validate remote poses and scales with <see cref="BasisModelPoseValidation"/> before handing
    /// them in; this class trusts what it is given.
    ///
    /// The manager eases every follower at once in <see cref="BasisModelFollowJob"/> and samples controllers there
    /// too; <see cref="Simulate"/> is the same step for one root on the main thread.
    /// </summary>
    public sealed class BasisModelTransformSync
    {
        public bool IsController;
        public bool HasRemoteTarget;

        /// <summary>
        /// The follower has reached its target and is left alone. Cleared by a new target, a demotion and anything
        /// else that moves the root (<see cref="BasisModelPickupObject.LiftAboveGround"/>).
        /// </summary>
        public bool Settled;

        public Vector3 TargetPosition;
        public Quaternion TargetRotation = Quaternion.identity;
        public float TargetScale = 1f;

        public float LastSendTime;
        public Vector3 LastSentPosition;
        public Quaternion LastSentRotation = Quaternion.identity;
        public float LastSentScale = 1f;

        /// <summary>A follower with somewhere still to go.</summary>
        public bool NeedsFollow => !IsController && HasRemoteTarget && !Settled;

        /// <summary>The controller, with its send interval elapsed: its pose is worth reading this frame.</summary>
        public bool CanSendAt(float now, float interval)
        {
            return IsController && now - LastSendTime >= interval;
        }

        /// <summary>Eases a follower toward its target. Frame-rate independent: two half steps equal one full step.</summary>
        public void Simulate(Transform root, float deltaTime)
        {
            if (!NeedsFollow)
                return;
            root.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            float scale = root.localScale.x;
            float before = scale;
            Settled = BasisModelFollowJob.Step(
                ref position,
                ref rotation,
                ref scale,
                TargetPosition,
                TargetRotation,
                TargetScale,
                BasisModelMath.RemoteTransformLerpFactor(deltaTime)
            );
            root.SetPositionAndRotation(position, rotation);
            if (scale != before)
                root.localScale = new Vector3(scale, scale, scale);
        }

        /// <summary>The first target snaps, so a late joiner's copy does not glide in from the origin.</summary>
        public void SetRemoteTarget(Transform root, Vector3 position, Quaternion rotation, float scale)
        {
            TargetPosition = position;
            TargetRotation = rotation;
            TargetScale = scale;
            Settled = false;
            if (HasRemoteTarget)
                return;
            root.SetPositionAndRotation(position, rotation);
            root.localScale = new Vector3(scale, scale, scale);
            HasRemoteTarget = true;
        }

        public void Promote()
        {
            IsController = true;
        }

        /// <summary>
        /// Someone else took the pickup. If the local player is still holding it they are made to let go, and
        /// the release keeps it kinematic (no throw velocity): it now follows the new controller. The current pose
        /// becomes the target so it holds still until their first transform lands.
        /// </summary>
        public void Demote(Transform root, Rigidbody body, BasisPickupInteractable interactable)
        {
            IsController = false;
            Settled = false;
            if (interactable != null && interactable.Inputs.AnyInteracting(false))
            {
                // OnInteractEnd restores this and skips the release velocity for a kinematic body.
                interactable._previousKinematicValue = true;
                interactable.Drop();
            }

            root.GetPositionAndRotation(out TargetPosition, out TargetRotation);
            TargetScale = root.localScale.x;
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
        }

        /// <summary>
        /// For the controller, at most once per <paramref name="interval"/>: the current pose when it moved more
        /// than the settings' epsilons since the last send, recorded as sent. False when there is nothing to send.
        /// </summary>
        public bool TryTakeSend(
            Transform root,
            float now,
            float interval,
            out Vector3 position,
            out Quaternion rotation,
            out float scale
        )
        {
            position = default;
            rotation = Quaternion.identity;
            scale = 1f;
            if (!CanSendAt(now, interval))
                return false;

            root.GetPositionAndRotation(out position, out rotation);
            scale = root.localScale.x;
            return TryTakeSend(position, rotation, scale, now, interval);
        }

        /// <summary>
        /// <see cref="TryTakeSend(Transform,float,float,out Vector3,out Quaternion,out float)"/> for a pose already
        /// read this frame: the manager samples its controllers in the follow pass.
        /// </summary>
        public bool TryTakeSend(Vector3 position, Quaternion rotation, float scale, float now, float interval)
        {
            if (!CanSendAt(now, interval))
                return false;
            bool moved =
                (position - LastSentPosition).sqrMagnitude
                    > BasisModelShareSettings.MovedPositionEpsilon * BasisModelShareSettings.MovedPositionEpsilon
                || Quaternion.Angle(rotation, LastSentRotation) > BasisModelShareSettings.MovedRotationEpsilonDegrees
                || Mathf.Abs(scale - LastSentScale) > BasisModelShareSettings.MovedScaleEpsilon;
            if (!moved)
                return false;

            LastSendTime = now;
            LastSentPosition = position;
            LastSentRotation = rotation;
            LastSentScale = scale;
            return true;
        }
    }
}
