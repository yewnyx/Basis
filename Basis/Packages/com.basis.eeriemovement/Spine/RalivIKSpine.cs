/*
MIT License

Copyright (c) 2026 Raliv and Gator Dragon Games

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using Unity.Collections;
using UnityEngine;

namespace Basis.IK
{
    public static class RalivIKSpine
    {
        public const int Iterations = 10;
        public const float MaxChestRotationDeltaDegrees = 90f;

        public struct SpineData
        {
            public Vector3 HipTargetPosition;
            public Quaternion HipTargetRotation;
            public Vector3 ChestTargetPosition;
            public Quaternion ChestTargetRotation;
            public Vector3 HeadTargetPosition;
            public Quaternion HeadTargetRotation;
            public FixedList512Bytes<Vector3> Positions;
            public FixedList512Bytes<Quaternion> Rotations;
            public FixedList512Bytes<Vector3> RestPositions;
            public FixedList512Bytes<Quaternion> RestRotations;
            public float Length;
            public FixedList128Bytes<float> T;
            public Vector3 ChestForward;
            public float ChestHintWeight;
            public float ChestPositionWeight;
            public int ChestIndex;
        }

        public static void Solve(ref SpineData spineData)
        {
            int count = spineData.Positions.Length;
            if (count < 2 || spineData.Rotations.Length != count || spineData.RestPositions.Length != count || spineData.RestRotations.Length != count || spineData.T.Length != count)
            {
                return;
            }

            int headIndex = count - 1;
            float hipHeadRestDistance = (spineData.RestPositions[headIndex] - spineData.RestPositions[0]).magnitude;
            float totalRestLength = 0f;
            for (int index = 1; index < count; index++)
            {
                totalRestLength += (spineData.RestPositions[index] - spineData.RestPositions[index - 1]).magnitude;
            }
            Vector3 headToHip = spineData.HipTargetPosition - spineData.HeadTargetPosition;
            if (headToHip.sqrMagnitude > BasisEerieMovement.sqrEpsilon)
            {
                headToHip = headToHip.normalized * hipHeadRestDistance;
                spineData.HipTargetPosition = Vector3.Lerp(spineData.HipTargetPosition, spineData.HeadTargetPosition + headToHip, 0.5f);
                Vector3 clampedHeadToHip = spineData.HipTargetPosition - spineData.HeadTargetPosition;
                if (totalRestLength > BasisEerieMovement.epsilon && clampedHeadToHip.sqrMagnitude > totalRestLength * totalRestLength)
                {
                    spineData.HipTargetPosition = spineData.HeadTargetPosition + clampedHeadToHip.normalized * totalRestLength;
                }
            }

            Quaternion hipRotationOffset = Quaternion.Inverse(spineData.RestRotations[0]) * spineData.HipTargetRotation;
            Quaternion headRotationOffset = Quaternion.Inverse(spineData.RestRotations[headIndex]) * spineData.HeadTargetRotation;

            FixedList512Bytes<Vector3> headAligned = default;
            FixedList512Bytes<Vector3> hipAligned = default;
            FixedList512Bytes<Vector3> targets = default;
            headAligned.Length = count;
            hipAligned.Length = count;
            targets.Length = count;

            Quaternion inverseRestHead = Quaternion.Inverse(spineData.RestRotations[headIndex]);
            Quaternion inverseRestHip = Quaternion.Inverse(spineData.RestRotations[0]);
            for (int index = 0; index < count; index++)
            {
                if (index == headIndex)
                {
                    headAligned[index] = spineData.HeadTargetPosition;
                }
                else
                {
                    Vector3 headSpacePosition = inverseRestHead * (spineData.RestPositions[index] - spineData.RestPositions[headIndex]);
                    headAligned[index] = spineData.HeadTargetRotation * headSpacePosition + spineData.HeadTargetPosition;
                }

                if (index == 0)
                {
                    hipAligned[index] = spineData.HipTargetPosition;
                }
                else
                {
                    Vector3 hipSpacePosition = inverseRestHip * (spineData.RestPositions[index] - spineData.RestPositions[0]);
                    hipAligned[index] = spineData.HipTargetRotation * hipSpacePosition + spineData.HipTargetPosition;
                }

                targets[index] = Vector3.Lerp(hipAligned[index], headAligned[index], Mathf.Clamp01(spineData.T[index]));
            }

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                float lengthConstraint = (float)iteration / Iterations;
                float curveConstraint = 1f - lengthConstraint;
                for (int index = 0; index < count; index++)
                {
                    spineData.Positions[index] = Vector3.Lerp(spineData.Positions[index], targets[index], curveConstraint);
                }
                ApplyChestPositionHint(ref spineData, headIndex, curveConstraint);
                for (int index = count - 2; index >= 0; index--)
                {
                    ConstrainSegment(ref spineData, index, index + 1, lengthConstraint);
                }
                for (int index = 1; index < count; index++)
                {
                    ConstrainSegment(ref spineData, index, index - 1, lengthConstraint);
                }
            }

            // Soft FABRIK intentionally eases toward its length constraints and can
            // leave large residual stretch when an endpoint becomes unreachable.
            // Pin the head, then make one exact backward pass so no vertebral
            // segment can exceed its cached rest length.
            spineData.Positions[headIndex] = spineData.HeadTargetPosition;
            for (int index = headIndex - 1; index >= 0; index--)
            {
                ConstrainSegment(ref spineData, index, index + 1, 1f);
            }

            for (int index = 0; index < headIndex; index++)
            {
                Quaternion preRotation = Quaternion.Slerp(hipRotationOffset, headRotationOffset, spineData.T[index]);
                Vector3 restParentOffset = preRotation * (spineData.RestPositions[index + 1] - spineData.RestPositions[index]);
                Vector3 parentOffset = spineData.Positions[index + 1] - spineData.Positions[index];
                Quaternion fromTo = SafeFromToRotation(restParentOffset, parentOffset);
                spineData.Rotations[index] = fromTo * preRotation * spineData.RestRotations[index];
            }
            spineData.Rotations[headIndex] = spineData.HeadTargetRotation;
            ApplyChestTwist(ref spineData, headIndex);
        }

        static void ApplyChestPositionHint(ref SpineData spineData, int headIndex, float curveConstraint)
        {
            int chestIndex = spineData.ChestIndex;
            float positionWeight = Mathf.Clamp01(spineData.ChestPositionWeight);
            if (positionWeight <= 0f || chestIndex <= 0 || chestIndex >= headIndex)
            {
                return;
            }

            // Match Raliv's chest hint: pull the chest point toward the tracker and
            // carry half of that displacement into the next point toward the head.
            Vector3 chestOffset = (spineData.ChestTargetPosition - spineData.Positions[chestIndex]) * positionWeight * curveConstraint;
            spineData.Positions[chestIndex] += chestOffset;
            if (chestIndex + 1 < headIndex)
            {
                spineData.Positions[chestIndex + 1] += chestOffset * 0.5f;
            }
        }

        static void ApplyChestTwist(ref SpineData spineData, int headIndex)
        {
            int chestIndex = spineData.ChestIndex;
            float hintWeight = Mathf.Clamp01(spineData.ChestHintWeight);
            if (hintWeight <= 0f || chestIndex <= 0 || chestIndex >= headIndex)
            {
                return;
            }

            Vector3 twistAxis = spineData.Positions[chestIndex + 1] - spineData.Positions[chestIndex];
            Vector3 chestForward = spineData.ChestForward;
            if (twistAxis.sqrMagnitude <= BasisEerieMovement.sqrEpsilon || chestForward.sqrMagnitude <= BasisEerieMovement.sqrEpsilon)
            {
                return;
            }

            twistAxis.Normalize();
            Vector3 predictedForward = Vector3.ProjectOnPlane(spineData.Rotations[chestIndex] * chestForward, twistAxis);
            Vector3 targetForward = Vector3.ProjectOnPlane(spineData.ChestTargetRotation * chestForward, twistAxis);
            if (predictedForward.sqrMagnitude <= BasisEerieMovement.sqrEpsilon || targetForward.sqrMagnitude <= BasisEerieMovement.sqrEpsilon)
            {
                return;
            }

            float twistDegrees = Mathf.Clamp(
                Vector3.SignedAngle(predictedForward, targetForward, twistAxis),
                -MaxChestRotationDeltaDegrees,
                MaxChestRotationDeltaDegrees) * hintWeight;

            for (int index = chestIndex - 1; index <= chestIndex + 1 && index < headIndex; index++)
            {
                float distribution = index == chestIndex ? 1f : 0.5f;
                Vector3 boneAxis = spineData.Positions[index + 1] - spineData.Positions[index];
                if (boneAxis.sqrMagnitude > BasisEerieMovement.sqrEpsilon)
                {
                    spineData.Rotations[index] = Quaternion.AngleAxis(twistDegrees * distribution, boneAxis.normalized) * spineData.Rotations[index];
                }
            }
        }

        public static Quaternion ConstrainChestRotation(Quaternion predictedRotation, Quaternion targetRotation)
        {
            float angle = Quaternion.Angle(predictedRotation, targetRotation);
            return angle <= MaxChestRotationDeltaDegrees
                ? targetRotation
                : Quaternion.Slerp(predictedRotation, targetRotation, MaxChestRotationDeltaDegrees / angle);
        }

        static void ConstrainSegment(ref SpineData spineData, int index, int targetIndex, float weight)
        {
            Vector3 targetOffset = spineData.Positions[index] - spineData.Positions[targetIndex];
            Vector3 restOffset = spineData.RestPositions[index] - spineData.RestPositions[targetIndex];
            float boneLength = restOffset.magnitude;
            if (boneLength <= BasisEerieMovement.epsilon)
            {
                return;
            }
            if (targetOffset.sqrMagnitude <= BasisEerieMovement.sqrEpsilon)
            {
                targetOffset = restOffset;
            }
            Vector3 targetPosition = spineData.Positions[targetIndex] + targetOffset.normalized * boneLength;
            spineData.Positions[index] = Vector3.Lerp(spineData.Positions[index], targetPosition, weight);
        }

        static Quaternion SafeFromToRotation(Vector3 from, Vector3 to)
        {
            return from.sqrMagnitude > BasisEerieMovement.sqrEpsilon && to.sqrMagnitude > BasisEerieMovement.sqrEpsilon
                ? BasisQuaternionExt.FromToRotation(from, to)
                : Quaternion.identity;
        }
    }
}
