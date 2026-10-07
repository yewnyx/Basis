using System.Collections.Generic;

namespace Basis.ModelPickup.Validation
{
    public enum BasisGltfAccessorRole : byte
    {
        None = 0,
        Index = 1,
        Vertex = 2,
        InverseBind = 3,
    }

    /// <summary>A retained accessor in canonical order. Its canonical bufferView has the same index.</summary>
    public sealed class BasisGltfCanonAccessor
    {
        public int Source;
        public BasisGltfAccessorRole Role;
        public bool IsPosition, IsJoints, IsWeights;
        public int ComponentType;
        public byte Type;
        public bool Normalized;
        /// <summary>Canonical element count; inverse bind matrices are trimmed to the joint count.</summary>
        public int Count;
        public int ElementSize;
        public int SourceStride;
        public byte[] Data;
        public long DataOffset;
        public int DestStride;
        public long ViewOffset, ViewLength;
        /// <summary>Raw-domain POSITION min/max (written as the canonical min/max).</summary>
        public float[] Min, Max;
        /// <summary>Dequantised POSITION bounds in metres: minX, minY, minZ, maxX, maxY, maxZ.</summary>
        public double[] LocalBounds;
        public uint MaxIndex;
        public int MaxJoint;
        public int FirstMesh = -1, FirstPrimitive = -1;
    }

    public struct BasisGltfCanonPrimitive
    {
        public int SourceMesh, SourcePrimitive;
        public int Position, Normal, Tangent, TexCoord0, TexCoord1, Color0, Joints0, Weights0, Indices;
        public int Material;
        public int VertexCount;
        /// <summary>Index count, or the vertex count for a non-indexed primitive (glTFast generates indices).</summary>
        public int IndexCount;
    }

    /// <summary>glTFast's per-mesh cluster: primitives with the same vertex layout share one Unity mesh.</summary>
    public sealed class BasisGltfCluster
    {
        public bool HasNormals, HasTangents, HasColors, HasBones;
        public int UvSets;
        public long Vertices;
        public long Indices;
        public List<int> Primitives = new List<int>();
        public List<int> TupleFirstPrimitive = new List<int>();
    }

    public sealed class BasisGltfCanonMesh
    {
        public int Source;
        public List<BasisGltfCanonPrimitive> Primitives = new List<BasisGltfCanonPrimitive>();
        public int Skin = -1;
        public List<BasisGltfCluster> Clusters = new List<BasisGltfCluster>();
        public long Vertices;
        public long Triangles;
        /// <summary>Scene-space hull of the skinned primitives (computed once per mesh; one skin per mesh).</summary>
        public double[] SkinnedBounds;
    }

    public sealed class BasisGltfCanonSkin
    {
        public int Source;
        public int[] Joints;
        public int Skeleton = -1;
        public int InverseBindMatrices = -1;
    }

    public struct BasisGltfCanonTextureRef
    {
        public int Texture;
        public int TexCoord;
        public float ScaleOrStrength;
        public bool HasTransform;
        public float OffsetU, OffsetV, Rotation, ScaleU, ScaleV;
    }

    public sealed class BasisGltfCanonMaterial
    {
        public int Source;
        public float[] BaseColor = new float[4];
        public float[] Emissive = new float[3];
        public float Metallic, Roughness, AlphaCutoff;
        public BasisGltfCanonTextureRef BaseColorTex, MetallicRoughnessTex, NormalTex, OcclusionTex, EmissiveTex;
        public byte AlphaMode;
        public bool DoubleSided, Unlit;
    }

    public struct BasisGltfCanonTexture
    {
        public int SourceImage;
        public int Image;
        public int Sampler;
        public int SamplerKey;
    }

    public struct BasisGltfCanonSampler
    {
        public int Mag, Min, WrapS, WrapT;
    }

    public sealed class BasisGltfCanonImage
    {
        public int Source;
        public BasisModelImageFormat Format;
        public byte[] SourceData;
        public int SourceOffset, SourceLength;
        public byte[] Final;
        public int Width, Height;
        public long ViewOffset;
    }

    /// <summary>
    /// The canonical form, in canonical order. Applying the canonical order to canonical input yields the identity
    /// mapping, which is why canonicalisation is idempotent by construction.
    /// </summary>
    public sealed class BasisGltfPlan
    {
        public int NodeCount;
        public int[] NodeSource;
        public int[] NodeParent;
        public int[] NodeMesh;
        public int[] NodeSkin;
        public int[][] NodeChildren;
        /// <summary>10 floats per node: tx, ty, tz, rx, ry, rz, rw, sx, sy, sz.</summary>
        public float[] NodeTrs;
        public int[] SceneRoots;
        public int[] SourceToNode;

        public List<BasisGltfCanonMesh> Meshes = new List<BasisGltfCanonMesh>();
        public List<BasisGltfCanonSkin> Skins = new List<BasisGltfCanonSkin>();
        public List<BasisGltfCanonMaterial> Materials = new List<BasisGltfCanonMaterial>();
        public List<BasisGltfCanonTexture> Textures = new List<BasisGltfCanonTexture>();
        public List<BasisGltfCanonSampler> Samplers = new List<BasisGltfCanonSampler>();
        public List<BasisGltfCanonImage> Images = new List<BasisGltfCanonImage>();
        public List<BasisGltfCanonAccessor> Accessors = new List<BasisGltfCanonAccessor>();

        public bool UsesQuantization, UsesUnlit, UsesTextureTransform;
        public BasisGlbAabb Bounds;
        public long BinLength;

        public int MeshInstances, SkinnedMeshInstances, DrawCalls, Primitives, Joints;
        public long Vertices, Triangles, RenderedTriangles, SkinnedVertexInstances;
    }

    /// <summary>
    /// Re-indexing results live in <see cref="BasisGltfPlan"/> (built by the semantic validator in canonical order);
    /// this class adds glTFast's vertex clustering, the totals that bound later work, and the BIN layout.
    /// </summary>
    public static class BasisGltfCanonicalizer
    {
        /// <summary>Mirrors glTFast LoadAccessorData: cluster by VertexBufferDescriptor, share buffers by equal attributes.</summary>
        public static void BuildClusters(BasisGltfPlan plan)
        {
            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                BasisGltfCanonMesh mesh = plan.Meshes[m];
                mesh.Clusters.Clear();
                mesh.Vertices = 0;
                mesh.Triangles = 0;
                for (int p = 0; p < mesh.Primitives.Count; p++)
                {
                    BasisGltfCanonPrimitive primitive = mesh.Primitives[p];
                    mesh.Triangles += primitive.IndexCount / 3;
                    bool normals = primitive.Normal >= 0;
                    bool tangents = primitive.Tangent >= 0;
                    int uvSets = primitive.TexCoord0 < 0 ? 0 : primitive.TexCoord1 < 0 ? 1 : 2;
                    bool colors = primitive.Color0 >= 0;
                    bool bones = primitive.Joints0 >= 0 && primitive.Weights0 >= 0;
                    BasisGltfCluster cluster = null;
                    for (int c = 0; c < mesh.Clusters.Count; c++)
                    {
                        BasisGltfCluster candidate = mesh.Clusters[c];
                        if (candidate.HasNormals == normals && candidate.HasTangents == tangents && candidate.UvSets == uvSets
                            && candidate.HasColors == colors && candidate.HasBones == bones)
                        {
                            cluster = candidate;
                            break;
                        }
                    }
                    if (cluster == null)
                    {
                        cluster = new BasisGltfCluster
                        {
                            HasNormals = normals,
                            HasTangents = tangents,
                            UvSets = uvSets,
                            HasColors = colors,
                            HasBones = bones,
                        };
                        mesh.Clusters.Add(cluster);
                    }
                    cluster.Primitives.Add(p);
                    cluster.Indices += primitive.IndexCount;
                    bool shared = false;
                    for (int t = 0; t < cluster.TupleFirstPrimitive.Count; t++)
                    {
                        if (SameAttributes(mesh.Primitives[cluster.TupleFirstPrimitive[t]], primitive))
                        {
                            shared = true;
                            break;
                        }
                    }
                    if (!shared)
                    {
                        cluster.TupleFirstPrimitive.Add(p);
                        cluster.Vertices += primitive.VertexCount;
                        mesh.Vertices += primitive.VertexCount;
                    }
                }
            }
        }

        public static bool SameAttributes(in BasisGltfCanonPrimitive a, in BasisGltfCanonPrimitive b)
        {
            return a.Position == b.Position && a.Normal == b.Normal && a.Tangent == b.Tangent && a.TexCoord0 == b.TexCoord0
                && a.TexCoord1 == b.TexCoord1 && a.Color0 == b.Color0 && a.Joints0 == b.Joints0 && a.Weights0 == b.Weights0;
        }

        /// <summary>
        /// Vertex, triangle and instance totals, checked before any data scan or skinned-hull pass so those costs are
        /// bounded. Accumulates in long and narrows only after the limit check.
        /// </summary>
        public static bool TryComputeTotals(BasisGltfWork work)
        {
            BasisGltfPlan plan = work.Plan;
            BasisModelLimits limits = work.Limits;
            BuildClusters(plan);
            if (!TryCheckVertexLayouts(work)) return false;
            long vertices = 0, triangles = 0, rendered = 0, skinnedVertices = 0;
            int primitives = 0;
            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                vertices += plan.Meshes[m].Vertices;
                triangles += plan.Meshes[m].Triangles;
                primitives += plan.Meshes[m].Primitives.Count;
            }
            int instances = 0, skinnedInstances = 0;
            long drawCalls = 0;
            for (int n = 0; n < plan.NodeCount; n++)
            {
                int mesh = plan.NodeMesh[n];
                if (mesh < 0) continue;
                instances++;
                drawCalls += plan.Meshes[mesh].Primitives.Count;
                rendered += plan.Meshes[mesh].Triangles;
                if (plan.NodeSkin[n] >= 0)
                {
                    skinnedInstances++;
                    skinnedVertices += plan.Meshes[mesh].Vertices;
                }
            }
            // Fewer than 3 vertices can only form degenerate triangles; claims admission relies on this floor.
            if (vertices < 3) return work.Fail(BasisGlbErrorKind.Malformed, "The model has fewer than 3 vertices, so it has no triangles to display.");
            if (vertices > limits.MaxVertices) return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Vertex count", vertices, limits.MaxVertices));
            if (triangles > limits.MaxTriangles) return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Triangle count", triangles, limits.MaxTriangles));
            if (rendered > limits.MaxRenderedTriangles) return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Rendered triangle count", rendered, limits.MaxRenderedTriangles));
            if (skinnedVertices > limits.MaxSkinnedVertexInstances) return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Skinned vertex instance count", skinnedVertices, limits.MaxSkinnedVertexInstances));
            if (instances > limits.MaxMeshInstances) return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Mesh instance count", instances, limits.MaxMeshInstances));
            if (drawCalls > limits.MaxDrawCalls) return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Draw call count", drawCalls, limits.MaxDrawCalls));
            plan.Vertices = vertices;
            plan.Triangles = triangles;
            plan.RenderedTriangles = rendered;
            plan.SkinnedVertexInstances = skinnedVertices;
            plan.MeshInstances = instances;
            plan.SkinnedMeshInstances = skinnedInstances;
            plan.DrawCalls = (int)drawCalls;
            plan.Primitives = primitives;
            return true;
        }

        /// <summary>
        /// glTFast picks a cluster's vertex layout from its first primitive plus what the materials need
        /// (MeshGenerator.GetMainBufferType). With no NORMAL and no TANGENT, a cluster whose materials all want tangents
        /// (a normal texture) and none want plain normals ends up as Position|Tangent, which glTFast has no layout for:
        /// the mesh is never built and the whole load fails. Reject it here with a clear reason instead.
        /// </summary>
        private static bool TryCheckVertexLayouts(BasisGltfWork work)
        {
            BasisGltfPlan plan = work.Plan;
            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                BasisGltfCanonMesh mesh = plan.Meshes[m];
                for (int c = 0; c < mesh.Clusters.Count; c++)
                {
                    BasisGltfCluster cluster = mesh.Clusters[c];
                    if (cluster.HasNormals || cluster.HasTangents) continue;
                    bool wantsNormals = false, wantsTangents = false;
                    for (int i = 0; i < cluster.Primitives.Count; i++)
                    {
                        int material = mesh.Primitives[cluster.Primitives[i]].Material;
                        if (material < 0)
                        {
                            wantsNormals = true;
                            continue;
                        }
                        BasisGltfCanonMaterial canon = plan.Materials[material];
                        if (canon.NormalTex.Texture >= 0) wantsTangents = true;
                        else if (!canon.Unlit) wantsNormals = true;
                    }
                    if (wantsTangents && !wantsNormals)
                    {
                        BasisGltfCanonPrimitive first = mesh.Primitives[cluster.Primitives[0]];
                        return work.Fail(BasisGlbErrorKind.Unsupported, BasisGlbErrors.Primitive(first.SourceMesh, first.SourcePrimitive)
                            + " has no NORMAL but its material uses a normal texture; glTFast cannot build that mesh.");
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// BIN layout: view i = accessor i, view A + k = image k; each view starts at a 4-byte boundary. Vertex elements
        /// that are not a multiple of 4 bytes are padded to a 4-byte stride (the view then declares byteStride).
        /// The exact size is known before the single output allocation.
        /// </summary>
        public static bool TryLayout(BasisGltfWork work)
        {
            BasisGltfPlan plan = work.Plan;
            long offset = 0;
            for (int a = 0; a < plan.Accessors.Count; a++)
            {
                BasisGltfCanonAccessor accessor = plan.Accessors[a];
                offset = Align4(offset);
                accessor.DestStride = accessor.Role == BasisGltfAccessorRole.Vertex && accessor.ElementSize % 4 != 0
                    ? (accessor.ElementSize + 3) & ~3
                    : accessor.ElementSize;
                accessor.ViewOffset = offset;
                accessor.ViewLength = (long)accessor.Count * accessor.DestStride;
                offset += accessor.ViewLength;
            }
            for (int i = 0; i < plan.Images.Count; i++)
            {
                BasisGltfCanonImage image = plan.Images[i];
                offset = Align4(offset);
                image.ViewOffset = offset;
                offset += image.Final.Length;
            }
            plan.BinLength = offset;
            if (offset > work.Limits.MaxModelBytes)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes("Canonical model data", offset, work.Limits.MaxModelBytes));
            }
            return true;
        }

        public static long Align4(long value)
        {
            return (value + 3) & ~3L;
        }
    }
}
