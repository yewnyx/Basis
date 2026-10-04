using System;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class BasisAvatarScaleModifier
    {
        public Vector3 DuringCalibrationScale = Vector3.one;
        public float ApplyScale = 1f;
        public Vector3 FinalScale = Vector3.one;
        [NonSerialized] private Transform scaleTarget;
        private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));
        private static Vector3 SanitizeCalibrationScale(Vector3 v)
        {
            if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z)) return Vector3.one;

            if (v.x == 0f) v.x = 1f;
            if (v.y == 0f) v.y = 1f;
            if (v.z == 0f) v.z = 1f;

            if (v.x < 0f) v.x = Mathf.Abs(v.x);
            if (v.y < 0f) v.y = Mathf.Abs(v.y);
            if (v.z < 0f) v.z = Mathf.Abs(v.z);

            return v;
        }
        public void ReInitialize(Animator animator)
        {
            scaleTarget = animator != null ? animator.transform : null;
            if (animator == null)
            {
                DuringCalibrationScale = Vector3.one;
            }
            else
            {
                DuringCalibrationScale = SanitizeCalibrationScale(animator.transform.localScale);
            }

            ApplyScale = 1f;
            FinalScale = DuringCalibrationScale * ApplyScale;
        }
        public void SetAvatarheightOverride(float scale)
        {
            if (!IsFinite(scale) || scale <= 0f) scale = 1f;

            ApplyScale = scale;
            FinalScale = DuringCalibrationScale * ApplyScale;

            if (scaleTarget != null)
            {
                // ReInitialize samples the Animator root, so the result must be written back to that
                // same transform. BasisAvatar may live on a wrapper object; applying the Animator's
                // authored scale to the wrapper compounds both transforms and makes the rendered body
                // (and every tracker-to-bone reference) too large or too small.
                scaleTarget.localScale = FinalScale;
            }
        }
    }
}
