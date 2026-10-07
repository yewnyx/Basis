using System;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Double-precision 4×4 helpers in glTF's column-major layout (m[column * 4 + row]). World matrices are always
    /// built from the canonical float TRS, never from a source matrix, so sender and receiver get bit-identical bounds.
    /// </summary>
    public static class BasisGltfTransformMath
    {
        public const int Stride = 16;

        public static void SetIdentity(double[] m, int o)
        {
            Array.Clear(m, o, Stride);
            m[o] = 1d;
            m[o + 5] = 1d;
            m[o + 10] = 1d;
            m[o + 15] = 1d;
        }

        /// <summary>T × R × S; the quaternion is taken as given (the canonical rule keeps it unit length within 1e-6).</summary>
        public static void SetTrs(double[] m, int o, float tx, float ty, float tz, float rx, float ry, float rz, float rw,
            float sx, float sy, float sz)
        {
            double x = rx, y = ry, z = rz, w = rw;
            double xx = x * x, yy = y * y, zz = z * z;
            double xy = x * y, xz = x * z, yz = y * z, xw = x * w, yw = y * w, zw = z * w;
            m[o + 0] = (1d - 2d * (yy + zz)) * sx;
            m[o + 1] = 2d * (xy + zw) * sx;
            m[o + 2] = 2d * (xz - yw) * sx;
            m[o + 3] = 0d;
            m[o + 4] = 2d * (xy - zw) * sy;
            m[o + 5] = (1d - 2d * (xx + zz)) * sy;
            m[o + 6] = 2d * (yz + xw) * sy;
            m[o + 7] = 0d;
            m[o + 8] = 2d * (xz + yw) * sz;
            m[o + 9] = 2d * (yz - xw) * sz;
            m[o + 10] = (1d - 2d * (xx + yy)) * sz;
            m[o + 11] = 0d;
            m[o + 12] = tx;
            m[o + 13] = ty;
            m[o + 14] = tz;
            m[o + 15] = 1d;
        }

        /// <summary>dst = a × b. dst must not overlap a or b.</summary>
        public static void Multiply(double[] a, int ao, double[] b, int bo, double[] dst, int dsto)
        {
            for (int column = 0; column < 4; column++)
            {
                double b0 = b[bo + column * 4], b1 = b[bo + column * 4 + 1], b2 = b[bo + column * 4 + 2], b3 = b[bo + column * 4 + 3];
                for (int row = 0; row < 4; row++)
                {
                    dst[dsto + column * 4 + row] = a[ao + row] * b0 + a[ao + 4 + row] * b1 + a[ao + 8 + row] * b2 + a[ao + 12 + row] * b3;
                }
            }
        }

        /// <summary>Every entry within ±max. NaN fails.</summary>
        public static bool AllWithin(double[] m, int o, double maximum)
        {
            for (int i = 0; i < Stride; i++)
            {
                if (!BasisGlbNumbers.Within(Math.Abs(m[o + i]), maximum)) return false;
            }
            return true;
        }

        public static void TransformPoint(double[] m, int o, double x, double y, double z, out double px, out double py, out double pz)
        {
            px = m[o] * x + m[o + 4] * y + m[o + 8] * z + m[o + 12];
            py = m[o + 1] * x + m[o + 5] * y + m[o + 9] * z + m[o + 13];
            pz = m[o + 2] * x + m[o + 6] * y + m[o + 10] * z + m[o + 14];
        }

        /// <summary>
        /// The canonical rotation rule: reject |q|² &lt; 1e-12; renormalise in double only when ||q|² − 1| &gt; 1e-6, otherwise
        /// keep the floats bit-for-bit. Applying it to its own output changes nothing, which the idempotence proof needs.
        /// </summary>
        public static bool TryCanonicalizeRotation(ref float x, ref float y, ref float z, ref float w)
        {
            double n2 = (double)x * x + (double)y * y + (double)z * z + (double)w * w;
            if (!(n2 >= 1e-12) || double.IsInfinity(n2)) return false;
            if (Math.Abs(n2 - 1d) > 1e-6)
            {
                double inv = 1d / Math.Sqrt(n2);
                x = BasisGlbNumbers.PositiveZero((float)(x * inv));
                y = BasisGlbNumbers.PositiveZero((float)(y * inv));
                z = BasisGlbNumbers.PositiveZero((float)(z * inv));
                w = BasisGlbNumbers.PositiveZero((float)(w * inv));
            }
            return true;
        }

        /// <summary>
        /// Decomposes a glTF node matrix the way glTFast does (Mathematics.Decompose): column lengths give the scale,
        /// a negative determinant negates rotation and scale, and the rotation becomes a quaternion. Rejects a
        /// projective bottom row or a degenerate column.
        /// </summary>
        public static bool TryDecompose(float[] matrix, out float tx, out float ty, out float tz,
            out float rx, out float ry, out float rz, out float rw, out float sx, out float sy, out float sz)
        {
            tx = ty = tz = rx = ry = rz = 0f;
            rw = sx = sy = sz = 1f;
            if (Math.Abs(matrix[3]) > 1e-6 || Math.Abs(matrix[7]) > 1e-6 || Math.Abs(matrix[11]) > 1e-6
                || Math.Abs(matrix[15] - 1d) > 1e-6)
            {
                return false;
            }
            double c0x = matrix[0], c0y = matrix[1], c0z = matrix[2];
            double c1x = matrix[4], c1y = matrix[5], c1z = matrix[6];
            double c2x = matrix[8], c2y = matrix[9], c2z = matrix[10];
            double l0 = Math.Sqrt(c0x * c0x + c0y * c0y + c0z * c0z);
            double l1 = Math.Sqrt(c1x * c1x + c1y * c1y + c1z * c1z);
            double l2 = Math.Sqrt(c2x * c2x + c2y * c2y + c2z * c2z);
            if (!(l0 > 1e-12) || !(l1 > 1e-12) || !(l2 > 1e-12)) return false;
            c0x /= l0; c0y /= l0; c0z /= l0;
            c1x /= l1; c1y /= l1; c1z /= l1;
            c2x /= l2; c2y /= l2; c2z /= l2;
            double crossX = c0y * c1z - c0z * c1y;
            double crossY = c0z * c1x - c0x * c1z;
            double crossZ = c0x * c1y - c0y * c1x;
            if (crossX * c2x + crossY * c2y + crossZ * c2z < 0d)
            {
                c0x = -c0x; c0y = -c0y; c0z = -c0z;
                c1x = -c1x; c1y = -c1y; c1z = -c1z;
                c2x = -c2x; c2y = -c2y; c2z = -c2z;
                l0 = -l0; l1 = -l1; l2 = -l2;
            }
            // r[row, column]; column j is c_j.
            double r00 = c0x, r10 = c0y, r20 = c0z;
            double r01 = c1x, r11 = c1y, r21 = c1z;
            double r02 = c2x, r12 = c2y, r22 = c2z;
            double qx, qy, qz, qw;
            double trace = r00 + r11 + r22;
            if (trace > 0d)
            {
                double s = Math.Sqrt(trace + 1d) * 2d;
                qw = 0.25d * s;
                qx = (r21 - r12) / s;
                qy = (r02 - r20) / s;
                qz = (r10 - r01) / s;
            }
            else if (r00 > r11 && r00 > r22)
            {
                double s = Math.Sqrt(1d + r00 - r11 - r22) * 2d;
                qw = (r21 - r12) / s;
                qx = 0.25d * s;
                qy = (r01 + r10) / s;
                qz = (r02 + r20) / s;
            }
            else if (r11 > r22)
            {
                double s = Math.Sqrt(1d + r11 - r00 - r22) * 2d;
                qw = (r02 - r20) / s;
                qx = (r01 + r10) / s;
                qy = 0.25d * s;
                qz = (r12 + r21) / s;
            }
            else
            {
                double s = Math.Sqrt(1d + r22 - r00 - r11) * 2d;
                qw = (r10 - r01) / s;
                qx = (r02 + r20) / s;
                qy = (r12 + r21) / s;
                qz = 0.25d * s;
            }
            double qn = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (!(qn > 0d)) return false;
            tx = BasisGlbNumbers.PositiveZero(matrix[12]);
            ty = BasisGlbNumbers.PositiveZero(matrix[13]);
            tz = BasisGlbNumbers.PositiveZero(matrix[14]);
            rx = BasisGlbNumbers.PositiveZero((float)(qx / qn));
            ry = BasisGlbNumbers.PositiveZero((float)(qy / qn));
            rz = BasisGlbNumbers.PositiveZero((float)(qz / qn));
            rw = BasisGlbNumbers.PositiveZero((float)(qw / qn));
            sx = BasisGlbNumbers.PositiveZero((float)l0);
            sy = BasisGlbNumbers.PositiveZero((float)l1);
            sz = BasisGlbNumbers.PositiveZero((float)l2);
            return BasisGlbNumbers.IsFinite(sx) && BasisGlbNumbers.IsFinite(sy) && BasisGlbNumbers.IsFinite(sz)
                && BasisGlbNumbers.IsFinite(rx) && BasisGlbNumbers.IsFinite(ry) && BasisGlbNumbers.IsFinite(rz)
                && BasisGlbNumbers.IsFinite(rw);
        }

        // ---- bounds -------------------------------------------------------------------------------------------------

        /// <summary>
        /// World matrices in canonical pre-order, then the default scene's AABB in scene-root space (glTF axes).
        /// Non-skinned instances transform the 8 corners of each primitive's local bounds. Skinned primitives add, for
        /// every vertex and every influence with weight &gt; 0, the point W[joint]·IBM·v: the convex-hull superset valid
        /// for any skin-weight quality setting, including glTFast's sort-and-renormalise path. Matrix entries are
        /// checked as they are computed, so a deep chain of large scales stops before it can overflow.
        /// </summary>
        public static bool TryComputeBounds(BasisGltfWork work)
        {
            BasisGltfPlan plan = work.Plan;
            double maxAbs = work.Limits.MaxWorldMatrixAbs;
            int count = plan.NodeCount;
            var world = new double[count * Stride];
            var local = new double[Stride];
            for (int n = 0; n < count; n++)
            {
                int t = n * 10;
                float[] trs = plan.NodeTrs;
                SetTrs(local, 0, trs[t], trs[t + 1], trs[t + 2], trs[t + 3], trs[t + 4], trs[t + 5], trs[t + 6], trs[t + 7], trs[t + 8], trs[t + 9]);
                int parent = plan.NodeParent[n];
                if (parent < 0) Array.Copy(local, 0, world, n * Stride, Stride);
                else Multiply(world, parent * Stride, local, 0, world, n * Stride);
                if (!AllWithin(world, n * Stride, maxAbs))
                {
                    return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Index("nodes", plan.NodeSource[n])
                        + " has a world transform beyond ±" + BasisGlbErrors.N((long)maxAbs) + ".");
                }
            }

            var bounds = new double[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            for (int n = 0; n < count; n++)
            {
                int meshIndex = plan.NodeMesh[n];
                if (meshIndex < 0) continue;
                if (work.Cancellation.IsCancellationRequested) return work.Cancelled();
                BasisGltfCanonMesh mesh = plan.Meshes[meshIndex];
                bool skinned = plan.NodeSkin[n] >= 0;
                for (int p = 0; p < mesh.Primitives.Count; p++)
                {
                    BasisGltfCanonPrimitive primitive = mesh.Primitives[p];
                    if (skinned && primitive.Joints0 >= 0) continue;
                    // A primitive without joints in a skinned instance gets its own MeshRenderer at the node transform.
                    double[] lb = plan.Accessors[primitive.Position].LocalBounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        double x = (corner & 1) == 0 ? lb[0] : lb[3];
                        double y = (corner & 2) == 0 ? lb[1] : lb[4];
                        double z = (corner & 4) == 0 ? lb[2] : lb[5];
                        TransformPoint(world, n * Stride, x, y, z, out double px, out double py, out double pz);
                        Expand(bounds, px, py, pz);
                    }
                }
                if (skinned)
                {
                    if (mesh.SkinnedBounds == null && !TryComputeSkinnedHull(work, mesh, world)) return false;
                    if (mesh.SkinnedBounds[0] <= mesh.SkinnedBounds[3])
                    {
                        Expand(bounds, mesh.SkinnedBounds[0], mesh.SkinnedBounds[1], mesh.SkinnedBounds[2]);
                        Expand(bounds, mesh.SkinnedBounds[3], mesh.SkinnedBounds[4], mesh.SkinnedBounds[5]);
                    }
                }
            }
            if (!(bounds[0] <= bounds[3]))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, "The model's bounds are empty.");
            }
            var aabb = new BasisGlbAabb
            {
                MinX = BasisGlbNumbers.PositiveZero((float)bounds[0]),
                MinY = BasisGlbNumbers.PositiveZero((float)bounds[1]),
                MinZ = BasisGlbNumbers.PositiveZero((float)bounds[2]),
                MaxX = BasisGlbNumbers.PositiveZero((float)bounds[3]),
                MaxY = BasisGlbNumbers.PositiveZero((float)bounds[4]),
                MaxZ = BasisGlbNumbers.PositiveZero((float)bounds[5]),
            };
            if (!aabb.TryCheckLimits(work.Limits, out string error))
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, error);
            }
            plan.Bounds = aabb;
            return true;
        }

        private static bool TryComputeSkinnedHull(BasisGltfWork work, BasisGltfCanonMesh mesh, double[] world)
        {
            BasisGltfPlan plan = work.Plan;
            BasisGltfCanonSkin skin = plan.Skins[mesh.Skin];
            int jointCount = skin.Joints.Length;
            var joints = new double[jointCount * Stride];
            var ibm = new double[Stride];
            BasisGltfCanonAccessor ibmAccessor = skin.InverseBindMatrices >= 0 ? plan.Accessors[skin.InverseBindMatrices] : null;
            for (int j = 0; j < jointCount; j++)
            {
                if (ibmAccessor == null)
                {
                    SetIdentity(ibm, 0);
                }
                else
                {
                    long element = ibmAccessor.DataOffset + (long)ibmAccessor.SourceStride * j;
                    for (int k = 0; k < Stride; k++) ibm[k] = BasisGltfAccessorScanner.ReadFloat(ibmAccessor.Data, element + 4L * k);
                }
                Multiply(world, skin.Joints[j] * Stride, ibm, 0, joints, j * Stride);
                if (!AllWithin(joints, j * Stride, work.Limits.MaxWorldMatrixAbs))
                {
                    return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Index("skins", skin.Source) + " joint "
                        + BasisGlbErrors.N(j) + " has a skinning transform beyond ±" + BasisGlbErrors.N((long)work.Limits.MaxWorldMatrixAbs) + ".");
                }
            }

            var hull = new double[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            for (int p = 0; p < mesh.Primitives.Count; p++)
            {
                BasisGltfCanonPrimitive primitive = mesh.Primitives[p];
                if (primitive.Joints0 < 0) continue;
                bool seen = false;
                for (int q = 0; q < p && !seen; q++)
                {
                    BasisGltfCanonPrimitive other = mesh.Primitives[q];
                    seen = other.Position == primitive.Position && other.Joints0 == primitive.Joints0 && other.Weights0 == primitive.Weights0;
                }
                if (seen) continue;
                BasisGltfCanonAccessor position = plan.Accessors[primitive.Position];
                BasisGltfCanonAccessor jointsAccessor = plan.Accessors[primitive.Joints0];
                BasisGltfCanonAccessor weights = plan.Accessors[primitive.Weights0];
                int pSize = BasisGltfComponent.Size(position.ComponentType);
                int jSize = BasisGltfComponent.Size(jointsAccessor.ComponentType);
                int wSize = BasisGltfComponent.Size(weights.ComponentType);
                for (int v = 0; v < position.Count; v++)
                {
                    long pe = position.DataOffset + (long)position.SourceStride * v;
                    double x = BasisGltfAccessorScanner.Dequantize(BasisGltfAccessorScanner.ReadRaw(position.Data, pe, position.ComponentType), position.ComponentType, position.Normalized);
                    double y = BasisGltfAccessorScanner.Dequantize(BasisGltfAccessorScanner.ReadRaw(position.Data, pe + pSize, position.ComponentType), position.ComponentType, position.Normalized);
                    double z = BasisGltfAccessorScanner.Dequantize(BasisGltfAccessorScanner.ReadRaw(position.Data, pe + 2 * pSize, position.ComponentType), position.ComponentType, position.Normalized);
                    long je = jointsAccessor.DataOffset + (long)jointsAccessor.SourceStride * v;
                    long we = weights.DataOffset + (long)weights.SourceStride * v;
                    for (int k = 0; k < 4; k++)
                    {
                        double w = BasisGltfAccessorScanner.Dequantize(BasisGltfAccessorScanner.ReadRaw(weights.Data, we + (long)wSize * k, weights.ComponentType), weights.ComponentType, weights.Normalized);
                        if (!(w > 0d)) continue;
                        int joint = (int)BasisGltfAccessorScanner.ReadRaw(jointsAccessor.Data, je + (long)jSize * k, jointsAccessor.ComponentType);
                        TransformPoint(joints, joint * Stride, x, y, z, out double px, out double py, out double pz);
                        Expand(hull, px, py, pz);
                    }
                    if ((v & 65535) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
                }
            }
            mesh.SkinnedBounds = hull;
            return true;
        }

        private static void Expand(double[] bounds, double x, double y, double z)
        {
            if (x < bounds[0]) bounds[0] = x;
            if (y < bounds[1]) bounds[1] = y;
            if (z < bounds[2]) bounds[2] = z;
            if (x > bounds[3]) bounds[3] = x;
            if (y > bounds[4]) bounds[4] = y;
            if (z > bounds[5]) bounds[5] = z;
        }
    }
}
