using System;
using System.Collections.Generic;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Stats from the canonical plan, mirroring how glTFast 6.14.1 will spend memory, and the remaining limit
    /// checks. Everything is computed from canonical identities, so the sender (from its source) and the receiver (from
    /// the canonical bytes) get identical stats.
    /// </summary>
    public static class BasisGlbStatsCalculator
    {
        public static bool TryCompute(BasisGltfWork work, int canonicalBytes, int jsonChunkBytes, int binChunkBytes, out BasisGlbStats stats)
        {
            BasisGltfPlan plan = work.Plan;
            BasisModelLimits limits = work.Limits;
            stats = new BasisGlbStats
            {
                CanonicalBytes = canonicalBytes,
                JsonBytes = jsonChunkBytes,
                BinBytes = binChunkBytes,
                Nodes = plan.NodeCount,
                Meshes = plan.Meshes.Count,
                Primitives = plan.Primitives,
                Materials = plan.Materials.Count,
                Textures = plan.Textures.Count,
                Images = plan.Images.Count,
                Samplers = plan.Samplers.Count,
                Skins = plan.Skins.Count,
                Joints = plan.Joints,
                Accessors = plan.Accessors.Count,
                MeshInstances = plan.MeshInstances,
                SkinnedMeshInstances = plan.SkinnedMeshInstances,
                DrawCalls = plan.DrawCalls,
                Vertices = (int)plan.Vertices,
                Triangles = (int)plan.Triangles,
                RenderedTriangles = plan.RenderedTriangles,
                SkinnedVertexInstances = plan.SkinnedVertexInstances,
                HasSkins = plan.Skins.Count > 0,
                UsesQuantization = plan.UsesQuantization,
                UsesUnlit = plan.UsesUnlit,
                UsesTextureTransform = plan.UsesTextureTransform,
                Bounds = plan.Bounds,
            };

            // Meshes: one Unity mesh per cluster. Position, normal and tangent are always counted because glTFast
            // may compute them; indices are always UInt32; meshes stay CPU-readable in 6.14.1, hence ×2.
            long meshBytes = 0, temporaryBytes = 0;
            long recalculatedNormals = 0, recalculatedTangents = 0;
            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                BasisGltfCanonMesh mesh = plan.Meshes[m];
                for (int c = 0; c < mesh.Clusters.Count; c++)
                {
                    BasisGltfCluster cluster = mesh.Clusters[c];
                    long perVertex = 40 + (cluster.HasColors ? 16 : 0) + 8L * cluster.UvSets + (cluster.HasBones ? 32 : 0);
                    long vb = cluster.Vertices * perVertex;
                    long ib = cluster.Indices * 4;
                    meshBytes += (vb + ib) * 2;
                    temporaryBytes += vb + ib;
                    GetRecalculation(plan, mesh, cluster, out bool normals, out bool tangents);
                    if (normals) recalculatedNormals += cluster.Vertices;
                    if (tangents) recalculatedTangents += cluster.Vertices;
                }
            }
            long skinnedBytes = 0;
            for (int n = 0; n < plan.NodeCount; n++)
            {
                if (plan.NodeSkin[n] >= 0 && plan.NodeMesh[n] >= 0) skinnedBytes += plan.Meshes[plan.NodeMesh[n]].Vertices * 40;
            }

            // Textures: glTFast clones an image per extra sampler key, and keeps it readable when more than one sampler
            // index uses it; mips add a third.
            long textureBytes = 0, texturePixels = 0, imageBytes = 0;
            int maxDimension = 0;
            var keys = new List<int>(4);
            var samplerIndices = new List<int>(4);
            for (int i = 0; i < plan.Images.Count; i++)
            {
                BasisGltfCanonImage image = plan.Images[i];
                keys.Clear();
                samplerIndices.Clear();
                for (int t = 0; t < plan.Textures.Count; t++)
                {
                    BasisGltfCanonTexture texture = plan.Textures[t];
                    if (texture.Image != i) continue;
                    int key = GltfastSamplerKey(plan, texture.Sampler);
                    if (!keys.Contains(key)) keys.Add(key);
                    if (!samplerIndices.Contains(texture.Sampler)) samplerIndices.Add(texture.Sampler);
                }
                long pixels = (long)image.Width * image.Height;
                long variants = Math.Max(1, keys.Count);
                texturePixels += pixels * variants;
                textureBytes += pixels * 16 / 3 * variants * (samplerIndices.Count > 1 ? 2 : 1);
                imageBytes += image.Final.Length;
                maxDimension = Math.Max(maxDimension, Math.Max(image.Width, image.Height));
            }

            stats.TexturePixels = texturePixels;
            stats.MaxTextureDimension = maxDimension;
            stats.EstimatedDecodedBytes = meshBytes + skinnedBytes + textureBytes;
            stats.EstimatedPeakBytes = stats.EstimatedDecodedBytes + canonicalBytes + imageBytes + temporaryBytes;
            stats.RecalculatedNormalVertices = (int)Math.Min(recalculatedNormals, int.MaxValue);
            stats.RecalculatedTangentVertices = (int)Math.Min(recalculatedTangents, int.MaxValue);

            if (texturePixels > limits.MaxTotalTexturePixels)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Total texture pixel count", texturePixels, limits.MaxTotalTexturePixels));
            }
            if (stats.EstimatedDecodedBytes > limits.MaxEstimatedDecodedBytes)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes("Estimated decoded memory", stats.EstimatedDecodedBytes, limits.MaxEstimatedDecodedBytes));
            }
            return true;
        }

        /// <summary>glTFast's SamplerKey(FilterMode, WrapU, WrapV); -1 is new Sampler() (no filters, repeat).</summary>
        private static int GltfastSamplerKey(BasisGltfPlan plan, int sampler)
        {
            int min = 0, mag = 0, wrapS = 10497, wrapT = 10497;
            if (sampler >= 0)
            {
                BasisGltfCanonSampler s = plan.Samplers[sampler];
                min = s.Min;
                mag = s.Mag;
                wrapS = s.WrapS;
                wrapT = s.WrapT;
            }
            int filter;
            if (min == 9987) filter = 2;                                      // trilinear
            else if (min == 9728 || min == 9984 || min == 9986) filter = 0;   // point
            else filter = mag == 9728 ? 0 : 1;                                // point or bilinear
            return filter * 9 + Wrap(wrapS) * 3 + Wrap(wrapT);
        }

        private static int Wrap(int wrap)
        {
            return wrap == 33071 ? 1 : wrap == 33648 ? 2 : 0;
        }

        /// <summary>
        /// MeshGenerator.GetMainBufferType: the first primitive picks Position, PosNorm or PosNormTan; then each primitive's
        /// material ORs in Tangent (normal texture) or Normal (lit, or no material). Missing streams in the result are
        /// recalculated on the main thread.
        /// </summary>
        private static void GetRecalculation(BasisGltfPlan plan, BasisGltfCanonMesh mesh, BasisGltfCluster cluster, out bool normals, out bool tangents)
        {
            const int Normal = 2, Tangent = 4;
            int main = cluster.HasTangents ? 7 : cluster.HasNormals ? 3 : 1;
            for (int i = 0; i < cluster.Primitives.Count; i++)
            {
                int material = mesh.Primitives[cluster.Primitives[i]].Material;
                if (material < 0)
                {
                    main |= Normal;
                    continue;
                }
                BasisGltfCanonMaterial canon = plan.Materials[material];
                if (canon.NormalTex.Texture >= 0) main |= Tangent;
                else if (!canon.Unlit) main |= Normal;
            }
            normals = !cluster.HasNormals && (main & Normal) != 0;
            tangents = !cluster.HasTangents && (main & Tangent) != 0;
        }
    }
}
