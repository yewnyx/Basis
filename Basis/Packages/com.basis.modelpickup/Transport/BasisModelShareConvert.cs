using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Conversions between Unity's vector types and the engine-free core structs. Field for field, so a value
    /// that crosses the edge and comes back is bit-identical.
    /// </summary>
    public static class BasisModelShareConvert
    {
        public static BasisModelVec3 ToShare(this Vector3 value)
        {
            return new BasisModelVec3(value.x, value.y, value.z);
        }

        public static Vector3 ToUnity(this BasisModelVec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        public static BasisModelQuat ToShare(this Quaternion value)
        {
            return new BasisModelQuat(value.x, value.y, value.z, value.w);
        }

        public static Quaternion ToUnity(this BasisModelQuat value)
        {
            return new Quaternion(value.X, value.Y, value.Z, value.W);
        }

        public static BasisModelPose ToSharePose(Vector3 position, Quaternion rotation)
        {
            return new BasisModelPose(position.ToShare(), rotation.ToShare());
        }

        public static void ToUnity(this BasisModelPose pose, out Vector3 position, out Quaternion rotation)
        {
            position = pose.Position.ToUnity();
            rotation = pose.Rotation.ToUnity();
        }
    }
}
