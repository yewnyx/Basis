namespace Basis.ModelPickup
{
    /// <summary>
    /// Engine-free stand-in for a Unity Vector3, so wire codecs and range maths can run outside the engine.
    /// Blittable and field-for-field compatible; <c>BasisModelShareConvert</c> converts at the Unity edge.
    /// </summary>
    public struct BasisModelVec3
    {
        public float X;
        public float Y;
        public float Z;

        public BasisModelVec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// Squared distance, summed in the same order as <c>(a - b).sqrMagnitude</c> so range decisions made
        /// here match the ones engine-side code would make.
        /// </summary>
        public static float DistanceSquared(in BasisModelVec3 a, in BasisModelVec3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    /// <summary>Engine-free stand-in for a Unity Quaternion (x, y, z, w order, as on the wire).</summary>
    public struct BasisModelQuat
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public static readonly BasisModelQuat Identity = new BasisModelQuat(0f, 0f, 0f, 1f);

        public BasisModelQuat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float LengthSquared => X * X + Y * Y + Z * Z + W * W;
    }

    /// <summary>A world position and rotation, laid out exactly as the 28 pose bytes of every model message.</summary>
    public struct BasisModelPose
    {
        public BasisModelVec3 Position;
        public BasisModelQuat Rotation;

        public BasisModelPose(BasisModelVec3 position, BasisModelQuat rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }
}
