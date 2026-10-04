using Basis.IK;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Basis.Tests.IK
{
    public class RalivIKSpineChestTests
    {
        static RalivIKSpine.SpineData CreateStraightSpine()
        {
            const int count = 5;
            RalivIKSpine.SpineData data = default;
            data.Positions.Length = count;
            data.Rotations.Length = count;
            data.RestPositions.Length = count;
            data.RestRotations.Length = count;
            data.T.Length = count;
            for (int index = 0; index < count; index++)
            {
                Vector3 position = new Vector3(0f, index * 0.2f, 0f);
                data.Positions[index] = position;
                data.RestPositions[index] = position;
                data.Rotations[index] = Quaternion.identity;
                data.RestRotations[index] = Quaternion.identity;
                data.T[index] = index / (float)(count - 1) * 0.8f;
            }
            data.Length = 0.6f;
            data.HipTargetPosition = data.Positions[0];
            data.HipTargetRotation = Quaternion.identity;
            data.HeadTargetPosition = data.Positions[count - 1];
            data.HeadTargetRotation = Quaternion.identity;
            return data;
        }

        [Test]
        public void UnreachableHipTargetCannotStretchSpineSegments()
        {
            RalivIKSpine.SpineData data = CreateStraightSpine();
            data.HipTargetPosition = new Vector3(0f, -5f, 0f);

            RalivIKSpine.Solve(ref data);

            Assert.That(Vector3.Distance(data.Positions[data.Positions.Length - 1], data.HeadTargetPosition), Is.LessThan(0.00001f));
            for (int index = 1; index < data.Positions.Length; index++)
            {
                float restLength = Vector3.Distance(data.RestPositions[index - 1], data.RestPositions[index]);
                float solvedLength = Vector3.Distance(data.Positions[index - 1], data.Positions[index]);
                Assert.AreEqual(restLength, solvedLength, 0.00001f, $"segment {index - 1}->{index} stretched");
            }
        }

        [Test]
        public void ApplyingExtremeHipTargetPreservesAuthoredSpineTranslations()
        {
            GameObject root = new GameObject("RalivApplyTest");
            Transform[] bones = new Transform[5];
            Transform parent = root.transform;
            for (int index = 0; index < bones.Length; index++)
            {
                GameObject bone = new GameObject($"Bone{index}");
                bone.transform.SetParent(parent, false);
                bone.transform.localPosition = index == 0 ? Vector3.zero : new Vector3(0f, 0.2f, 0f);
                bones[index] = bone.transform;
                parent = bone.transform;
            }

            BasisPoseSkeleton skeleton = new BasisPoseSkeleton();
            NativeArray<BasisBoneHandle> chain = default;
            try
            {
                skeleton.Build(bones[0], bones);
                skeleton.GatherNow();
                chain = new NativeArray<BasisBoneHandle>(bones.Length, Allocator.TempJob);
                for (int index = 0; index < bones.Length; index++)
                {
                    chain[index] = skeleton.Bind(bones[bones.Length - 1 - index]);
                }

                BasisEerieMovement job = new BasisEerieMovement
                {
                    chainHeadToSpine = chain,
                    chainChestIdx = 2,
                    handleHips = skeleton.Bind(bones[0]),
                    handleSpine = skeleton.Bind(bones[1]),
                    handleChest = skeleton.Bind(bones[2]),
                    handleNeck = skeleton.Bind(bones[3]),
                    handleHead = skeleton.Bind(bones[4]),
                    offsetRotationHips = Quaternion.identity,
                    offsetRotationHead = Quaternion.identity,
                    offsetRotationChest = Quaternion.identity,
                    targetRotationHips = Quaternion.identity,
                    targetRotationHead = Quaternion.identity,
                    playerUp = Vector3.up,
                    tposeBakeScale = 1f,
                    poseStream = skeleton.Stream,
                };
                job.InitalizeRalivSpineIK();
                BasisEeriePlanner.Bind(ref job);
                BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true });
                job.targetPositionHips = new Vector3(0f, -5f, 0f);
                job.targetPositionHead = bones[4].position;

                Vector3[] authoredLocalPositions = new Vector3[bones.Length];
                for (int index = 1; index < bones.Length; index++)
                {
                    authoredLocalPositions[index] = skeleton.Stream.LocalPosition[skeleton.Bind(bones[index]).Index];
                }

                job.SolveSpine();

                for (int index = 1; index < bones.Length; index++)
                {
                    Vector3 solvedLocalPosition = skeleton.Stream.LocalPosition[skeleton.Bind(bones[index]).Index];
                    Assert.AreEqual(authoredLocalPositions[index], solvedLocalPosition, $"bone {index} local translation changed");
                }
                Assert.That(Vector3.Distance(skeleton.Stream.GetPosition(job.handleHead), job.targetPositionHead), Is.LessThan(0.0001f));
            }
            finally
            {
                if (chain.IsCreated) chain.Dispose();
                skeleton.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ChestRotationCannotExceedNinetyDegreesFromPrediction()
        {
            Quaternion predicted = Quaternion.Euler(0f, 10f, 0f);
            Quaternion target = Quaternion.Euler(0f, 170f, 0f);
            Quaternion constrained = RalivIKSpine.ConstrainChestRotation(predicted, target);
            Assert.AreEqual(90f, Quaternion.Angle(predicted, constrained), 0.001f);
            Assert.Less(Quaternion.Angle(constrained, target), Quaternion.Angle(predicted, target));
        }
    }
}
