using Basis.IK;
using NUnit.Framework;
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
