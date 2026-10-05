using UnityEngine;
using static RalivIKSpine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        void SolveSpinePass()
        {
            SolveSpine();
        }
        public void SolveSpine()
        {
            BasisEerieMarkers.SpineHipsPlacement.Begin();

            if (plan.prone)
            {
                if (plan.hasHips && plan.hasHead)
                {
                    ApplyProneBodyYaw();
                }
            }
            else
            {
                ResetSpineChainToRest();
                Vector3 headTargetPos = targetPositionHead, hipsTargetPos = targetPositionHips;
                Quaternion hipsTargetRot = targetRotationHips;
                Quaternion offsetHips = offsetRotationHips, hipDesired = hipsTargetRot * offsetHips;
                Vector3 up = playerUp;
                float crouchFade = 1f;
                if (plan.crouchOffset)
                {
                    hipsTargetPos = ApplyCrouchBodyOffset(headTargetPos, hipsTargetPos, hipDesired, up, crouchFade);
                }
                targetPositionHips = hipsTargetPos;
                if (plan.hasHips)
                {
                    poseStream.SetPosition(handleHips, hipsTargetPos);
                    poseStream.SetRotation(handleHips, hipDesired);
                }
            }
            BasisEerieMarkers.SpineHipsPlacement.End();
            if (plan.hasSpineChain && (plan.chestChain || plan.headChain))
            {
                BasisEerieMarkers.SpineSequentialIK.Begin();
                SolveRalivSpineIK(targetPositionHead, targetRotationHead * offsetRotationHead);
                BasisEerieMarkers.SpineSequentialIK.End();
            }
        }
        void SolveRalivSpineIK(Vector3 headTargetPosition, Quaternion headTargetRotation)
        {
            int count = chainHeadToSpine.Length;
            if (count < 2)
            {
                return;
            }
            if (SpineData.length <= epsilon || SpineData.positions.Length != count)
            {
                InitalizeRalivSpineIK();
            }
            if (SpineData.length <= epsilon || SpineData.positions.Length != count)
            {
                return;
            }
            SpineData.hipTargetPosition = plan.hasHips ? poseStream.GetPosition(handleHips) : SpineData.positions[0];
            SpineData.hipTargetRotation = plan.hasHips ? poseStream.GetRotation(handleHips) : SpineData.rotations[0];
            SpineData.headTargetPosition = headTargetPosition;
            SpineData.headTargetRotation = headTargetRotation;
            int chestIndex = count - 1 - plan.chestIdx;
            bool hasTrackedChest = plan.chestTracked && plan.chestChain && chestIndex > 0 && chestIndex < count - 1;
            SpineData.chestIndex = chestIndex;
            SpineData.chestTargetPosition = targetPositionChest;
            SpineData.chestTargetRotation = targetRotationChest * offsetRotationChest;
            SpineData.chestForward = Vector3.forward;
            SpineData.chestHintWeight = hasTrackedChest ? 1f : 0f;
            //SpineData.chestPositionWeight = hasTrackedChest ? Mathf.Clamp01(chestIkWeight) : 0f;

            RalivIKSpine.SolveSpine(ref SpineData);

            // Preserve the authored local translations of every vertebra. Writing
            // world positions into each mapped bone mutates local bone lengths and
            // breaks avatars with helper transforms between humanoid spine bones.
            BasisBoneHandle rootHandle = chainHeadToSpine[count - 1];
            poseStream.SetPosition(rootHandle, SpineData.positions[0]);
            for (int index = 0; index < count; index++)
            {
                BasisBoneHandle handle = chainHeadToSpine[count - 1 - index];
                poseStream.SetRotation(handle, SpineData.rotations[index]);
            }

            BasisBoneHandle headHandle = chainHeadToSpine[0];
            Vector3 headCorrection = headTargetPosition - poseStream.GetPosition(headHandle);
            if (headCorrection.sqrMagnitude > sqrEpsilon)
            {
                poseStream.SetPosition(rootHandle, poseStream.GetPosition(rootHandle) + headCorrection);
            }
        }
        public void InitalizeRalivSpineIK()
        {
            int count = chainHeadToSpine.Length;
            if (count < 2 || !poseStream.LocalPosition.IsCreated)
            {
                return;
            }
            SpineData = default;
            SpineData.positions.Length = count;
            SpineData.rotations.Length = count;
            SpineData.restPositions.Length = count;
            SpineData.restRotations.Length = count;
            SpineData.t.Length = count;
            SpineData.targetSpinePositions.Length = count;
            SpineData.hipTargetAlignedSpinePositions.Length = count;
            SpineData.headTargetAlignedSpinePositions.Length = count;

            float hiplessSpineLength = 0f;
            for (int index = 0; index < count; index++)
            {
                BasisBoneHandle handle = chainHeadToSpine[count - 1 - index];
                if (!poseStream.IsValid(handle))
                {
                    SpineData = default;
                    return;
                }
                poseStream.GetPositionAndRotation(handle, out Vector3 position, out Quaternion rotation);
                SpineData.positions[index] = position;
                SpineData.rotations[index] = rotation;
                SpineData.restPositions[index] = position;
                SpineData.restRotations[index] = rotation;

                // Match SpineTest: do not include the hips-to-first-spine-bone
                // segment in the interpolation length.
                if (index > 1)
                {
                    hiplessSpineLength += (SpineData.restPositions[index] - SpineData.restPositions[index - 1]).magnitude;
                }
                SpineData.t[index] = hiplessSpineLength;
            }

            if (hiplessSpineLength <= epsilon)
            {
                SpineData = default;
                return;
            }
            SpineData.length = hiplessSpineLength;
            for (int index = 0; index < count; index++)
            {
                SpineData.t[index] = SpineData.t[index] / hiplessSpineLength * 0.8f;
            }

            SpineData.hipTargetPosition = SpineData.positions[0];
            SpineData.hipTargetRotation = SpineData.rotations[0];
            SpineData.headTargetPosition = SpineData.positions[count - 1];
            SpineData.headTargetRotation = SpineData.rotations[count - 1];
            SpineData.chestTargetPosition = SpineData.positions[Mathf.Clamp(count - 1 - chainChestIdx, 0, count - 1)];
            SpineData.chestForward = Vector3.forward;
        }

        void RescaleRalivSpineIK(float scale)
        {
            int count = SpineData.restPositions.Length;
            if (count < 2 || SpineData.positions.Length != count ||
                !(scale > 0f) || float.IsNaN(scale) || float.IsInfinity(scale))
            {
                return;
            }

            Vector3 restRoot = SpineData.restPositions[0];
            Vector3 workingRoot = SpineData.positions[0];
            for (int index = 1; index < count; index++)
            {
                SpineData.restPositions[index] = restRoot + (SpineData.restPositions[index] - restRoot) * scale;
                SpineData.positions[index] = workingRoot + (SpineData.positions[index] - workingRoot) * scale;
            }
            SpineData.length *= scale;
        }
        void ResetSpineChainToRest()
        {
            if (!plan.hasSpineChain)
            {
                return;
            }
            for (int i = chainHeadToSpine.Length - 1; i >= 0; i--)
            {
                poseStream.ResetToRest(chainHeadToSpine[i]);
            }
        }
        void ApplyProneBodyYaw()
        {
            Vector3 up = playerUp, hipsPos = poseStream.GetPosition(handleHips);
            Vector3 headPos = poseStream.GetPosition(handleHead), bodyFwd = headPos - hipsPos;
            bodyFwd -= up * Vector3.Dot(bodyFwd, up);
            Vector3 desiredFwd = targetRotationHips * Vector3.forward;
            desiredFwd -= up * Vector3.Dot(desiredFwd, up);
            if (bodyFwd.sqrMagnitude < sqrEpsilon || desiredFwd.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            float deltaYaw = Vector3.SignedAngle(bodyFwd, desiredFwd, up);
            Quaternion swing = Quaternion.AngleAxis(deltaYaw, up);
            Vector3 toTarget = targetPositionHead - headPos;
            toTarget -= up * Vector3.Dot(toTarget, up);
            poseStream.SetPosition(handleHips, headPos + swing * (hipsPos - headPos) + toTarget);
            poseStream.SetRotation(handleHips, swing * poseStream.GetRotation(handleHips));
        }
        void ReachHeadJoint(int i, Vector3 headTargetPos, int firstJoint, int chestIdx, float jointSpan, Vector3 ccdUp)
        {
            const int tipIdx = 0;
            Vector3 jointPos = poseStream.GetPosition(chainHeadToSpine[i]);
            Vector3 curTipPos = poseStream.GetPosition(chainHeadToSpine[tipIdx]), cur = curTipPos - jointPos;
            Vector3 tgt = headTargetPos - jointPos;
            if (cur.sqrMagnitude < sqrEpsilon || tgt.sqrMagnitude < sqrEpsilon)
                return;

            Quaternion delta = BasisQuaternionExt.FromToRotation(cur, tgt);
            float t = (i - firstJoint) / jointSpan, jointTwistKeep = Mathf.Lerp(spineNeckTwistKeep, spineTwistKeep, t);
            float jointSwingScale = 1f - thoracicBendStiffen * (1f - Mathf.Abs(2f * t - 1f));
            delta = BasisTwistSolveCore.ShapeReachStep(delta, ccdUp, jointTwistKeep, jointSwingScale);
            delta = Quaternion.Slerp(Quaternion.identity, delta, spineCCDRelax);
            poseStream.SetRotation(chainHeadToSpine[i], delta * poseStream.GetRotation(chainHeadToSpine[i]));

            if (i == firstJoint)
            {
                ClampNeckCone(i, neckMaxConeDeg);
            }
            else if (i == chestIdx)
            {
                ClampChestCone(i, maxChestDeltaDeg);
            }

            GuardSpineJoint(i);
        }
        const int ChestBarrierProbes = 5;
        void BlendLumbarAndRestoreHead(Quaternion lumbarBefore, Quaternion lumbarAfter, float w, Vector3 headTargetPos, int firstJoint, int lastJoint, int chestBoneIdx, float jointSpan, Vector3 ccdUp)
        {
            poseStream.SetRotation(chainHeadToSpine[lastJoint], Quaternion.Slerp(lumbarBefore, lumbarAfter, w));
            GuardSpineJoint(lastJoint);
            RestoreHeadAboveChest(headTargetPos, firstJoint, lastJoint, chestBoneIdx, jointSpan, ccdUp);
        }
        void RestoreHeadAboveChest(Vector3 headTargetPos, int firstJoint, int lastJoint, int chestBoneIdx, float jointSpan, Vector3 ccdUp)
        {
            for (int sweep = 0; sweep < chestIkHeadRestoreSweeps; sweep++)
            {
                for (int i = lastJoint - 1; i >= firstJoint; i--)
                {
                    ReachHeadJoint(i, headTargetPos, firstJoint, chestBoneIdx, jointSpan, ccdUp);
                }
            }
        }
        void GuardSpineJoint(int i)
        {
            if (!plan.spineRom)
            {
                return;
            }

            BasisSpineRestFrame frame = chainSpineRestFrames[i];
            if (!frame.Valid)
            {
                return;
            }

            int parent = i + 1;
            Quaternion parentRot = poseStream.GetRotation(chainHeadToSpine[parent]);
            Quaternion boneRot = poseStream.GetRotation(chainHeadToSpine[i]);
            Quaternion local = BasisSpineAnatomyCore.Conj(parentRot) * boneRot;
            Quaternion clamped = BasisSpineAnatomyCore.Clamp(local, frame, BasisSpineAnatomy.Rom(frame.Segment), out BasisSpineClampInfo info);
            if (!info.Touched)
            {
                return;
            }

            poseStream.SetRotation(chainHeadToSpine[i], parentRot * clamped);
        }
        void ClampNeckCone(int neckIdx, float maxConeDeg)
        {
            Vector3 chestPos = poseStream.GetPosition(chainHeadToSpine[neckIdx + 1]);
            Vector3 neckPos = poseStream.GetPosition(chainHeadToSpine[neckIdx]);
            Vector3 headPos = poseStream.GetPosition(chainHeadToSpine[0]), parentDir = neckPos - chestPos;
            Vector3 boneDir = headPos - neckPos;
            if (parentDir.sqrMagnitude < sqrEpsilon || boneDir.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            float ang = Vector3.Angle(parentDir, boneDir);
            if (ang <= maxConeDeg)
            {
                return;
            }

            Vector3 axis = Vector3.Cross(boneDir, parentDir);
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            axis.Normalize();
            Quaternion correction = Quaternion.AngleAxis(ang - maxConeDeg, axis);
            poseStream.SetRotation(chainHeadToSpine[neckIdx], correction * poseStream.GetRotation(chainHeadToSpine[neckIdx]));
        }
        void ClampChestCone(int chestIdx, float maxConeDeg)
        {
            Vector3 spinePos = poseStream.GetPosition(chainHeadToSpine[chestIdx + 1]);
            Vector3 chestPos = poseStream.GetPosition(chainHeadToSpine[chestIdx]);
            Vector3 childPos = poseStream.GetPosition(chainHeadToSpine[chestIdx - 1]), parentDir = chestPos - spinePos;
            Vector3 boneDir = childPos - chestPos;
            if (parentDir.sqrMagnitude < sqrEpsilon || boneDir.sqrMagnitude < sqrEpsilon)
                return;

            float ang = Vector3.Angle(parentDir, boneDir);
            if (ang <= maxConeDeg)
                return;

            Vector3 axis = Vector3.Cross(boneDir, parentDir);
            if (axis.sqrMagnitude < sqrEpsilon)
                return;

            axis.Normalize();
            Quaternion correction = Quaternion.AngleAxis(ang - maxConeDeg, axis);
            poseStream.SetRotation(chainHeadToSpine[chestIdx], correction * poseStream.GetRotation(chainHeadToSpine[chestIdx]));
        }
        Vector3 ApplyCrouchBodyOffset(Vector3 headTargetPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUpDir, float fade)
        {
            BasisCrouchOffsetInput input;
            input.HeadTargetPos = headTargetPos;
            input.HipsPos = hipsPos;
            input.HipsRot = hipsRot;
            input.Bind = offsetRotationHips;
            input.PlayerUp = playerUpDir;
            input.Factor = moveBodyBackWhenCrouching;
            input.RestDist = minHeadSpineHeight;
            input.CrouchDepth = crouchDepth;
            input.StandingHeadHeight = standingHeadHeight;
            input.Fade = fade;
            BasisCrouchOffsetCore.Solve(input, out BasisCrouchOffsetResult result);
            return result.HipsPos;
        }
    }
}
