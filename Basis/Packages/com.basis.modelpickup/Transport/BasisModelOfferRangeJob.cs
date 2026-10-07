using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Distances for every outstanding server-cache offer at once, as a Burst job over persistent buffers. Top
    /// level and non-generic so Burst and IL2CPP compile it without a <c>RegisterGenericJobType</c>.
    /// </summary>
    [BurstCompile]
    public struct BasisModelOfferRangeJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<Vector3> Positions;

        public Vector3 Viewer;

        /// <summary>Zero or less means unlimited.</summary>
        public float RangeSquared;

        [WriteOnly]
        public NativeArray<byte> InRange;

        /// <summary>Each offer's distance squared from <see cref="Viewer"/>, so the caller never measures twice; 0 when unlimited.</summary>
        [WriteOnly]
        public NativeArray<float> DistanceSquared;

        public void Execute(int index)
        {
            if (RangeSquared <= 0f)
            {
                InRange[index] = 1;
                DistanceSquared[index] = 0f;
                return;
            }
            Vector3 delta = Positions[index] - Viewer;
            float distanceSquared = (delta.x * delta.x) + (delta.y * delta.y) + (delta.z * delta.z);
            InRange[index] = distanceSquared <= RangeSquared ? (byte)1 : (byte)0;
            DistanceSquared[index] = distanceSquared;
        }
    }
}
