using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// One pass per retained accessor over its source bytes (unaligned-safe little-endian reads, no
    /// per-element allocation). Indices are bounded by the vertex count (glTFast uses DontValidateIndices), joints by
    /// the skin's joint count (the GPU bone buffer), float data must be finite, skin weights must sum to 1, and
    /// POSITION bounds are recomputed because glTFast trusts min/max for mesh bounds.
    /// </summary>
    public static class BasisGltfAccessorScanner
    {
        private const int CancellationInterval = 65536;

        public static bool TryScan(BasisGltfWork work)
        {
            BasisGltfPlan plan = work.Plan;
            for (int a = 0; a < plan.Accessors.Count; a++)
            {
                if (work.Cancellation.IsCancellationRequested) return work.Cancelled();
                BasisGltfCanonAccessor accessor = plan.Accessors[a];
                switch (accessor.Role)
                {
                    case BasisGltfAccessorRole.Index:
                        if (!TryScanIndices(work, accessor)) return false;
                        break;
                    case BasisGltfAccessorRole.InverseBind:
                        if (!TryScanFinite(work, accessor)) return false;
                        break;
                    default:
                        if (accessor.IsPosition)
                        {
                            if (!TryScanPosition(work, accessor)) return false;
                        }
                        else if (accessor.ComponentType == BasisGltfComponent.Float && !TryScanFinite(work, accessor))
                        {
                            return false;
                        }
                        if (accessor.IsJoints && !TryScanJoints(work, accessor)) return false;
                        if (accessor.IsWeights && !TryScanWeights(work, plan, accessor)) return false;
                        break;
                }
            }

            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                BasisGltfCanonMesh mesh = plan.Meshes[m];
                for (int p = 0; p < mesh.Primitives.Count; p++)
                {
                    BasisGltfCanonPrimitive primitive = mesh.Primitives[p];
                    if (primitive.Indices >= 0 && plan.Accessors[primitive.Indices].MaxIndex >= (uint)primitive.VertexCount)
                    {
                        FindIndexAtOrAbove(plan.Accessors[primitive.Indices], (uint)primitive.VertexCount, out uint value, out int position);
                        return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Primitive(mesh.Source, primitive.SourcePrimitive)
                            + " index " + BasisGlbErrors.N(value) + " at position " + BasisGlbErrors.N(position)
                            + " is not below the vertex count " + BasisGlbErrors.N(primitive.VertexCount) + ".");
                    }
                    if (primitive.Joints0 >= 0)
                    {
                        BasisGltfCanonSkin skin = plan.Skins[mesh.Skin];
                        int maxJoint = plan.Accessors[primitive.Joints0].MaxJoint;
                        if (maxJoint >= skin.Joints.Length)
                        {
                            return work.Fail(BasisGlbErrorKind.Malformed, "JOINTS_0 of " + BasisGlbErrors.Primitive(mesh.Source, primitive.SourcePrimitive)
                                + " uses joint " + BasisGlbErrors.N(maxJoint) + ", but " + BasisGlbErrors.Index("skins", skin.Source)
                                + " has " + BasisGlbErrors.N(skin.Joints.Length) + " joints.");
                        }
                    }
                }
            }
            return true;
        }

        // ---- readers ------------------------------------------------------------------------------------------------

        public static float ReadFloat(byte[] data, long offset)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(data, (int)offset, 4)));
        }

        /// <summary>The raw component value (not normalised).</summary>
        public static double ReadRaw(byte[] data, long offset, int componentType)
        {
            int o = (int)offset;
            switch (componentType)
            {
                case BasisGltfComponent.Float:
                    return ReadFloat(data, offset);
                case BasisGltfComponent.Byte:
                    return (sbyte)data[o];
                case BasisGltfComponent.UnsignedByte:
                    return data[o];
                case BasisGltfComponent.Short:
                    return BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(data, o, 2));
                case BasisGltfComponent.UnsignedShort:
                    return BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(data, o, 2));
                default:
                    return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, o, 4));
            }
        }

        /// <summary>glTF dequantisation, as glTFast's Accessor.TryGetBounds applies it.</summary>
        public static double Dequantize(double raw, int componentType, bool normalized)
        {
            if (!normalized) return raw;
            switch (componentType)
            {
                case BasisGltfComponent.Byte:
                    return Math.Max(raw / 127d, -1d);
                case BasisGltfComponent.UnsignedByte:
                    return raw / 255d;
                case BasisGltfComponent.Short:
                    return Math.Max(raw / 32767d, -1d);
                case BasisGltfComponent.UnsignedShort:
                    return raw / 65535d;
                default:
                    return raw;
            }
        }

        // ---- scans --------------------------------------------------------------------------------------------------

        private static bool TryScanIndices(BasisGltfWork work, BasisGltfCanonAccessor accessor)
        {
            byte[] data = accessor.Data;
            long offset = accessor.DataOffset;
            int count = accessor.Count;
            uint max = 0;
            switch (accessor.ComponentType)
            {
                case BasisGltfComponent.UnsignedByte:
                    for (int i = 0; i < count; i++)
                    {
                        uint v = data[(int)(offset + i)];
                        if (v > max) max = v;
                    }
                    break;
                case BasisGltfComponent.UnsignedShort:
                    for (int i = 0; i < count; i++)
                    {
                        uint v = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(data, (int)(offset + 2L * i), 2));
                        if (v > max) max = v;
                        if ((i & (CancellationInterval - 1)) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
                    }
                    break;
                default:
                    for (int i = 0; i < count; i++)
                    {
                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, (int)(offset + 4L * i), 4));
                        if (v > max) max = v;
                        if ((i & (CancellationInterval - 1)) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
                    }
                    break;
            }
            accessor.MaxIndex = max;
            return true;
        }

        private static void FindIndexAtOrAbove(BasisGltfCanonAccessor accessor, uint limit, out uint value, out int position)
        {
            int size = BasisGltfComponent.Size(accessor.ComponentType);
            for (int i = 0; i < accessor.Count; i++)
            {
                uint v = (uint)ReadRaw(accessor.Data, accessor.DataOffset + (long)size * i, accessor.ComponentType);
                if (v >= limit)
                {
                    value = v;
                    position = i;
                    return;
                }
            }
            value = accessor.MaxIndex;
            position = -1;
        }

        private static bool TryScanFinite(BasisGltfWork work, BasisGltfCanonAccessor accessor)
        {
            if (accessor.ComponentType != BasisGltfComponent.Float) return true;
            int components = BasisGltfType.Components(accessor.Type);
            byte[] data = accessor.Data;
            for (int i = 0; i < accessor.Count; i++)
            {
                long element = accessor.DataOffset + (long)accessor.SourceStride * i;
                for (int c = 0; c < components; c++)
                {
                    if (!BasisGlbNumbers.IsFinite(ReadFloat(data, element + 4L * c)))
                    {
                        return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("accessors", accessor.Source)
                            + " has a non-finite value at element " + BasisGlbErrors.N(i) + ".");
                    }
                }
                if ((i & (CancellationInterval - 1)) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
            }
            return true;
        }

        private static bool TryScanPosition(BasisGltfWork work, BasisGltfCanonAccessor accessor)
        {
            byte[] data = accessor.Data;
            int componentType = accessor.ComponentType;
            int size = BasisGltfComponent.Size(componentType);
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < accessor.Count; i++)
            {
                long element = accessor.DataOffset + (long)accessor.SourceStride * i;
                double x = ReadRaw(data, element, componentType);
                double y = ReadRaw(data, element + size, componentType);
                double z = ReadRaw(data, element + 2 * size, componentType);
                if (componentType == BasisGltfComponent.Float && !(BasisGlbNumbers.IsFinite(x) && BasisGlbNumbers.IsFinite(y) && BasisGlbNumbers.IsFinite(z)))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("accessors", accessor.Source)
                        + " has a non-finite position at element " + BasisGlbErrors.N(i) + ".");
                }
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
                if ((i & (CancellationInterval - 1)) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
            }
            // Raw-domain values are exact floats (float data, or integers up to 65,535).
            accessor.Min = new[] { BasisGlbNumbers.PositiveZero((float)minX), BasisGlbNumbers.PositiveZero((float)minY), BasisGlbNumbers.PositiveZero((float)minZ) };
            accessor.Max = new[] { BasisGlbNumbers.PositiveZero((float)maxX), BasisGlbNumbers.PositiveZero((float)maxY), BasisGlbNumbers.PositiveZero((float)maxZ) };
            bool normalized = accessor.Normalized;
            accessor.LocalBounds = new[]
            {
                Dequantize(minX, componentType, normalized), Dequantize(minY, componentType, normalized), Dequantize(minZ, componentType, normalized),
                Dequantize(maxX, componentType, normalized), Dequantize(maxY, componentType, normalized), Dequantize(maxZ, componentType, normalized),
            };
            return true;
        }

        private static bool TryScanJoints(BasisGltfWork work, BasisGltfCanonAccessor accessor)
        {
            // Every component counts, whatever its weight: an out-of-range index reads past the GPU bone buffer.
            byte[] data = accessor.Data;
            int componentType = accessor.ComponentType;
            int size = BasisGltfComponent.Size(componentType);
            int max = 0;
            for (int i = 0; i < accessor.Count; i++)
            {
                long element = accessor.DataOffset + (long)accessor.SourceStride * i;
                for (int c = 0; c < 4; c++)
                {
                    int joint = (int)ReadRaw(data, element + (long)size * c, componentType);
                    if (joint > max) max = joint;
                }
                if ((i & (CancellationInterval - 1)) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
            }
            accessor.MaxJoint = max;
            return true;
        }

        private static bool TryScanWeights(BasisGltfWork work, BasisGltfPlan plan, BasisGltfCanonAccessor accessor)
        {
            byte[] data = accessor.Data;
            int componentType = accessor.ComponentType;
            int size = BasisGltfComponent.Size(componentType);
            for (int i = 0; i < accessor.Count; i++)
            {
                long element = accessor.DataOffset + (long)accessor.SourceStride * i;
                double sum = 0d;
                for (int c = 0; c < 4; c++)
                {
                    double weight = Dequantize(ReadRaw(data, element + (long)size * c, componentType), componentType, accessor.Normalized);
                    if (!(weight >= 0d) || double.IsInfinity(weight))
                    {
                        return work.Fail(BasisGlbErrorKind.Malformed, "WEIGHTS_0 of " + FirstUser(plan, accessor) + " vertex " + BasisGlbErrors.N(i)
                            + " has a negative or non-finite weight.");
                    }
                    sum += weight;
                }
                // A sum of 0 collapses the vertex to the world origin.
                if (!(Math.Abs(sum - 1d) <= 0.01d))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, "WEIGHTS_0 of " + FirstUser(plan, accessor) + " vertex " + BasisGlbErrors.N(i)
                        + " sum to " + sum.ToString("0.###", CultureInfo.InvariantCulture) + "; skin weights must sum to 1 (±0.01).");
                }
                if ((i & (CancellationInterval - 1)) == 0 && work.Cancellation.IsCancellationRequested) return work.Cancelled();
            }
            return true;
        }

        private static string FirstUser(BasisGltfPlan plan, BasisGltfCanonAccessor accessor)
        {
            if (accessor.FirstMesh < 0) return BasisGlbErrors.Index("accessors", accessor.Source);
            return BasisGlbErrors.Primitive(plan.Meshes[accessor.FirstMesh].Source, accessor.FirstPrimitive);
        }
    }
}
