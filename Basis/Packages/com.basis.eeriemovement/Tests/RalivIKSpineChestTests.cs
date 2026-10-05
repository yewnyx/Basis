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
            data.positions.Length = count;
            data.rotations.Length = count;
            data.restPositions.Length = count;
            data.restRotations.Length = count;
            data.t.Length = count;
            for (int index = 0; index < count; index++)
            {
                Vector3 position = new Vector3(0f, index * 0.2f, 0f);
                data.positions[index] = position;
                data.restPositions[index] = position;
                data.rotations[index] = Quaternion.identity;
                data.restRotations[index] = Quaternion.identity;
                data.t[index] = index / (float)(count - 1) * 0.8f;
            }
            data.length = 0.6f;
            data.hipTargetPosition = data.positions[0];
            data.hipTargetRotation = Quaternion.identity;
            data.headTargetPosition = data.positions[count - 1];
            data.headTargetRotation = Quaternion.identity;
            return data;
        }

        [Test]
        public void UnreachableHipTargetCannotStretchSpineSegments()
        {
            RalivIKSpine.SpineData data = CreateStraightSpine();
            data.hipTargetPosition = new Vector3(0f, -5f, 0f);

            RalivIKSpine.SolveSpine(ref data);

            Assert.That(Vector3.Distance(data.positions[data.positions.Length - 1], data.headTargetPosition), Is.LessThan(0.00001f));
            for (int index = 1; index < data.positions.Length; index++)
            {
                float restLength = Vector3.Distance(data.restPositions[index - 1], data.restPositions[index]);
                float solvedLength = Vector3.Distance(data.positions[index - 1], data.positions[index]);
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
            RalivIKSpine.SpineData data = CreateStraightSpine();
            data.chestIndex = 2;
            data.chestForward = Vector3.forward;
            data.chestTargetRotation = Quaternion.Euler(0f, 170f, 0f);
            data.chestHintWeight = 1f;

            RalivIKSpine.SolveSpine(ref data);

            Assert.AreEqual(45f, Quaternion.Angle(Quaternion.identity, data.rotations[1]), 0.001f);
            Assert.AreEqual(90f, Quaternion.Angle(Quaternion.identity, data.rotations[2]), 0.001f);
            Assert.AreEqual(45f, Quaternion.Angle(Quaternion.identity, data.rotations[3]), 0.001f);
        }

        [Test]
        public void ChestRotationDoesNotAlterSolvedPositions()
        {
            RalivIKSpine.SpineData baseline = CreateStraightSpine();
            RalivIKSpine.SpineData tracked = baseline;
            tracked.chestIndex = 2;
            tracked.chestForward = Vector3.forward;
            tracked.chestTargetRotation = Quaternion.Euler(0f, 75f, 0f);
            tracked.chestHintWeight = 1f;

            RalivIKSpine.SolveSpine(ref baseline);
            RalivIKSpine.SolveSpine(ref tracked);

            for (int index = 0; index < baseline.positions.Length; index++)
            {
                Assert.AreEqual(baseline.positions[index], tracked.positions[index], $"chest rotation moved spine point {index}");
            }
        }

        [Test]
        public void ChestPositionPullsSolvedChestTowardTrackerAndKeepsHeadPinned()
        {
            RalivIKSpine.SpineData baseline = CreateStraightSpine();
            RalivIKSpine.SpineData tracked = baseline;
            tracked.chestIndex = 2;
            tracked.chestTargetPosition = tracked.positions[2] + new Vector3(0.2f, 0f, 0f);
            tracked.chestHintWeight = 0.5f;

            RalivIKSpine.SolveSpine(ref baseline);
            RalivIKSpine.SolveSpine(ref tracked);

            Assert.Greater(tracked.positions[2].x, baseline.positions[2].x, "tracked chest did not move toward its positional target");
            Assert.That(Vector3.Distance(tracked.positions[tracked.positions.Length - 1], tracked.headTargetPosition), Is.LessThan(0.00001f));
            for (int index = 1; index < tracked.positions.Length; index++)
            {
                float restLength = Vector3.Distance(tracked.restPositions[index - 1], tracked.restPositions[index]);
                float solvedLength = Vector3.Distance(tracked.positions[index - 1], tracked.positions[index]);
                Assert.AreEqual(restLength, solvedLength, 0.00001f, $"segment {index - 1}->{index} stretched");
            }
        }

        [Test]
        public void ChestPositionIsIgnoredWithoutTrackedChestWeight()
        {
            RalivIKSpine.SpineData baseline = CreateStraightSpine();
            RalivIKSpine.SpineData untracked = baseline;
            untracked.chestIndex = 2;
            untracked.chestTargetPosition = new Vector3(10f, 10f, 10f);
            untracked.chestHintWeight = 0f;

            RalivIKSpine.SolveSpine(ref baseline);
            RalivIKSpine.SolveSpine(ref untracked);

            for (int index = 0; index < baseline.positions.Length; index++)
            {
                Assert.AreEqual(baseline.positions[index], untracked.positions[index], $"untracked chest moved spine point {index}");
            }
        }

        [Test]
        public void ChestRotationIsIgnoredWithoutTrackedChestWeight()
        {
            RalivIKSpine.SpineData data = CreateStraightSpine();
            data.chestIndex = 2;
            data.chestForward = Vector3.forward;
            data.chestTargetRotation = Quaternion.Euler(0f, 90f, 0f);
            data.chestHintWeight = 0f;

            RalivIKSpine.SolveSpine(ref data);

            Assert.AreEqual(Quaternion.identity, data.rotations[1]);
            Assert.AreEqual(Quaternion.identity, data.rotations[2]);
            Assert.AreEqual(Quaternion.identity, data.rotations[3]);
        }
    }
}
