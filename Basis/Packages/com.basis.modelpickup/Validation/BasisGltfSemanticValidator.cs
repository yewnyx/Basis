using System;
using System.Collections.Generic;
using System.Globalization;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// Header and extensions, the node graph, references and tables, and buffer resolution. Checks run in
    /// order and stop at the first failure; paths use source indices. Only retained objects are checked: anything the
    /// default scene cannot reach is dropped (and flagged), never validated. The canonical plan is built here, in
    /// canonical order, so every later stage works on canonical identities.
    /// </summary>
    public static class BasisGltfSemanticValidator
    {
        private const string ExtUnlit = "KHR_materials_unlit";
        private const string ExtTextureTransform = "KHR_texture_transform";
        private const string ExtQuantization = "KHR_mesh_quantization";
        private const string ExtEmissiveStrength = "KHR_materials_emissive_strength";
        private const int MaxUriCharsShown = 64;

        // ---- header and extensions ----------------------------------------------------------------------------------

        public static bool TryCheckHeader(BasisGltfWork work)
        {
            BasisGltfDocument doc = work.Document;
            if (!doc.HasAsset) return work.Fail(BasisGlbErrorKind.Malformed, "The glTF has no asset object.");
            if (doc.AssetVersion == null) return work.Fail(BasisGlbErrorKind.Malformed, "asset.version is missing.");
            if (!IsVersion2(doc.AssetVersion)) return work.Fail(BasisGlbErrorKind.Unsupported, "asset.version must be 2.x; only glTF 2 is supported.");
            if (doc.AssetMinVersion != null && doc.AssetMinVersion != "2.0")
            {
                return work.Fail(BasisGlbErrorKind.Unsupported, "asset.minVersion requires a glTF version newer than 2.0.");
            }
            for (int i = 0; i < doc.ExtensionsRequired.Count; i++)
            {
                string name = doc.ExtensionsRequired[i];
                if (!doc.ExtensionsUsed.Contains(name))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, work.Sender
                        ? "extensionsRequired lists \"" + BasisGlbErrors.ForDisplay(name, 64) + "\" which is missing from extensionsUsed."
                        : BasisGlbErrors.Index("extensionsRequired", i) + " is missing from extensionsUsed.");
                }
                if (name != ExtUnlit && name != ExtTextureTransform && name != ExtQuantization && name != ExtEmissiveStrength)
                {
                    return work.Fail(BasisGlbErrorKind.Unsupported, work.Sender
                        ? "The model requires the glTF extension \"" + BasisGlbErrors.ForDisplay(name, 64) + "\", which Basis does not support."
                        : "The model requires an unsupported glTF extension (" + BasisGlbErrors.Index("extensionsRequired", i) + ").");
                }
            }
            for (int i = 0; i < doc.ExtensionsUsed.Count; i++)
            {
                doc.Stripped |= ClassifyDeclaredExtension(doc.ExtensionsUsed[i]);
            }
            return true;
        }

        private static bool IsVersion2(string version)
        {
            if (version.Length < 3 || version[0] != '2' || version[1] != '.') return false;
            for (int i = 2; i < version.Length; i++)
            {
                if (version[i] < '0' || version[i] > '9') return false;
            }
            return true;
        }

        private static BasisGlbStripped ClassifyDeclaredExtension(string name)
        {
            switch (name)
            {
                case ExtUnlit:
                case ExtTextureTransform:
                case ExtQuantization:
                    return BasisGlbStripped.None;
                case ExtEmissiveStrength:
                    return BasisGlbStripped.MaterialExtensions;
                case "KHR_lights_punctual":
                    return BasisGlbStripped.Lights;
                case "EXT_mesh_gpu_instancing":
                    return BasisGlbStripped.Instancing;
                case "KHR_materials_variants":
                    return BasisGlbStripped.MaterialVariants;
                case "KHR_draco_mesh_compression":
                case "EXT_meshopt_compression":
                case "KHR_meshopt_compression":
                case "KHR_texture_basisu":
                case "EXT_texture_webp":
                case "EXT_texture_avif":
                case "MSFT_texture_dds":
                    return BasisGlbStripped.CompressionFallbackUsed;
                default:
                    return name.StartsWith("KHR_materials_", StringComparison.Ordinal)
                        ? BasisGlbStripped.MaterialExtensions
                        : BasisGlbStripped.OtherExtensions;
            }
        }

        // ---- node graph ---------------------------------------------------------------------------------------------

        public static bool TryBuildGraph(BasisGltfWork work)
        {
            BasisGltfDocument doc = work.Document;
            BasisModelLimits limits = work.Limits;
            List<BasisGltfNode> nodes = doc.Nodes;
            int nodeCount = nodes.Count;

            int sceneIndex;
            if (doc.HasScene)
            {
                if (doc.Scene >= doc.Scenes.Count)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, "scene " + BasisGlbErrors.N(doc.Scene) + " is out of range; the model has "
                        + BasisGlbErrors.N(doc.Scenes.Count) + " scenes.");
                }
                sceneIndex = doc.Scene;
            }
            else if (doc.Scenes.Count > 0)
            {
                sceneIndex = 0;
            }
            else
            {
                return work.Fail(BasisGlbErrorKind.Malformed, "The model has no scene to display.");
            }
            if (doc.Scenes.Count > 1) doc.Stripped |= BasisGlbStripped.ExtraScenes;

            var parent = new int[nodeCount];
            var stamp = new int[nodeCount];
            for (int i = 0; i < nodeCount; i++) parent[i] = -1;
            for (int i = 0; i < nodeCount; i++)
            {
                int[] children = nodes[i].Children;
                if (children == null) continue;
                for (int j = 0; j < children.Length; j++)
                {
                    int child = children[j];
                    if (child >= nodeCount)
                    {
                        return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", i, "children") + "[" + Num(j) + "] is out of range.");
                    }
                    if (stamp[child] == i + 1)
                    {
                        return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", i, "children") + " lists "
                            + BasisGlbErrors.Index("nodes", child) + " twice.");
                    }
                    stamp[child] = i + 1;
                    if (parent[child] >= 0)
                    {
                        return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", child) + " is a child of both "
                            + BasisGlbErrors.Index("nodes", parent[child]) + " and " + BasisGlbErrors.Index("nodes", i) + ".");
                    }
                    parent[child] = i;
                }
            }

            int[] roots = doc.Scenes[sceneIndex].Nodes ?? Array.Empty<int>();
            Array.Clear(stamp, 0, nodeCount);
            for (int k = 0; k < roots.Length; k++)
            {
                int root = roots[k];
                string path = BasisGlbErrors.Index("scenes", sceneIndex, "nodes");
                if (root >= nodeCount) return work.Fail(BasisGlbErrorKind.Malformed, path + "[" + Num(k) + "] is out of range.");
                if (stamp[root] != 0) return work.Fail(BasisGlbErrorKind.Malformed, path + " lists " + BasisGlbErrors.Index("nodes", root) + " twice.");
                stamp[root] = 1;
                if (parent[root] >= 0)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, path + "[" + Num(k) + "] is a child of "
                        + BasisGlbErrors.Index("nodes", parent[root]) + "; scene roots must have no parent.");
                }
            }
            if (roots.Length == 0) return work.Fail(BasisGlbErrorKind.Malformed, "The model has no scene to display.");

            // Iterative pre-order DFS: roots in order, children in array order. Closes glTFast's unbounded
            // IterateNodes recursion and the parent-walk hang on cycles; unreachable nodes are simply dropped.
            var sourceToNode = new int[nodeCount];
            for (int i = 0; i < nodeCount; i++) sourceToNode[i] = -1;
            var order = new List<int>(Math.Min(nodeCount, limits.MaxNodes + 1));
            var stackNode = new int[nodeCount];
            var stackDepth = new int[nodeCount];
            int top = 0;
            for (int k = roots.Length - 1; k >= 0; k--)
            {
                stackNode[top] = roots[k];
                stackDepth[top] = 1;
                top++;
            }
            while (top > 0)
            {
                top--;
                int node = stackNode[top];
                int depth = stackDepth[top];
                if (sourceToNode[node] >= 0)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, "The node graph has a cycle at " + BasisGlbErrors.Index("nodes", node) + ".");
                }
                if (depth > limits.MaxNodeDepth)
                {
                    return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("Node depth", depth, limits.MaxNodeDepth));
                }
                if (order.Count >= limits.MaxNodes)
                {
                    return work.Fail(BasisGlbErrorKind.OverLimit, "The displayed scene has more than " + BasisGlbErrors.N(limits.MaxNodes)
                        + " nodes. The maximum is " + BasisGlbErrors.N(limits.MaxNodes) + ".");
                }
                sourceToNode[node] = order.Count;
                order.Add(node);
                int[] children = nodes[node].Children;
                if (children == null) continue;
                for (int j = children.Length - 1; j >= 0; j--)
                {
                    if (top >= stackNode.Length)
                    {
                        return work.Fail(BasisGlbErrorKind.Malformed, "The node graph has a cycle at " + BasisGlbErrors.Index("nodes", children[j]) + ".");
                    }
                    stackNode[top] = children[j];
                    stackDepth[top] = depth + 1;
                    top++;
                }
            }
            if (order.Count < nodeCount) doc.Stripped |= BasisGlbStripped.UnreferencedContent;

            int count = order.Count;
            BasisGltfPlan plan = work.Plan;
            plan.NodeCount = count;
            plan.NodeSource = order.ToArray();
            plan.SourceToNode = sourceToNode;
            plan.NodeParent = new int[count];
            plan.NodeMesh = new int[count];
            plan.NodeSkin = new int[count];
            plan.NodeChildren = new int[count][];
            plan.NodeTrs = new float[count * 10];
            plan.SceneRoots = new int[roots.Length];
            for (int k = 0; k < roots.Length; k++) plan.SceneRoots[k] = sourceToNode[roots[k]];

            for (int c = 0; c < count; c++)
            {
                int source = plan.NodeSource[c];
                BasisGltfNode node = nodes[source];
                plan.NodeParent[c] = parent[source] >= 0 ? sourceToNode[parent[source]] : -1;
                plan.NodeMesh[c] = -1;
                plan.NodeSkin[c] = -1;
                int[] children = node.Children;
                if (children == null || children.Length == 0)
                {
                    plan.NodeChildren[c] = Array.Empty<int>();
                }
                else
                {
                    var mapped = new int[children.Length];
                    for (int j = 0; j < children.Length; j++) mapped[j] = sourceToNode[children[j]];
                    plan.NodeChildren[c] = mapped;
                }
                if (!TryCanonicalTransform(work, source, node, plan.NodeTrs, c * 10)) return false;
                if (node.Mesh >= doc.Meshes.Count) return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", source, "mesh") + " is out of range.");
                if (node.Skin >= doc.Skins.Count) return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", source, "skin") + " is out of range.");
                if (node.Skin >= 0 && node.Mesh < 0) return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", source) + " has a skin but no mesh.");
            }
            return true;
        }

        private static bool TryCanonicalTransform(BasisGltfWork work, int source, BasisGltfNode node, float[] trs, int o)
        {
            float tx = node.Tx, ty = node.Ty, tz = node.Tz;
            float rx = node.Rx, ry = node.Ry, rz = node.Rz, rw = node.Rw;
            float sx = node.Sx, sy = node.Sy, sz = node.Sz;
            if (node.HasMatrix)
            {
                if (node.HasT || node.HasR || node.HasS)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", source) + " has both a matrix and translation, rotation or scale.");
                }
                if (!BasisGltfTransformMath.TryDecompose(node.Matrix, out tx, out ty, out tz, out rx, out ry, out rz, out rw, out sx, out sy, out sz))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", source, "matrix")
                        + " is not a valid transform (projective, degenerate or non-finite).");
                }
            }
            if (!BasisGltfTransformMath.TryCanonicalizeRotation(ref rx, ref ry, ref rz, ref rw))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("nodes", source, "rotation") + " is a zero quaternion.");
            }
            double maxT = work.Limits.MaxNodeTranslation;
            if (!BasisGlbNumbers.Within(Math.Abs((double)tx), maxT) || !BasisGlbNumbers.Within(Math.Abs((double)ty), maxT)
                || !BasisGlbNumbers.Within(Math.Abs((double)tz), maxT))
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Index("nodes", source, "translation") + " exceeds ±"
                    + BasisGlbErrors.Meters(maxT) + ".");
            }
            double maxS = work.Limits.MaxNodeScale;
            if (!BasisGlbNumbers.Within(Math.Abs((double)sx), maxS) || !BasisGlbNumbers.Within(Math.Abs((double)sy), maxS)
                || !BasisGlbNumbers.Within(Math.Abs((double)sz), maxS))
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Index("nodes", source, "scale") + " exceeds ±"
                    + maxS.ToString("0.###", CultureInfo.InvariantCulture) + ".");
            }
            trs[o] = BasisGlbNumbers.PositiveZero(tx);
            trs[o + 1] = BasisGlbNumbers.PositiveZero(ty);
            trs[o + 2] = BasisGlbNumbers.PositiveZero(tz);
            trs[o + 3] = BasisGlbNumbers.PositiveZero(rx);
            trs[o + 4] = BasisGlbNumbers.PositiveZero(ry);
            trs[o + 5] = BasisGlbNumbers.PositiveZero(rz);
            trs[o + 6] = BasisGlbNumbers.PositiveZero(rw);
            trs[o + 7] = BasisGlbNumbers.PositiveZero(sx);
            trs[o + 8] = BasisGlbNumbers.PositiveZero(sy);
            trs[o + 9] = BasisGlbNumbers.PositiveZero(sz);
            return true;
        }

        // ---- references and tables ----------------------------------------------------------------------------------

        /// <summary>Per-build scratch for the references pass: source → canonical maps and role bookkeeping.</summary>
        private sealed class ReferenceState
        {
            public int[] MeshToCanon;
            public int[][] MeshRetained;
            public bool[] MeshHasBones;
            public int[] MeshSkin;
            public int[] SkinToCanon;
            public int[] MaterialToCanon;
            public int[] ImageToCanon;
            public bool[] TextureReferenced;
            public bool[] SamplerReferenced;
            public int[] AccessorToCanon;
            public BasisGltfAccessorRole[] AccessorRole;
            public Dictionary<long, int> InverseBindToCanon = new Dictionary<long, int>();
            public Dictionary<long, int> TextureKeyToCanon = new Dictionary<long, int>();
        }

        public static bool TryBuildReferences(BasisGltfWork work)
        {
            BasisGltfDocument doc = work.Document;
            BasisGltfPlan plan = work.Plan;
            BasisModelLimits limits = work.Limits;
            var state = new ReferenceState
            {
                MeshToCanon = Filled(doc.Meshes.Count),
                MeshRetained = new int[doc.Meshes.Count][],
                MeshHasBones = new bool[doc.Meshes.Count],
                MeshSkin = Filled(doc.Meshes.Count),
                SkinToCanon = Filled(doc.Skins.Count),
                MaterialToCanon = Filled(doc.Materials.Count),
                ImageToCanon = Filled(doc.Images.Count),
                TextureReferenced = new bool[doc.Textures.Count],
                SamplerReferenced = new bool[doc.Samplers.Count],
                AccessorToCanon = Filled(doc.Accessors.Count),
                AccessorRole = new BasisGltfAccessorRole[doc.Accessors.Count],
            };

            // Mesh retention: only triangle primitives survive; a mesh with none loses its node references.
            for (int n = 0; n < plan.NodeCount; n++)
            {
                int mesh = doc.Nodes[plan.NodeSource[n]].Mesh;
                if (mesh < 0) continue;
                if (state.MeshRetained[mesh] == null && !TryRetainPrimitives(work, state, mesh)) return false;
            }

            // Skinning: a node is skinned when it has a skin and its mesh keeps JOINTS_0 + WEIGHTS_0.
            for (int n = 0; n < plan.NodeCount; n++)
            {
                BasisGltfNode node = doc.Nodes[plan.NodeSource[n]];
                if (node.Mesh < 0) continue;
                bool retained = state.MeshRetained[node.Mesh].Length > 0;
                if (node.Skin < 0) continue;
                if (!retained || !state.MeshHasBones[node.Mesh])
                {
                    doc.Stripped |= BasisGlbStripped.UnusedSkins;
                    continue;
                }
                int current = state.MeshSkin[node.Mesh];
                if (current >= 0 && current != node.Skin)
                {
                    // glTFast writes the shared mesh.bindposes per skin, so the second skin would corrupt the first.
                    return work.Fail(BasisGlbErrorKind.Unsupported, BasisGlbErrors.Index("meshes", node.Mesh) + " is skinned by both "
                        + BasisGlbErrors.Index("skins", current) + " and " + BasisGlbErrors.Index("skins", node.Skin) + ".");
                }
                state.MeshSkin[node.Mesh] = node.Skin;
            }

            // Canonical meshes and skins: first reference by canonical nodes, in node order.
            for (int n = 0; n < plan.NodeCount; n++)
            {
                BasisGltfNode node = doc.Nodes[plan.NodeSource[n]];
                if (node.Mesh < 0 || state.MeshRetained[node.Mesh].Length == 0) continue;
                int canonMesh = state.MeshToCanon[node.Mesh];
                if (canonMesh < 0)
                {
                    canonMesh = plan.Meshes.Count;
                    if (canonMesh >= limits.MaxMeshes)
                    {
                        return work.Fail(BasisGlbErrorKind.OverLimit, "The model uses more than " + BasisGlbErrors.N(limits.MaxMeshes)
                            + " meshes. The maximum is " + BasisGlbErrors.N(limits.MaxMeshes) + ".");
                    }
                    state.MeshToCanon[node.Mesh] = canonMesh;
                    plan.Meshes.Add(new BasisGltfCanonMesh { Source = node.Mesh });
                }
                plan.NodeMesh[n] = canonMesh;
                if (node.Skin >= 0 && state.MeshSkin[node.Mesh] == node.Skin)
                {
                    int canonSkin = state.SkinToCanon[node.Skin];
                    if (canonSkin < 0)
                    {
                        if (!TryAddSkin(work, state, node.Skin, out canonSkin)) return false;
                    }
                    plan.NodeSkin[n] = canonSkin;
                    plan.Meshes[canonMesh].Skin = canonSkin;
                }
            }
            if (plan.Meshes.Count == 0) return work.Fail(BasisGlbErrorKind.Malformed, "The model has no triangles to display.");

            // Canonical primitives, accessors (indices, POSITION, NORMAL, TANGENT, TEXCOORD_0/1, COLOR_0, JOINTS_0,
            // WEIGHTS_0) and materials, in canonical mesh → primitive order.
            for (int m = 0; m < plan.Meshes.Count; m++)
            {
                BasisGltfCanonMesh canonMesh = plan.Meshes[m];
                int[] retained = state.MeshRetained[canonMesh.Source];
                for (int r = 0; r < retained.Length; r++)
                {
                    if (!TryAddPrimitive(work, state, m, canonMesh, retained[r])) return false;
                }
            }
            for (int s = 0; s < plan.Skins.Count; s++)
            {
                if (!TryAddInverseBind(work, state, plan.Skins[s])) return false;
            }

            // Materials, then textures in material slot order (images and samplers by first texture).
            for (int m = 0; m < plan.Materials.Count; m++)
            {
                if (!TryBuildMaterial(work, state, plan.Materials[m])) return false;
            }

            if (!CheckCount(work, "material", plan.Materials.Count, limits.MaxMaterials)
                || !CheckCount(work, "texture", plan.Textures.Count, limits.MaxTextures)
                || !CheckCount(work, "image", plan.Images.Count, limits.MaxImages)
                || !CheckCount(work, "sampler", plan.Samplers.Count, limits.MaxSamplers)
                || !CheckCount(work, "accessor", plan.Accessors.Count, limits.MaxAccessors)
                || !CheckCount(work, "skin", plan.Skins.Count, limits.MaxSkins))
            {
                return false;
            }
            int primitives = 0;
            for (int m = 0; m < plan.Meshes.Count; m++) primitives += plan.Meshes[m].Primitives.Count;
            if (!CheckCount(work, "primitive", primitives, limits.MaxPrimitives)) return false;

            if (plan.Meshes.Count < doc.Meshes.Count || plan.Skins.Count < doc.Skins.Count
                || plan.Materials.Count < doc.Materials.Count || plan.Images.Count < doc.Images.Count
                || plan.Accessors.Count < doc.Accessors.Count || !AllTrue(state.TextureReferenced) || !AllTrue(state.SamplerReferenced))
            {
                doc.Stripped |= BasisGlbStripped.UnreferencedContent;
            }
            return true;
        }

        private static bool CheckCount(BasisGltfWork work, string noun, int count, int maximum)
        {
            if (count <= maximum) return true;
            return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("The " + noun + " count", count, maximum));
        }

        private static bool TryRetainPrimitives(BasisGltfWork work, ReferenceState state, int mesh)
        {
            List<BasisGltfPrimitive> primitives = work.Document.Meshes[mesh].Primitives;
            int kept = 0;
            for (int p = 0; p < primitives.Count; p++)
            {
                if (primitives[p].Mode == 4) kept++;
            }
            if (kept < primitives.Count) work.Document.Stripped |= BasisGlbStripped.NonTrianglePrimitives;
            var retained = new int[kept];
            kept = 0;
            bool bones = false;
            for (int p = 0; p < primitives.Count; p++)
            {
                BasisGltfPrimitive primitive = primitives[p];
                if (primitive.Mode != 4) continue;
                retained[kept++] = p;
                if (primitive.Position < 0)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Primitive(mesh, p) + " has no POSITION.");
                }
                if ((primitive.Joints0 >= 0) != (primitive.Weights0 >= 0))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Primitive(mesh, p) + " has JOINTS_0 without WEIGHTS_0, or the reverse.");
                }
                if (primitive.Joints0 >= 0) bones = true;
            }
            state.MeshRetained[mesh] = retained;
            state.MeshHasBones[mesh] = bones;
            return true;
        }

        private static bool TryAddSkin(BasisGltfWork work, ReferenceState state, int source, out int canonical)
        {
            canonical = -1;
            BasisGltfDocument doc = work.Document;
            BasisGltfPlan plan = work.Plan;
            BasisGltfSkin skin = doc.Skins[source];
            int[] joints = skin.Joints;
            if (joints == null || joints.Length == 0)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("skins", source, "joints") + " is empty.");
            }
            if (joints.Length > work.Limits.MaxJointsPerSkin)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit(BasisGlbErrors.Index("skins", source) + " joint count",
                    joints.Length, work.Limits.MaxJointsPerSkin));
            }
            var canonJoints = new int[joints.Length];
            var seen = new HashSet<int>();
            for (int j = 0; j < joints.Length; j++)
            {
                int node = joints[j];
                if (node >= doc.Nodes.Count)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("skins", source, "joints") + "[" + Num(j) + "] is out of range.");
                }
                if (!seen.Add(node))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("skins", source, "joints") + " lists "
                        + BasisGlbErrors.Index("nodes", node) + " twice.");
                }
                int canonNode = plan.SourceToNode[node];
                if (canonNode < 0)
                {
                    // glTFast would throw KeyNotFound mid-instantiation (bones[j] = m_Nodes[jointIndex]).
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("skins", source, "joints") + "[" + Num(j)
                        + "] is not part of the displayed scene.");
                }
                canonJoints[j] = canonNode;
            }
            int skeleton = -1;
            if (skin.Skeleton >= 0 && skin.Skeleton < doc.Nodes.Count && plan.SourceToNode[skin.Skeleton] >= 0)
            {
                skeleton = plan.SourceToNode[skin.Skeleton];
                for (int j = 0; j < canonJoints.Length && skeleton >= 0; j++)
                {
                    if (!IsAncestorOrSelf(plan, skeleton, canonJoints[j], work.Limits.MaxNodeDepth)) skeleton = -1;
                }
            }
            if (!TryCheckAccessorRow(work, skin.InverseBindMatrices >= 0 ? skin.InverseBindMatrices : -1, BasisGltfAccessorRole.InverseBind, Row.InverseBind,
                    BasisGlbErrors.Index("skins", source, "inverseBindMatrices")))
            {
                return false;
            }
            if (skin.InverseBindMatrices >= 0 && doc.Accessors[skin.InverseBindMatrices].Count < joints.Length)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("skins", source, "inverseBindMatrices") + " has "
                    + BasisGlbErrors.N(doc.Accessors[skin.InverseBindMatrices].Count) + " matrices but the skin has "
                    + BasisGlbErrors.N(joints.Length) + " joints.");
            }
            canonical = plan.Skins.Count;
            state.SkinToCanon[source] = canonical;
            plan.Skins.Add(new BasisGltfCanonSkin { Source = source, Joints = canonJoints, Skeleton = skeleton });
            plan.Joints += canonJoints.Length;
            return true;
        }

        private static bool IsAncestorOrSelf(BasisGltfPlan plan, int ancestor, int node, int maxDepth)
        {
            for (int steps = 0; node >= 0 && steps <= maxDepth; steps++)
            {
                if (node == ancestor) return true;
                node = plan.NodeParent[node];
            }
            return false;
        }

        private static bool TryAddPrimitive(BasisGltfWork work, ReferenceState state, int canonMeshIndex, BasisGltfCanonMesh mesh, int sourcePrimitive)
        {
            BasisGltfDocument doc = work.Document;
            BasisGltfPlan plan = work.Plan;
            int meshIndex = mesh.Source;
            BasisGltfPrimitive primitive = doc.Meshes[meshIndex].Primitives[sourcePrimitive];
            string path = BasisGlbErrors.Primitive(meshIndex, sourcePrimitive);
            bool skinned = mesh.Skin >= 0;

            if (!TryCheckAccessorRow(work, primitive.Position, BasisGltfAccessorRole.Vertex, Row.Position, path + " POSITION")) return false;
            int vertexCount = doc.Accessors[primitive.Position].Count;
            int normal = primitive.Normal, tangent = primitive.Tangent, uv0 = primitive.TexCoord0, uv1 = primitive.TexCoord1;
            int color = primitive.Color0, joints = primitive.Joints0, weights = primitive.Weights0;
            if (uv1 >= 0 && uv0 < 0)
            {
                uv1 = -1;
                doc.Stripped |= BasisGlbStripped.ExtraUvSets;
            }
            if (joints >= 0 && !skinned)
            {
                joints = -1;
                weights = -1;
                doc.Stripped |= BasisGlbStripped.ExtraVertexStreams;
            }
            if (!TryCheckAttribute(work, normal, Row.Normal, path, "NORMAL", vertexCount)
                || !TryCheckAttribute(work, tangent, Row.Tangent, path, "TANGENT", vertexCount)
                || !TryCheckAttribute(work, uv0, Row.TexCoord, path, "TEXCOORD_0", vertexCount)
                || !TryCheckAttribute(work, uv1, Row.TexCoord, path, "TEXCOORD_1", vertexCount)
                || !TryCheckAttribute(work, color, Row.Color, path, "COLOR_0", vertexCount)
                || !TryCheckAttribute(work, joints, Row.Joints, path, "JOINTS_0", vertexCount)
                || !TryCheckAttribute(work, weights, Row.Weights, path, "WEIGHTS_0", vertexCount))
            {
                return false;
            }
            int indexCount;
            if (primitive.Indices >= 0)
            {
                if (!TryCheckAccessorRow(work, primitive.Indices, BasisGltfAccessorRole.Index, Row.Indices, path + " indices")) return false;
                indexCount = doc.Accessors[primitive.Indices].Count;
                if (indexCount % 3 != 0)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, path + " has " + BasisGlbErrors.N(indexCount)
                        + " indices; triangles need a multiple of 3.");
                }
            }
            else
            {
                indexCount = vertexCount;
                if (vertexCount % 3 != 0)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, path + " has " + BasisGlbErrors.N(vertexCount)
                        + " vertices and no indices; triangles need a multiple of 3.");
                }
            }
            if (primitive.Material >= doc.Materials.Count)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + ".material is out of range.");
            }

            var canon = new BasisGltfCanonPrimitive
            {
                SourceMesh = meshIndex,
                SourcePrimitive = sourcePrimitive,
                VertexCount = vertexCount,
                IndexCount = indexCount,
                Indices = primitive.Indices >= 0 ? AddAccessor(state, plan, primitive.Indices, BasisGltfAccessorRole.Index, canonMeshIndex, sourcePrimitive) : -1,
                Position = AddAccessor(state, plan, primitive.Position, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive),
            };
            canon.Normal = normal >= 0 ? AddAccessor(state, plan, normal, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            canon.Tangent = tangent >= 0 ? AddAccessor(state, plan, tangent, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            canon.TexCoord0 = uv0 >= 0 ? AddAccessor(state, plan, uv0, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            canon.TexCoord1 = uv1 >= 0 ? AddAccessor(state, plan, uv1, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            canon.Color0 = color >= 0 ? AddAccessor(state, plan, color, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            canon.Joints0 = joints >= 0 ? AddAccessor(state, plan, joints, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            canon.Weights0 = weights >= 0 ? AddAccessor(state, plan, weights, BasisGltfAccessorRole.Vertex, canonMeshIndex, sourcePrimitive) : -1;
            if (canon.Indices == -2 || canon.Position == -2 || canon.Normal == -2 || canon.Tangent == -2 || canon.TexCoord0 == -2
                || canon.TexCoord1 == -2 || canon.Color0 == -2 || canon.Joints0 == -2 || canon.Weights0 == -2)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + " uses one accessor both for indices and for vertex data.");
            }
            plan.Accessors[canon.Position].IsPosition = true;
            if (canon.Joints0 >= 0) plan.Accessors[canon.Joints0].IsJoints = true;
            if (canon.Weights0 >= 0) plan.Accessors[canon.Weights0].IsWeights = true;

            canon.Material = -1;
            if (primitive.Material >= 0)
            {
                int canonMaterial = state.MaterialToCanon[primitive.Material];
                if (canonMaterial < 0)
                {
                    canonMaterial = plan.Materials.Count;
                    state.MaterialToCanon[primitive.Material] = canonMaterial;
                    plan.Materials.Add(new BasisGltfCanonMaterial { Source = primitive.Material });
                }
                canon.Material = canonMaterial;
            }
            mesh.Primitives.Add(canon);
            return true;
        }

        /// <summary>Returns the canonical index, or -2 when the accessor already serves another role class.</summary>
        private static int AddAccessor(ReferenceState state, BasisGltfPlan plan, int source, BasisGltfAccessorRole role, int mesh, int primitive)
        {
            BasisGltfAccessorRole existing = state.AccessorRole[source];
            if (existing != BasisGltfAccessorRole.None && existing != role) return -2;
            int canonical = state.AccessorToCanon[source];
            if (canonical >= 0) return canonical;
            canonical = plan.Accessors.Count;
            state.AccessorToCanon[source] = canonical;
            state.AccessorRole[source] = role;
            plan.Accessors.Add(new BasisGltfCanonAccessor { Source = source, Role = role, FirstMesh = mesh, FirstPrimitive = primitive });
            return canonical;
        }

        private static bool TryAddInverseBind(BasisGltfWork work, ReferenceState state, BasisGltfCanonSkin skin)
        {
            int source = work.Document.Skins[skin.Source].InverseBindMatrices;
            if (source < 0) return true;
            BasisGltfAccessorRole existing = state.AccessorRole[source];
            if (existing != BasisGltfAccessorRole.None && existing != BasisGltfAccessorRole.InverseBind)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("accessors", source) + " is used both as inverse bind matrices and as mesh data.");
            }
            state.AccessorRole[source] = BasisGltfAccessorRole.InverseBind;
            long key = ((long)source << 32) | (uint)skin.Joints.Length;
            if (!state.InverseBindToCanon.TryGetValue(key, out int canonical))
            {
                canonical = work.Plan.Accessors.Count;
                state.InverseBindToCanon.Add(key, canonical);
                work.Plan.Accessors.Add(new BasisGltfCanonAccessor
                {
                    Source = source,
                    Role = BasisGltfAccessorRole.InverseBind,
                    Count = skin.Joints.Length,
                });
            }
            skin.InverseBindMatrices = canonical;
            return true;
        }

        // ---- accessor typing --------------------------------------------------------------------------------------

        private enum Row : byte { Position, Normal, Tangent, TexCoord, Color, Joints, Weights, Indices, InverseBind }

        private static bool TryCheckAttribute(BasisGltfWork work, int accessor, Row row, string path, string attribute, int vertexCount)
        {
            if (accessor < 0) return true;
            if (!TryCheckAccessorRow(work, accessor, BasisGltfAccessorRole.Vertex, row, path + " " + attribute)) return false;
            int count = work.Document.Accessors[accessor].Count;
            if (count != vertexCount)
            {
                // A larger count is a release-build heap overwrite in glTFast's vertex jobs.
                return work.Fail(BasisGlbErrorKind.Malformed, path + " " + attribute + " has " + BasisGlbErrors.N(count)
                    + " elements but POSITION has " + BasisGlbErrors.N(vertexCount) + ".");
            }
            return true;
        }

        /// <summary>
        /// General accessor rules plus the attribute table (a subset of what glTFast decodes; any other combination
        /// gives glTFast a null job and an exception). A quantised type sets <see cref="BasisGltfPlan.UsesQuantization"/>.
        /// </summary>
        private static bool TryCheckAccessorRow(BasisGltfWork work, int index, BasisGltfAccessorRole role, Row row, string user)
        {
            if (index < 0) return true;
            BasisGltfDocument doc = work.Document;
            if (index >= doc.Accessors.Count) return work.Fail(BasisGlbErrorKind.Malformed, user + " refers to an accessor that is out of range.");
            BasisGltfAccessor accessor = doc.Accessors[index];
            string path = BasisGlbErrors.Index("accessors", index);
            if (accessor.HasSparse) return work.Fail(BasisGlbErrorKind.Unsupported, path + " uses sparse storage, which is not supported.");
            if (accessor.BufferView < 0) return work.Fail(BasisGlbErrorKind.Malformed, path + " has no bufferView.");
            if (accessor.BufferView >= doc.BufferViews.Count) return work.Fail(BasisGlbErrorKind.Malformed, path + ".bufferView is out of range.");
            if (accessor.Count < 1) return work.Fail(BasisGlbErrorKind.Malformed, path + ".count must be at least 1.");
            if (accessor.Count > work.Limits.MaxAccessorElements)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit(path + " element count", accessor.Count, work.Limits.MaxAccessorElements));
            }
            if (accessor.Type == BasisGltfType.None) return work.Fail(BasisGlbErrorKind.Malformed, path + ".type is not a glTF accessor type.");
            int size = BasisGltfComponent.Size(accessor.ComponentType);
            if (size == 0) return work.Fail(BasisGlbErrorKind.Malformed, path + ".componentType is not supported.");
            if (accessor.Normalized && (accessor.ComponentType == BasisGltfComponent.Float || accessor.ComponentType == BasisGltfComponent.UnsignedInt))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + " is normalized but its component type cannot be.");
            }
            bool quantized;
            if (!MatchesRow(row, accessor.Type, accessor.ComponentType, accessor.Normalized, out quantized))
            {
                return work.Fail(BasisGlbErrorKind.Unsupported, user + " uses " + BasisGltfType.Name(accessor.Type) + " "
                    + ComponentName(accessor.ComponentType) + (accessor.Normalized ? " normalized" : string.Empty)
                    + " data (" + path + "), which glTF and Basis do not support for that use.");
            }
            if (quantized) work.Plan.UsesQuantization = true;
            return true;
        }

        private static bool MatchesRow(Row row, byte type, int component, bool normalized, out bool quantized)
        {
            quantized = false;
            const int B = BasisGltfComponent.Byte, UB = BasisGltfComponent.UnsignedByte, S = BasisGltfComponent.Short;
            const int US = BasisGltfComponent.UnsignedShort, UI = BasisGltfComponent.UnsignedInt, F = BasisGltfComponent.Float;
            switch (row)
            {
                case Row.Position:
                    if (type != BasisGltfType.Vec3) return false;
                    if (component == F) return !normalized;
                    quantized = component == B || component == UB || component == S || component == US;
                    return quantized;
                case Row.Normal:
                    if (type != BasisGltfType.Vec3) return false;
                    if (component == F) return !normalized;
                    quantized = (component == B || component == S) && normalized;
                    return quantized;
                case Row.Tangent:
                    if (type != BasisGltfType.Vec4) return false;
                    if (component == F) return !normalized;
                    quantized = (component == B || component == S) && normalized;
                    return quantized;
                case Row.TexCoord:
                    if (type != BasisGltfType.Vec2) return false;
                    if (component == F) return !normalized;
                    if ((component == UB || component == US) && normalized) return true;
                    quantized = component == B || component == S || ((component == UB || component == US) && !normalized);
                    return quantized;
                case Row.Color:
                    if (type != BasisGltfType.Vec3 && type != BasisGltfType.Vec4) return false;
                    return (component == F && !normalized) || ((component == UB || component == US) && normalized);
                case Row.Joints:
                    return type == BasisGltfType.Vec4 && (component == UB || component == US) && !normalized;
                case Row.Weights:
                    if (type != BasisGltfType.Vec4) return false;
                    return (component == F && !normalized) || ((component == UB || component == US) && normalized);
                case Row.Indices:
                    return type == BasisGltfType.Scalar && (component == UB || component == US || component == UI) && !normalized;
                case Row.InverseBind:
                    return type == BasisGltfType.Mat4 && component == F && !normalized;
                default:
                    return false;
            }
        }

        private static string ComponentName(int component)
        {
            switch (component)
            {
                case BasisGltfComponent.Byte: return "byte";
                case BasisGltfComponent.UnsignedByte: return "unsigned byte";
                case BasisGltfComponent.Short: return "short";
                case BasisGltfComponent.UnsignedShort: return "unsigned short";
                case BasisGltfComponent.UnsignedInt: return "unsigned int";
                case BasisGltfComponent.Float: return "float";
                default: return "unknown";
            }
        }

        // ---- materials and textures -------------------------------------------------------------------------------

        private static bool TryBuildMaterial(BasisGltfWork work, ReferenceState state, BasisGltfCanonMaterial canon)
        {
            BasisGltfMaterial source = work.Document.Materials[canon.Source];
            string path = BasisGlbErrors.Index("materials", canon.Source);
            if (source.AlphaMode == BasisGltfJsonBinder.AlphaModeInvalid)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + ".alphaMode must be OPAQUE, MASK or BLEND.");
            }
            if (source.HasEmissiveStrength && !(source.EmissiveStrength >= 0f))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + " KHR_materials_emissive_strength.emissiveStrength must be at least 0.");
            }
            for (int i = 0; i < 4; i++) canon.BaseColor[i] = Clamp01(source.BaseColor[i]);
            for (int i = 0; i < 3; i++) canon.Emissive[i] = Clamp01(source.Emissive[i]);
            canon.Metallic = Clamp01(source.Metallic);
            canon.Roughness = Clamp01(source.Roughness);
            canon.AlphaCutoff = Clamp01(source.AlphaCutoff);
            canon.AlphaMode = source.AlphaMode;
            canon.DoubleSided = source.DoubleSided;
            canon.Unlit = source.Unlit;
            if (source.Unlit) work.Plan.UsesUnlit = true;
            return TryResolveTextureRef(work, state, path, "pbrMetallicRoughness.baseColorTexture", source.BaseColorTex, false, out canon.BaseColorTex)
                && TryResolveTextureRef(work, state, path, "pbrMetallicRoughness.metallicRoughnessTexture", source.MetallicRoughnessTex, false, out canon.MetallicRoughnessTex)
                && TryResolveTextureRef(work, state, path, "normalTexture", source.NormalTex, false, out canon.NormalTex)
                && TryResolveTextureRef(work, state, path, "occlusionTexture", source.OcclusionTex, true, out canon.OcclusionTex)
                && TryResolveTextureRef(work, state, path, "emissiveTexture", source.EmissiveTex, false, out canon.EmissiveTex);
        }

        private static float Clamp01(float value)
        {
            return BasisGlbNumbers.PositiveZero(value < 0f ? 0f : value > 1f ? 1f : value);
        }

        private static bool TryResolveTextureRef(BasisGltfWork work, ReferenceState state, string materialPath, string slot,
            in BasisGltfTextureRef source, bool clampFactor, out BasisGltfCanonTextureRef canon)
        {
            canon = new BasisGltfCanonTextureRef { Texture = -1, ScaleOrStrength = 1f, ScaleU = 1f, ScaleV = 1f };
            if (!source.Present) return true;
            BasisGltfDocument doc = work.Document;
            BasisGltfPlan plan = work.Plan;
            if (source.Index < 0 || source.Index >= doc.Textures.Count)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, materialPath + "." + slot + ".index is out of range.");
            }
            if (source.TexCoord > 1 || (source.HasTransform && source.TransformTexCoord > 1))
            {
                int used = source.TexCoord > 1 ? source.TexCoord : source.TransformTexCoord;
                return work.Fail(BasisGlbErrorKind.Unsupported, materialPath + "." + slot + " uses TEXCOORD_" + Num(used)
                    + "; only TEXCOORD_0 and TEXCOORD_1 are supported.");
            }
            BasisGltfTexture texture = doc.Textures[source.Index];
            state.TextureReferenced[source.Index] = true;
            if (texture.Source < 0)
            {
                // Its only image was in a stripped extension (basisu, webp…): the texture and the reference go.
                doc.Stripped |= BasisGlbStripped.UnreferencedContent;
                return true;
            }
            string texturePath = BasisGlbErrors.Index("textures", source.Index);
            if (texture.Source >= doc.Images.Count) return work.Fail(BasisGlbErrorKind.Malformed, texturePath + ".source is out of range.");
            if (texture.Sampler >= doc.Samplers.Count) return work.Fail(BasisGlbErrorKind.Malformed, texturePath + ".sampler is out of range.");

            int samplerKey = -1;
            BasisGltfCanonSampler samplerValue = default;
            if (texture.Sampler >= 0)
            {
                state.SamplerReferenced[texture.Sampler] = true;
                BasisGltfSampler sampler = doc.Samplers[texture.Sampler];
                string samplerPath = BasisGlbErrors.Index("samplers", texture.Sampler);
                if (sampler.MagFilter != 0 && sampler.MagFilter != 9728 && sampler.MagFilter != 9729)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, samplerPath + ".magFilter is not a valid filter.");
                }
                if (sampler.MinFilter != 0 && sampler.MinFilter != 9728 && sampler.MinFilter != 9729
                    && (sampler.MinFilter < 9984 || sampler.MinFilter > 9987))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, samplerPath + ".minFilter is not a valid filter.");
                }
                if (!IsWrap(sampler.WrapS) || !IsWrap(sampler.WrapT))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, samplerPath + " has an invalid wrap mode.");
                }
                bool isDefault = sampler.MagFilter == 0 && sampler.MinFilter == 0 && sampler.WrapS == 10497 && sampler.WrapT == 10497;
                if (!isDefault)
                {
                    samplerValue = new BasisGltfCanonSampler { Mag = sampler.MagFilter, Min = sampler.MinFilter, WrapS = sampler.WrapS, WrapT = sampler.WrapT };
                    samplerKey = SamplerValueKey(samplerValue);
                }
            }

            long textureKey = ((long)texture.Source << 32) | (uint)samplerKey;
            if (!state.TextureKeyToCanon.TryGetValue(textureKey, out int canonTexture))
            {
                int canonImage = state.ImageToCanon[texture.Source];
                if (canonImage < 0)
                {
                    canonImage = plan.Images.Count;
                    state.ImageToCanon[texture.Source] = canonImage;
                    plan.Images.Add(new BasisGltfCanonImage { Source = texture.Source });
                }
                int canonSampler = -1;
                if (samplerKey >= 0)
                {
                    for (int s = 0; s < plan.Samplers.Count; s++)
                    {
                        if (SamplerValueKey(plan.Samplers[s]) == samplerKey)
                        {
                            canonSampler = s;
                            break;
                        }
                    }
                    if (canonSampler < 0)
                    {
                        canonSampler = plan.Samplers.Count;
                        plan.Samplers.Add(samplerValue);
                    }
                }
                canonTexture = plan.Textures.Count;
                state.TextureKeyToCanon.Add(textureKey, canonTexture);
                plan.Textures.Add(new BasisGltfCanonTexture { SourceImage = texture.Source, Image = canonImage, Sampler = canonSampler, SamplerKey = samplerKey });
            }

            canon.Texture = canonTexture;
            canon.TexCoord = source.HasTransform && source.TransformTexCoord >= 0 ? source.TransformTexCoord : source.TexCoord;
            canon.ScaleOrStrength = clampFactor ? Clamp01(source.ScaleOrStrength) : source.ScaleOrStrength;
            bool identity = source.OffsetU == 0f && source.OffsetV == 0f && source.Rotation == 0f && source.ScaleU == 1f && source.ScaleV == 1f;
            if (source.HasTransform && !identity)
            {
                canon.HasTransform = true;
                canon.OffsetU = source.OffsetU;
                canon.OffsetV = source.OffsetV;
                canon.Rotation = source.Rotation;
                canon.ScaleU = source.ScaleU;
                canon.ScaleV = source.ScaleV;
                plan.UsesTextureTransform = true;
            }
            return true;
        }

        private static bool IsWrap(int wrap)
        {
            return wrap == 33071 || wrap == 33648 || wrap == 10497;
        }

        /// <summary>Packs a sampler into 31 bits: mag (2 bits), min (3), wrapS (2), wrapT (2).</summary>
        private static int SamplerValueKey(in BasisGltfCanonSampler sampler)
        {
            int mag = sampler.Mag == 0 ? 0 : sampler.Mag - 9727;                      // 0, 1, 2
            int min = sampler.Min == 0 ? 0 : sampler.Min < 9984 ? sampler.Min - 9727 : sampler.Min - 9981; // 0..6
            return (mag << 7) | (min << 4) | (WrapCode(sampler.WrapS) << 2) | WrapCode(sampler.WrapT);
        }

        private static int WrapCode(int wrap)
        {
            return wrap == 33071 ? 1 : wrap == 33648 ? 2 : 0;
        }

        // ---- buffers ------------------------------------------------------------------------------------------------

        public static bool TryResolveBuffers(BasisGltfWork work)
        {
            BasisGltfDocument doc = work.Document;
            BasisGltfPlan plan = work.Plan;
            if (!work.Sender)
            {
                // Wire input: GLB only, no uri anywhere, retained or not.
                for (int b = 0; b < doc.Buffers.Count; b++)
                {
                    if (doc.Buffers[b].HasUri) return work.Fail(BasisGlbErrorKind.Unsupported, BasisGlbErrors.Index("buffers", b) + " has a uri.");
                }
                for (int i = 0; i < doc.Images.Count; i++)
                {
                    if (doc.Images[i].HasUri) return work.Fail(BasisGlbErrorKind.Unsupported, BasisGlbErrors.Index("images", i) + " has a uri.");
                }
            }
            if (work.Chunks.HasBin && doc.Buffers.Count > 0 && doc.Buffers[0].HasUri)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, "The GLB has a BIN chunk but buffers[0] has a uri.");
            }
            work.BufferData = new byte[doc.Buffers.Count][];
            work.BufferBase = new long[doc.Buffers.Count];
            var viewChecked = new bool[doc.BufferViews.Count];
            int viewsUsed = 0;

            for (int a = 0; a < plan.Accessors.Count; a++)
            {
                BasisGltfCanonAccessor canon = plan.Accessors[a];
                BasisGltfAccessor accessor = doc.Accessors[canon.Source];
                int viewIndex = accessor.BufferView;
                if (!viewChecked[viewIndex])
                {
                    if (!TryCheckView(work, viewIndex)) return false;
                    viewChecked[viewIndex] = true;
                    viewsUsed++;
                }
                BasisGltfBufferView view = doc.BufferViews[viewIndex];
                int elementSize = BasisGltfType.Components(accessor.Type) * BasisGltfComponent.Size(accessor.ComponentType);
                string path = BasisGlbErrors.Index("accessors", canon.Source);
                if (canon.Role == BasisGltfAccessorRole.Index && view.ByteStride != -1)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("bufferViews", viewIndex) + " holds indices and must not have a byteStride.");
                }
                int stride = view.ByteStride > 0 ? view.ByteStride : elementSize;
                if (stride < elementSize)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, path + " elements are " + BasisGlbErrors.N(elementSize) + " bytes but "
                        + BasisGlbErrors.Index("bufferViews", viewIndex, "byteStride") + " is " + BasisGlbErrors.N(stride) + ".");
                }
                long needed = accessor.ByteOffset + (long)stride * (accessor.Count - 1) + elementSize;
                if (needed > view.ByteLength)
                {
                    // Closes the release-build out-of-bounds reads in glTFast's GetSubArray and MemCopy jobs.
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.At(path, "reads past the end of "
                        + BasisGlbErrors.Index("bufferViews", viewIndex) + ": it needs " + BasisGlbErrors.N(needed - accessor.ByteOffset)
                        + " bytes from offset " + BasisGlbErrors.N(accessor.ByteOffset) + ", but the view is "
                        + BasisGlbErrors.N(view.ByteLength) + " bytes."));
                }
                canon.ComponentType = accessor.ComponentType;
                canon.Type = accessor.Type;
                canon.Normalized = accessor.Normalized;
                if (canon.Role != BasisGltfAccessorRole.InverseBind) canon.Count = accessor.Count;
                canon.ElementSize = elementSize;
                canon.SourceStride = stride;
                canon.Data = work.BufferData[view.Buffer];
                canon.DataOffset = work.BufferBase[view.Buffer] + view.ByteOffset + accessor.ByteOffset;
            }

            for (int i = 0; i < plan.Images.Count; i++)
            {
                if (!TryResolveImage(work, plan.Images[i], viewChecked, ref viewsUsed)) return false;
            }

            int buffersUsed = 0;
            for (int b = 0; b < work.BufferData.Length; b++)
            {
                if (work.BufferData[b] != null) buffersUsed++;
            }
            if (viewsUsed < doc.BufferViews.Count || buffersUsed < doc.Buffers.Count) doc.Stripped |= BasisGlbStripped.UnreferencedContent;
            return true;
        }

        private static bool TryCheckView(BasisGltfWork work, int viewIndex)
        {
            BasisGltfDocument doc = work.Document;
            BasisGltfBufferView view = doc.BufferViews[viewIndex];
            string path = BasisGlbErrors.Index("bufferViews", viewIndex);
            if (view.Buffer < 0 || view.Buffer >= doc.Buffers.Count) return work.Fail(BasisGlbErrorKind.Malformed, path + ".buffer is out of range.");
            if (view.ByteLength < 1) return work.Fail(BasisGlbErrorKind.Malformed, path + ".byteLength must be at least 1.");
            if (view.ByteStride != -1 && (view.ByteStride < 4 || view.ByteStride > 252 || view.ByteStride % 4 != 0))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + ".byteStride must be a multiple of 4 from 4 to 252.");
            }
            if (work.BufferData[view.Buffer] == null && !TryResolveBuffer(work, view.Buffer)) return false;
            long bufferLength = doc.Buffers[view.Buffer].ByteLength;
            if (view.ByteOffset + view.ByteLength > bufferLength)
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + " ends at byte " + BasisGlbErrors.N(view.ByteOffset + view.ByteLength)
                    + ", past the end of " + BasisGlbErrors.Index("buffers", view.Buffer) + " (" + BasisGlbErrors.N(bufferLength) + " bytes).");
            }
            return true;
        }

        private static bool TryResolveBuffer(BasisGltfWork work, int index)
        {
            BasisGltfBuffer buffer = work.Document.Buffers[index];
            string path = BasisGlbErrors.Index("buffers", index);
            if (buffer.ByteLength < 1) return work.Fail(BasisGlbErrorKind.Malformed, path + ".byteLength must be at least 1.");
            if (!buffer.HasUri)
            {
                if (work.Format != BasisModelSourceFormat.Glb || index != 0 || !work.Chunks.HasBin)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, work.Format == BasisModelSourceFormat.Glb && index == 0
                        ? "buffers[0] has no uri and the GLB has no BIN chunk."
                        : path + " has no uri; only buffers[0] of a GLB may use the BIN chunk.");
                }
                long binLength = work.Chunks.BinLength;
                if (binLength < buffer.ByteLength || binLength > buffer.ByteLength + 3)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, "BIN chunk is " + BasisGlbErrors.N(binLength) + " bytes but buffers[0] declares "
                        + BasisGlbErrors.N(buffer.ByteLength) + ".");
                }
                work.BufferData[index] = work.Source;
                work.BufferBase[index] = work.Chunks.BinStart;
                return true;
            }
            if (!TryDecodeUri(work, path, buffer.UriStart, buffer.UriLength, buffer.UriHasEscapes, false, buffer.ByteLength,
                    buffer.ByteLength + 3, out BasisDataUriMedia _, out byte[] decoded))
            {
                return false;
            }
            work.BufferData[index] = decoded;
            work.BufferBase[index] = 0;
            return true;
        }

        /// <summary>Sender only (receivers reject every uri first). Decoded size is checked against the budget before allocating.</summary>
        private static bool TryDecodeUri(BasisGltfWork work, string path, int start, int length, bool hasEscapes, bool forImage,
            long minDecoded, long maxDecoded, out BasisDataUriMedia media, out byte[] decoded)
        {
            media = BasisDataUriMedia.None;
            decoded = null;
            byte[] json = work.Document.Json;
            byte[] uri = json;
            int uriStart = start;
            int uriLength = length;
            if (hasEscapes)
            {
                // In practice "\/"; the unescaped form is never longer than the raw one.
                uri = new byte[length];
                uriLength = BasisJsonReader.DecodeEscaped(json, start, length, uri);
                uriStart = 0;
                if (uriLength < 0) return work.Fail(BasisGlbErrorKind.Malformed, path + ".uri could not be decoded.");
            }
            if (!BasisGlbContainer.IsDataUri(uri, uriStart, uriLength))
            {
                string shown = System.Text.Encoding.UTF8.GetString(uri, uriStart, Math.Min(uriLength, MaxUriCharsShown * 4));
                return work.Fail(BasisGlbErrorKind.Unsupported, path + " references the external file \""
                    + BasisGlbErrors.ForDisplay(shown, MaxUriCharsShown)
                    + "\". Only .glb files and .gltf files with embedded data: URIs are supported.");
            }
            if (!BasisGlbContainer.TryParseDataUri(uri, uriStart, uriLength, forImage, out media, out int payloadStart, out int payloadLength, out string error))
            {
                return work.Fail(BasisGlbErrorKind.Unsupported, path + " " + error);
            }
            if (!BasisGlbContainer.TryGetBase64DecodedLength(uri, payloadStart, payloadLength, out int dataChars, out long decodedLength))
            {
                return work.Fail(BasisGlbErrorKind.Malformed, path + " has malformed base64.");
            }
            // Length math only: nothing is allocated for a payload that cannot be what the file declares.
            if (decodedLength < minDecoded || decodedLength > maxDecoded)
            {
                if (!forImage)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, path + " decodes to " + BasisGlbErrors.N(decodedLength) + " bytes but declares "
                        + BasisGlbErrors.N(minDecoded) + ".");
                }
                return decodedLength < minDecoded
                    ? work.Fail(BasisGlbErrorKind.Malformed, path + " has an empty data: URI.")
                    : work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes(path, decodedLength, maxDecoded));
            }
            work.DataUris++;
            if (work.DataUris > work.Limits.MaxDataUris)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Limit("The data: URI count", work.DataUris, work.Limits.MaxDataUris));
            }
            if (work.DecodedUriBytes + decodedLength > work.Limits.MaxSourceBytes)
            {
                return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes("Decoded data: URIs", work.DecodedUriBytes + decodedLength, work.Limits.MaxSourceBytes));
            }
            work.DecodedUriBytes += decodedLength;
            decoded = new byte[decodedLength];
            if (!BasisGlbContainer.TryDecodeBase64(uri, payloadStart, dataChars, decoded, 0))
            {
                decoded = null;
                return work.Fail(BasisGlbErrorKind.Malformed, path + " has malformed base64.");
            }
            return true;
        }

        private static bool TryResolveImage(BasisGltfWork work, BasisGltfCanonImage canon, bool[] viewChecked, ref int viewsUsed)
        {
            BasisGltfDocument doc = work.Document;
            BasisGltfImage image = doc.Images[canon.Source];
            string path = BasisGlbErrors.Index("images", canon.Source);
            if (image.HasUri && image.BufferView >= 0) return work.Fail(BasisGlbErrorKind.Malformed, path + " has both a uri and a bufferView.");
            if (!image.HasUri && image.BufferView < 0) return work.Fail(BasisGlbErrorKind.Malformed, path + " has neither a uri nor a bufferView.");
            bool declaredPng = image.MimeType == "image/png";
            bool declaredJpeg = image.MimeType == "image/jpeg";
            if (image.MimeType != null && !declaredPng && !declaredJpeg)
            {
                return work.Fail(BasisGlbErrorKind.ImageRejected, path + ".mimeType must be image/png or image/jpeg.");
            }
            byte[] data;
            int offset, length;
            if (image.BufferView >= 0)
            {
                if (image.MimeType == null) return work.Fail(BasisGlbErrorKind.Malformed, path + " uses a bufferView and must declare a mimeType.");
                if (image.BufferView >= doc.BufferViews.Count) return work.Fail(BasisGlbErrorKind.Malformed, path + ".bufferView is out of range.");
                if (!viewChecked[image.BufferView])
                {
                    if (!TryCheckView(work, image.BufferView)) return false;
                    viewChecked[image.BufferView] = true;
                    viewsUsed++;
                }
                BasisGltfBufferView view = doc.BufferViews[image.BufferView];
                if (view.ByteStride != -1)
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, BasisGlbErrors.Index("bufferViews", image.BufferView) + " holds an image and must not have a byteStride.");
                }
                data = work.BufferData[view.Buffer];
                offset = (int)(work.BufferBase[view.Buffer] + view.ByteOffset);
                length = (int)view.ByteLength;
            }
            else
            {
                if (!TryDecodeUri(work, path, image.UriStart, image.UriLength, image.UriHasEscapes, true, 1, work.Limits.MaxSourceImageBytes,
                        out BasisDataUriMedia media, out data))
                {
                    return false;
                }
                if ((declaredPng && media != BasisDataUriMedia.Png) || (declaredJpeg && media != BasisDataUriMedia.Jpeg))
                {
                    return work.Fail(BasisGlbErrorKind.Malformed, path + ".mimeType does not match its data: URI.");
                }
                declaredPng = media == BasisDataUriMedia.Png;
                declaredJpeg = media == BasisDataUriMedia.Jpeg;
                offset = 0;
                length = data.Length;
            }

            var bytes = new ReadOnlySpan<byte>(data, offset, length);
            if (!work.Sender && !declaredPng)
            {
                return work.Fail(BasisGlbErrorKind.ImageRejected, path + " is not a PNG; received models carry PNG images only.");
            }
            bool isPng = BasisGlbPng.HasSignature(bytes);
            bool isJpeg = BasisGlbPng.HasJpegSignature(bytes);
            if ((declaredPng && !isPng) || (declaredJpeg && !isJpeg))
            {
                string what = BasisGlbPng.HasGifSignature(bytes) ? " is a GIF; glTF images must be PNG or JPEG."
                    : BasisGlbPng.HasWebpSignature(bytes) ? " is a WebP image; glTF images must be PNG or JPEG."
                    : isPng ? " holds PNG data but is labelled image/jpeg."
                    : isJpeg ? " holds JPEG data but is labelled image/png."
                    : " is not a PNG or JPEG image.";
                return work.Fail(BasisGlbErrorKind.ImageRejected, path + what);
            }
            canon.Format = isPng ? BasisModelImageFormat.Png : BasisModelImageFormat.Jpeg;
            canon.SourceData = data;
            canon.SourceOffset = offset;
            canon.SourceLength = length;
            if (work.Sender)
            {
                BasisModelLimits limits = work.Limits;
                if (length > limits.MaxSourceImageBytes)
                {
                    return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Bytes(path, length, limits.MaxSourceImageBytes));
                }
                // PNG headers are checked here, before any main-thread decode; the sanitiser checks JPEG headers itself.
                if (isPng)
                {
                    if (!BasisGlbPng.TryReadDimensions(bytes, out int width, out int height, out string error))
                    {
                        return work.Fail(BasisGlbErrorKind.ImageRejected, path + ": " + error);
                    }
                    if (width > limits.MaxSourceTextureDimension || height > limits.MaxSourceTextureDimension
                        || (long)width * height > limits.MaxSourceTexturePixels)
                    {
                        return work.Fail(BasisGlbErrorKind.OverLimit, BasisGlbErrors.Dimensions(path, width, height,
                            limits.MaxSourceTextureDimension, limits.MaxSourceTexturePixels));
                    }
                }
            }
            return true;
        }

        // ---- helpers ------------------------------------------------------------------------------------------------

        private static int[] Filled(int count)
        {
            var values = new int[count];
            for (int i = 0; i < count; i++) values[i] = -1;
            return values;
        }

        private static bool AllTrue(bool[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!values[i]) return false;
            }
            return true;
        }

        private static string Num(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
