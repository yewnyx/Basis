using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using static BasisMuscleDriver;
[DefaultExecutionOrder(15001)]
[BurstCompile]
public abstract class BasisBaseMuscleDriver : MonoBehaviour
{
    public Animator Animator; // Reference to the Animator component
    public HumanPoseHandler poseHandler;
    public BasisLocalAvatarDriver BasisLocalAvatarDriver;
    public HumanPose pose;
    public float[] LeftThumb;
    public float[] LeftIndex;
    public float[] LeftMiddle;
    public float[] LeftRing;
    public float[] LeftLittle;

    public float[] RightThumb;
    public float[] RightIndex;
    public float[] RightMiddle;
    public float[] RightRing;
    public float[] RightLittle;
    [SerializeField]
    public FingerPose LeftFinger;
    [SerializeField]
    public FingerPose RightFinger;
    /// <summary>
    /// 0.7 = straight fingers
    /// -1 is fully closed
    /// </summary>
    [System.Serializable]
    public struct FingerPose
    {
        public Vector2 ThumbPercentage;
        public Vector2 IndexPercentage;
        public Vector2 MiddlePercentage;
        public Vector2 RingPercentage;
        public Vector2 LittlePercentage;
    }

    public Vector2 LastLeftThumbPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftIndexPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftMiddlePercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftRingPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastLeftLittlePercentage = new Vector2(-1.1f, -1.1f);

    public Vector2 LastRightThumbPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightIndexPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightMiddlePercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightRingPercentage = new Vector2(-1.1f, -1.1f);
    public Vector2 LastRightLittlePercentage = new Vector2(-1.1f, -1.1f);
    public Dictionary<Vector2, PoseDataAdditional> CoordToPose = new Dictionary<Vector2, PoseDataAdditional>();
    public Vector2[] coordKeys; // Cached array of keys for optimization

    public PoseDataAdditional LeftThumbAdditional;
    public PoseDataAdditional LeftIndexAdditional;
    public PoseDataAdditional LeftMiddleAdditional;
    public PoseDataAdditional LeftRingAdditional;
    public PoseDataAdditional LeftLittleAdditional;

    public PoseDataAdditional RightThumbAdditional;
    public PoseDataAdditional RightIndexAdditional;
    public PoseDataAdditional RightMiddleAdditional;
    public PoseDataAdditional RightRingAdditional;
    public PoseDataAdditional RightLittleAdditional;
    public NativeArray<Vector2> coordKeysArray;
    public NativeArray<float> distancesArray;
    public NativeArray<int> closestIndexArray;
    public float LerpSpeed = 17f;
    // Dictionary to store the mapping
    public Dictionary<Vector2, PoseData> pointMap = new Dictionary<Vector2, PoseData>();
    public static float MapValue(float value, float minSource, float maxSource, float minTarget, float maxTarget)
    {
        return minTarget + (maxTarget - minTarget) * ((value - minSource) / (maxSource - minSource));
    }
    [BurstCompile]
    public struct RecordFingerJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<bool> HasProximal;
        [WriteOnly] public NativeArray<MuscleLocalPose> FingerPoses;

        public void Execute(int index, TransformAccess transform)
        {
            if (HasProximal[index])
            {
                FingerPoses[index] = new MuscleLocalPose
                {
                    position = transform.localPosition,
                    rotation = transform.localRotation
                };
            }
        }
    }

    public void RecordCurrentPose(ref PoseData poseData)
    {
        Basis.Scripts.Common.BasisTransformMapping Mapping = BasisLocalAvatarDriver.References;

        // Process fingers in parallel
        poseData.LeftThumb = RecordFingerPoses(Mapping.LeftThumb, Mapping.HasLeftThumb);
        poseData.LeftIndex = RecordFingerPoses(Mapping.LeftIndex, Mapping.HasLeftIndex);
        poseData.LeftMiddle = RecordFingerPoses(Mapping.LeftMiddle, Mapping.HasLeftMiddle);
        poseData.LeftRing = RecordFingerPoses(Mapping.LeftRing, Mapping.HasLeftRing);
        poseData.LeftLittle = RecordFingerPoses(Mapping.LeftLittle, Mapping.HasLeftLittle);
        poseData.RightThumb = RecordFingerPoses(Mapping.RightThumb, Mapping.HasRightThumb);
        poseData.RightIndex = RecordFingerPoses(Mapping.RightIndex, Mapping.HasRightThumb);
        poseData.RightMiddle = RecordFingerPoses(Mapping.RightMiddle, Mapping.HasRightMiddle);
        poseData.RightRing = RecordFingerPoses(Mapping.RightRing, Mapping.HasRightRing);
        poseData.RightLittle = RecordFingerPoses(Mapping.RightLittle, Mapping.HasRightLittle);
    }
    public struct PoseData
    {
        public NativeArray<MuscleLocalPose> LeftThumb;
        public NativeArray<MuscleLocalPose> LeftIndex;
        public NativeArray<MuscleLocalPose> LeftMiddle;
        public NativeArray<MuscleLocalPose> LeftRing;
        public NativeArray<MuscleLocalPose> LeftLittle;
        public NativeArray<MuscleLocalPose> RightThumb;
        public NativeArray<MuscleLocalPose> RightIndex;
        public NativeArray<MuscleLocalPose> RightMiddle;
        public NativeArray<MuscleLocalPose> RightRing;
        public NativeArray<MuscleLocalPose> RightLittle;
    }
    private NativeArray<MuscleLocalPose> RecordFingerPoses(Transform[] proximal, bool[] hasProximal)
    {
        int length = proximal.Length;

        // Prepare NativeArrays and TransformAccessArray
        NativeArray<bool> hasProximalArray = new NativeArray<bool>(length, Allocator.Persistent);
        NativeArray<MuscleLocalPose> fingerPoses = new NativeArray<MuscleLocalPose>(length, Allocator.Persistent);
        TransformAccessArray transformAccessArray = new TransformAccessArray(length);

        // Fill NativeArrays and TransformAccessArray
        for (int i = 0; i < length; i++)
        {
            hasProximalArray[i] = hasProximal[i];
            if (proximal[i] != null)
            {
                transformAccessArray.Add(proximal[i]);
            }
        }

        // Create and schedule the job
        var job = new RecordFingerJob
        {
            HasProximal = hasProximalArray,
            FingerPoses = fingerPoses
        };
        JobHandle handle = job.Schedule(transformAccessArray);
        handle.Complete();
        return fingerPoses;
    }
    public void DisposeAllJobsData()
    {
        // Dispose NativeArrays if allocated
        if (coordKeysArray.IsCreated)
        {
            coordKeysArray.Dispose();
        }
        if (distancesArray.IsCreated)
        {
            distancesArray.Dispose();
        }
        if (closestIndexArray.IsCreated)
        {
            closestIndexArray.Dispose();
        }
    }
    public void SetAndRecordPose(float fillValue, ref PoseData poseData, float Splane)
    {
        // Apply muscle data to both hands
        SetMuscleData(LeftThumb, fillValue, Splane);
        SetMuscleData(LeftIndex, fillValue, Splane);
        SetMuscleData(LeftMiddle, fillValue, Splane);
        SetMuscleData(LeftRing, fillValue, Splane);
        SetMuscleData(LeftLittle, fillValue, Splane);

        SetMuscleData(RightThumb, fillValue, Splane);
        SetMuscleData(RightIndex, fillValue, Splane);
        SetMuscleData(RightMiddle, fillValue, Splane);
        SetMuscleData(RightRing, fillValue, Splane);
        SetMuscleData(RightLittle, fillValue, Splane);

        ApplyMuscleData();
        poseHandler.SetHumanPose(ref pose);
        RecordCurrentPose(ref poseData);
    }
    public void ApplyMuscleData()
    {
        // Update the finger muscle values in the poses array using Array.Copy
        System.Array.Copy(LeftThumb, 0, pose.muscles, 55, 4);
        System.Array.Copy(LeftIndex, 0, pose.muscles, 59, 4);
        System.Array.Copy(LeftMiddle, 0, pose.muscles, 63, 4);
        System.Array.Copy(LeftRing, 0, pose.muscles, 67, 4);
        System.Array.Copy(LeftLittle, 0, pose.muscles, 71, 4);

        System.Array.Copy(RightThumb, 0, pose.muscles, 75, 4);
        System.Array.Copy(RightIndex, 0, pose.muscles, 79, 4);
        System.Array.Copy(RightMiddle, 0, pose.muscles, 83, 4);
        System.Array.Copy(RightRing, 0, pose.muscles, 87, 4);
        System.Array.Copy(RightLittle, 0, pose.muscles, 91, 4);
    }
    public void SetMuscleData(float[] muscleArray, float fillValue, float specificValue)
    {
        Array.Fill(muscleArray, fillValue);
        muscleArray[1] = specificValue;
    }
    public void UpdateAllFingers(Basis.Scripts.Common.BasisTransformMapping Map, ref PoseData Current)
    {
        float Rotation = LerpSpeed * Time.deltaTime;

        // Update Thumb
        if (LeftFinger.ThumbPercentage != LastLeftThumbPercentage)
        {
            GetClosestValue(LeftFinger.ThumbPercentage, out LeftThumbAdditional);
            LastLeftThumbPercentage = LeftFinger.ThumbPercentage;
        }
        UpdateFingerPoses(Map.LeftThumb, LeftThumbAdditional.PoseData.LeftThumb, ref Current.LeftThumb, Map.HasLeftThumb, Rotation);

        // Update Index
        if (LeftFinger.IndexPercentage != LastLeftIndexPercentage)
        {
            GetClosestValue(LeftFinger.IndexPercentage, out LeftIndexAdditional);
            LastLeftIndexPercentage = LeftFinger.IndexPercentage;
        }
        UpdateFingerPoses(Map.LeftIndex, LeftIndexAdditional.PoseData.LeftIndex, ref Current.LeftIndex, Map.HasLeftIndex, Rotation);

        // Update Middle
        if (LeftFinger.MiddlePercentage != LastLeftMiddlePercentage)
        {
            GetClosestValue(LeftFinger.MiddlePercentage, out LeftMiddleAdditional);
            LastLeftMiddlePercentage = LeftFinger.MiddlePercentage;
        }
        UpdateFingerPoses(Map.LeftMiddle, LeftMiddleAdditional.PoseData.LeftMiddle, ref Current.LeftMiddle, Map.HasLeftMiddle, Rotation);

        // Update Ring
        if (LeftFinger.RingPercentage != LastLeftRingPercentage)
        {
            GetClosestValue(LeftFinger.RingPercentage, out LeftRingAdditional);
            LastLeftRingPercentage = LeftFinger.RingPercentage;
        }
        UpdateFingerPoses(Map.LeftRing, LeftRingAdditional.PoseData.LeftRing, ref Current.LeftRing, Map.HasLeftRing, Rotation);

        // Update Little
        if (LeftFinger.LittlePercentage != LastLeftLittlePercentage)
        {
            GetClosestValue(LeftFinger.LittlePercentage, out LeftLittleAdditional);
            LastLeftLittlePercentage = LeftFinger.LittlePercentage;
        }
        UpdateFingerPoses(Map.LeftLittle, LeftLittleAdditional.PoseData.LeftLittle, ref Current.LeftLittle, Map.HasLeftLittle, Rotation);

        // Update Right Thumb
        if (RightFinger.ThumbPercentage != LastRightThumbPercentage)
        {
            GetClosestValue(RightFinger.ThumbPercentage, out RightThumbAdditional);
            LastRightThumbPercentage = RightFinger.ThumbPercentage;
        }
        UpdateFingerPoses(Map.RightThumb, RightThumbAdditional.PoseData.RightThumb, ref Current.RightThumb, Map.HasRightThumb, Rotation);

        // Update Right Index
        if (RightFinger.IndexPercentage != LastRightIndexPercentage)
        {
            GetClosestValue(RightFinger.IndexPercentage, out RightIndexAdditional);
            LastRightIndexPercentage = RightFinger.IndexPercentage;
        }
        UpdateFingerPoses(Map.RightIndex, RightIndexAdditional.PoseData.RightIndex, ref Current.RightIndex, Map.HasRightIndex, Rotation);

        // Update Right Middle
        if (RightFinger.MiddlePercentage != LastRightMiddlePercentage)
        {
            GetClosestValue(RightFinger.MiddlePercentage, out RightMiddleAdditional);
            LastRightMiddlePercentage = RightFinger.MiddlePercentage;
        }
        UpdateFingerPoses(Map.RightMiddle, RightMiddleAdditional.PoseData.RightMiddle, ref Current.RightMiddle, Map.HasRightMiddle, Rotation);

        // Update Right Ring
        if (RightFinger.RingPercentage != LastRightRingPercentage)
        {
            GetClosestValue(RightFinger.RingPercentage, out RightRingAdditional);
            LastRightRingPercentage = RightFinger.RingPercentage;
        }
        UpdateFingerPoses(Map.RightRing, RightRingAdditional.PoseData.RightRing, ref Current.RightRing, Map.HasRightRing, Rotation);

        // Update Right Little
        if (RightFinger.LittlePercentage != LastRightLittlePercentage)
        {
            GetClosestValue(RightFinger.LittlePercentage, out RightLittleAdditional);
            LastRightLittlePercentage = RightFinger.LittlePercentage;
        }
        UpdateFingerPoses(Map.RightLittle, RightLittleAdditional.PoseData.RightLittle, ref Current.RightLittle, Map.HasRightRing, Rotation);
    }
    public void UpdateFingerPoses(Transform[] proximal, NativeArray<MuscleLocalPose> poses, ref NativeArray<MuscleLocalPose> currentPoses, bool[] hasProximal, float rotation)
    {
        // Update proximal pose if available
        if (hasProximal[0])
        {
            Vector3 newProximalPosition = Vector3.Lerp(currentPoses[0].position, poses[0].position, rotation);
            Quaternion newProximalRotation = Quaternion.Slerp(currentPoses[0].rotation, poses[0].rotation, rotation);

            currentPoses[0].position = newProximalPosition;
            currentPoses[0].rotation = newProximalRotation;

            proximal[0].SetLocalPositionAndRotation(newProximalPosition, newProximalRotation);
        }

        // Update intermediate pose if available
        if (hasProximal[1])
        {
            Vector3 newIntermediatePosition = Vector3.Lerp(currentPoses[1].position, poses[1].position, rotation);
            Quaternion newIntermediateRotation = Quaternion.Slerp(currentPoses[1].rotation, poses[1].rotation, rotation);

            currentPoses[1].position = newIntermediatePosition;
            currentPoses[1].rotation = newIntermediateRotation;

            proximal[1].SetLocalPositionAndRotation(newIntermediatePosition, newIntermediateRotation);
        }

        // Update distal pose if available
        if (hasProximal[2])
        {
            Vector3 newDistalPosition = Vector3.Lerp(currentPoses[2].position, poses[2].position, rotation);
            Quaternion newDistalRotation = Quaternion.Slerp(currentPoses[2].rotation, poses[2].rotation, rotation);

            currentPoses[2].position = newDistalPosition;
            currentPoses[2].rotation = newDistalRotation;

            proximal[2].SetLocalPositionAndRotation(newDistalPosition, newDistalRotation);
        }
    }
    public bool GetClosestValue(Vector2 percentage, out PoseDataAdditional first)
    {
        // Create and schedule the distance computation job
        var distanceJob = new FindClosestPointJob
        {
            target = percentage,
            coordKeys = coordKeysArray,
            distances = distancesArray
        };

        JobHandle distanceJobHandle = distanceJob.Schedule(coordKeysArray.Length, 64);
        distanceJobHandle.Complete();

        // Create and schedule the parallel reduction job
        var reductionJob = new FindMinDistanceJob
        {
            distances = distancesArray,
            closestIndex = closestIndexArray
        };

        JobHandle reductionJobHandle = reductionJob.Schedule();
        reductionJobHandle.Complete();

        // Find the closest point
        int closestIndex = closestIndexArray[0];
        Vector2 closestPoint = coordKeysArray[closestIndex];

        // Return result
        return CoordToPose.TryGetValue(closestPoint, out first);
    }

    [BurstCompile]
    private struct FindClosestPointJob : IJobParallelFor
    {
        public Vector2 target;
        public NativeArray<Vector2> coordKeys;
        public NativeArray<float> distances;

        public void Execute(int index)
        {
            distances[index] = Vector2.Distance(coordKeys[index], target);
        }
    }

    [BurstCompile]
    private struct FindMinDistanceJob : IJob
    {
        [ReadOnly] public NativeArray<float> distances;
        public NativeArray<int> closestIndex;

        public void Execute()
        {
            float minDistance = float.MaxValue;
            int minIndex = -1;

            for (int i = 0; i < distances.Length; i++)
            {
                if (distances[i] < minDistance)
                {
                    minDistance = distances[i];
                    minIndex = i;
                }
            }

            closestIndex[0] = minIndex;
        }
    }
}