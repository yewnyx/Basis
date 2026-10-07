using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.ModelPickup.Validation;
using GLTFast;
using GLTFast.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Basis.ModelPickup
{
    /// <summary>One import request: who it is for, and whether they still want it.</summary>
    public sealed class BasisModelImportTicket
    {
        /// <summary>
        /// Set when nobody wants the result any more (the pickup was destroyed, its job died). glTFast ignores
        /// cancellation, so the loader checks this after each awaited step and destroys what it built.
        /// </summary>
        public volatile bool Abandoned;

        /// <summary>Names the import in log lines. Must not contain remote text (no file or owner names).</summary>
        public readonly string Label;

        /// <summary>
        /// The bytes came from another player. Their glTFast diagnostics are logged as one capped warning, never as
        /// errors, because every error is reported to the error server when error notifications are on.
        /// </summary>
        public readonly bool Remote;

        public BasisModelImportTicket(string label, bool remote)
        {
            Label = string.IsNullOrEmpty(label) ? "model" : label;
            Remote = remote;
        }
    }

    /// <summary>Per-device import choices; the manager fills these from the tier.</summary>
    public struct BasisModelImportOptions
    {
        public int Layer;
        public bool CastShadows;

        /// <summary>
        /// glTFast builds mips on the CPU inside the main-thread texture decode. The validator's decoded-memory estimate
        /// assumes them.
        /// </summary>
        public bool GenerateMipMaps;
    }

    /// <summary>
    /// What <see cref="BasisModelGltfLoader.ImportAsync"/> produced. On success the inactive holder and the glTFast
    /// import are live and owned by whoever holds this result until a pickup adopts them; on failure both are null.
    /// </summary>
    public sealed class BasisModelImportResult : IDisposable
    {
        public bool Ok;
        public bool Cancelled;
        public string Error;
        public GltfImport Import;
        public GameObject Holder;

        /// <summary>Destroys the holder, then disposes the import. Idempotent; a no-op once a pickup has adopted them.</summary>
        public void Dispose()
        {
            BasisModelGltfLoader.DestroyModel(Holder, Import);
            Holder = null;
            Import = null;
        }
    }

    /// <summary>
    /// Turns a validated canonical GLB into an inactive GameObject hierarchy through glTFast, on the main thread.
    /// Only <see cref="BasisGlbValidationResult.CleanGlb"/> may be passed in: glTFast does no bounds checking of its
    /// own in release players. Each import gets the deny-all download provider, the caller's defer agent, a
    /// collecting logger, no animation and mesh-only instantiation; the result is checked against the validator's
    /// stats before anyone sees it.
    /// </summary>
    public static class BasisModelGltfLoader
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;

        public const int MaxForwardedLocalLogItems = 16;
        public const int MaxRemoteLogCharacters = 200;

        /// <summary>The import time slice. Created by the manager with the tier's budget and armed from its tick.</summary>
        public static BasisModelDeferAgent DeferAgent;

        // Scratch for the synchronous checks after instantiation. Nothing awaits while these are in use, so imports
        // interleaving on the main thread never see each other's contents.
        private static readonly List<Component> s_Components = new List<Component>();
        private static readonly List<Renderer> s_Renderers = new List<Renderer>();
        private static readonly List<MeshFilter> s_MeshFilters = new List<MeshFilter>();
        private static readonly HashSet<Mesh> s_SkinnedMeshes = new HashSet<Mesh>();
        private static readonly HashSet<Texture2D> s_Textures = new HashSet<Texture2D>();

        /// <summary>
        /// Main thread only, and never after a <c>ConfigureAwait(false)</c>. Never throws. On any outcome other than
        /// success the method has already destroyed its holder and disposed its import; on success the holder is a
        /// standalone root (so destroying a pickup mid-import can never orphan glTFast objects) and still inactive.
        /// </summary>
        public static async Task<BasisModelImportResult> ImportAsync(
            byte[] canonicalGlb,
            BasisGlbStats stats,
            BasisModelImportOptions options,
            IDeferAgent deferAgent,
            BasisModelImportTicket ticket
        )
        {
            var result = new BasisModelImportResult();
            bool remote = ticket == null || ticket.Remote;
            var logger = new CollectingLogger();
            GltfImport import = null;
            GameObject holder = null;
            try
            {
                if (ticket == null)
                    return Fail(result, "the import has no ticket");
                if (canonicalGlb == null || canonicalGlb.Length == 0)
                    return Fail(result, "there is no model data");
                // A null agent would make glTFast create its global 30 fps agent object, which also fails in edit mode.
                if (deferAgent == null)
                    return Fail(result, "the import has no defer agent");

                // A null provider would make glTFast fall back to its default one, which fetches external URIs.
                BasisModelDenyAllDownloadProvider downloads = BasisModelDenyAllDownloadProvider.Instance;
                if (downloads == null)
                    return Fail(result, "BasisModelDenyAllDownloadProvider.Instance is null");

                import = new GltfImport(downloads, deferAgent, null, logger);
                var settings = new ImportSettings
                {
                    NodeNameMethod = NameImportMethod.Original,
                    AnimationMethod = AnimationMethod.None,
                    GenerateMipMaps = options.GenerateMipMaps,
                    TexturesReadable = false,
                    AnisotropicFilterLevel = 1,
                };
                bool loaded = await import.Load(canonicalGlb, null, settings);
                if (ticket.Abandoned)
                    return Cancel(result);
                if (!loaded || import.LoadingError)
                    return Fail(result, "glTF load failed: " + FirstProblem(logger, remote));

                holder = new GameObject("Model");
                holder.SetActive(false);
                holder.layer = options.Layer;
                if (Application.isPlaying)
                    Object.DontDestroyOnLoad(holder);

                var instantiator = new GameObjectInstantiator(
                    import,
                    holder.transform,
                    logger,
                    new InstantiationSettings
                    {
                        Mask = ComponentType.Mesh,
                        SceneObjectCreation = SceneObjectCreation.Always,
                        SkinUpdateWhenOffscreen = false,
                        Layer = options.Layer,
                    }
                );
                // A file without "scene" instantiates nothing through the main-scene call; the canonical form always
                // has one, but fall back to scene 0 rather than show an empty pickup.
                int scene = import.DefaultSceneIndex ?? 0;
                if (scene < 0 || scene >= import.SceneCount)
                    return Fail(result, "the model has no scene to show");
                bool placed = await import.InstantiateSceneAsync(instantiator, scene);
                if (ticket.Abandoned)
                    return Cancel(result);
                if (!placed)
                    return Fail(result, "glTF instantiation failed: " + FirstProblem(logger, remote));

                // One walk of glTFast's hierarchy serves both: the check collects the renderers and mesh filters the
                // display pass then uses. Nothing awaits between the two, so no other import sees the lists.
                if (!VerifyAgainstStats(import, holder, stats, true, out string reason))
                    return Fail(result, "imported model does not match its validated description: " + reason);
                PrepareForDisplay(import, options.CastShadows);

                result.Ok = true;
                result.Import = import;
                result.Holder = holder;
                return result;
            }
            catch (Exception e)
            {
                string message = e.GetType().Name + ": " + e.Message;
                return Fail(result, remote ? SanitizeRemoteText(message) : message);
            }
            finally
            {
                // Cleanup comes first: nothing below may skip it. Every await above has completed by now, so glTFast
                // has no load or instantiation in flight that could read what Dispose frees.
                if (!result.Ok)
                {
                    result.Holder = null;
                    result.Import = null;
                    try
                    {
                        DestroyModel(holder, import);
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogWarning("Model import cleanup failed: " + e.GetType().Name, LogTag);
                    }
                }
                try
                {
                    ForwardLog(logger, ticket, remote);
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning("Model import log forwarding failed: " + e.GetType().Name, LogTag);
                }
            }
        }

        /// <summary>
        /// Checks the instantiated hierarchy against what the validator measured, so a glTFast behaviour the
        /// validator does not model can never put more on screen than the claims admitted. Bounds come from the
        /// validator, never from renderers. Textures are checked through <see cref="GltfImport.GetTexture"/>: glTFast
        /// releases its image list once loading ends, while the texture list (sampler clones included) survives.
        /// </summary>
        public static bool VerifyAgainstStats(GltfImport import, GameObject holder, in BasisGlbStats stats, out string reason)
        {
            return VerifyAgainstStats(import, holder, stats, false, out reason);
        }

        /// <summary>
        /// <see cref="VerifyAgainstStats(GltfImport,GameObject,in BasisGlbStats,out string)"/>, and with
        /// <paramref name="collectForDisplay"/> it also leaves the hierarchy's renderers and mesh filters in the
        /// scratch lists <see cref="PrepareForDisplay"/> reads and clears, so the hierarchy is walked once. The lists
        /// are cleared here on any failure. Call <see cref="PrepareForDisplay"/> next, with no await in between.
        /// </summary>
        public static bool VerifyAgainstStats(
            GltfImport import,
            GameObject holder,
            in BasisGlbStats stats,
            bool collectForDisplay,
            out string reason
        )
        {
            bool verified = false;
            try
            {
                verified = Verify(import, holder, stats, collectForDisplay, out reason);
                return verified;
            }
            finally
            {
                if (!verified)
                {
                    s_Renderers.Clear();
                    s_MeshFilters.Clear();
                }
            }
        }

        private static bool Verify(GltfImport import, GameObject holder, in BasisGlbStats stats, bool collect, out string reason)
        {
            reason = null;
            if (import == null || holder == null)
            {
                reason = "nothing was imported";
                return false;
            }

            int transforms = 0;
            int renderers = 0;
            holder.GetComponentsInChildren(true, s_Components);
            try
            {
                int componentCount = s_Components.Count;
                for (int i = 0; i < componentCount; i++)
                {
                    Component component = s_Components[i];
                    if (component is Transform)
                    {
                        transforms++;
                        continue;
                    }
                    if (component is MeshRenderer || component is SkinnedMeshRenderer)
                    {
                        renderers++;
                        if (collect)
                            s_Renderers.Add((Renderer)component);
                        continue;
                    }
                    if (component is MeshFilter filter)
                    {
                        if (collect)
                            s_MeshFilters.Add(filter);
                        continue;
                    }
                    // Mesh-only instantiation creates nothing else: a camera, light, animation, collider or script
                    // here means glTFast built something the mask was meant to exclude.
                    reason = "it contains a " + (component == null ? "missing script" : component.GetType().Name) + " component";
                    return false;
                }
            }
            finally
            {
                s_Components.Clear();
            }

            // A mesh instanced on N nodes yields N renderers; DrawCalls is that sum over instances.
            if (renderers > stats.DrawCalls)
            {
                reason = renderers + " renderers for " + stats.DrawCalls + " draw calls";
                return false;
            }
            // The holder, glTFast's scene object, one per node, and at most one extra per draw call when a node's
            // primitives need separate meshes.
            long maxTransforms = 2L + stats.Nodes + stats.DrawCalls;
            if (transforms > maxTransforms)
            {
                reason = transforms + " transforms for " + stats.Nodes + " nodes";
                return false;
            }

            IReadOnlyCollection<Mesh> meshes = import.Meshes;
            int meshCount = meshes != null ? meshes.Count : 0;
            if (meshCount > stats.Primitives)
            {
                reason = meshCount + " meshes for " + stats.Primitives + " primitives";
                return false;
            }
            long vertices = 0;
            long indices = 0;
            if (meshes != null)
            {
                foreach (Mesh mesh in meshes)
                {
                    if (mesh == null)
                        continue;
                    vertices += mesh.vertexCount;
                    int subMeshes = mesh.subMeshCount;
                    for (int s = 0; s < subMeshes; s++)
                        indices += mesh.GetIndexCount(s);
                }
            }
            if (vertices > stats.Vertices)
            {
                reason = vertices + " vertices for " + stats.Vertices + " validated";
                return false;
            }
            if (indices > 3L * stats.Triangles)
            {
                reason = indices + " indices for " + stats.Triangles + " triangles";
                return false;
            }

            if (import.MaterialCount > stats.Materials)
            {
                reason = import.MaterialCount + " materials for " + stats.Materials + " validated";
                return false;
            }
            int textureCount = import.TextureCount;
            if (textureCount > stats.Textures)
            {
                reason = textureCount + " textures for " + stats.Textures + " validated";
                return false;
            }
            long pixels = 0;
            try
            {
                for (int t = 0; t < textureCount; t++)
                {
                    Texture2D texture = import.GetTexture(t);
                    // Several glTF textures can share one Unity texture; count each once.
                    if (texture == null || !s_Textures.Add(texture))
                        continue;
                    if (texture.width > stats.MaxTextureDimension || texture.height > stats.MaxTextureDimension)
                    {
                        reason = "a " + texture.width + "x" + texture.height + " texture exceeds "
                            + stats.MaxTextureDimension + " px";
                        return false;
                    }
                    pixels += (long)texture.width * texture.height;
                }
            }
            finally
            {
                s_Textures.Clear();
            }
            if (pixels > stats.TexturePixels)
            {
                reason = pixels + " texture pixels for " + stats.TexturePixels + " validated";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Destroys the holder, then disposes the import (its meshes, materials and textures). Edit mode destroys
        /// immediately, as glTFast's own Dispose does. glTFast's shared default material is never touched: no
        /// import owns it.
        /// </summary>
        public static void DestroyModel(GameObject holder, GltfImport import)
        {
            if (holder != null)
                DestroyUnityObject(holder);
            import?.Dispose();
        }

        public static void DestroyUnityObject(Object target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(target);
            else
                Object.DestroyImmediate(target);
        }

        /// <summary>
        /// Shadows per tier, and CPU copies released wherever nothing reads them. Static meshes are drawn and
        /// highlighted from GPU data only, and trigger boxes never need mesh data, so their CPU copies go; skinned
        /// meshes keep theirs for CPU skinning. A texture glTFast left readable (an image sampled two ways, cloned per
        /// sampler) is uploaded once more and released. Works on the renderers and filters a collecting
        /// <see cref="VerifyAgainstStats(GltfImport,GameObject,in BasisGlbStats,bool,out string)"/> just gathered,
        /// and clears them.
        /// </summary>
        private static void PrepareForDisplay(GltfImport import, bool castShadows)
        {
            ShadowCastingMode shadowMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            try
            {
                int rendererCount = s_Renderers.Count;
                for (int i = 0; i < rendererCount; i++)
                {
                    Renderer renderer = s_Renderers[i];
                    renderer.shadowCastingMode = shadowMode;
                    renderer.receiveShadows = castShadows;
                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                        s_SkinnedMeshes.Add(skinned.sharedMesh);
                }

                int filterCount = s_MeshFilters.Count;
                for (int i = 0; i < filterCount; i++)
                {
                    Mesh mesh = s_MeshFilters[i].sharedMesh;
                    if (mesh != null && mesh.isReadable && !s_SkinnedMeshes.Contains(mesh))
                        mesh.UploadMeshData(true);
                }
            }
            finally
            {
                s_Renderers.Clear();
                s_MeshFilters.Clear();
                s_SkinnedMeshes.Clear();
            }

            int textureCount = import.TextureCount;
            for (int t = 0; t < textureCount; t++)
            {
                Texture2D texture = import.GetTexture(t);
                if (texture != null && texture.isReadable)
                    texture.Apply(false, true);
            }
        }

        private static BasisModelImportResult Fail(BasisModelImportResult result, string error)
        {
            result.Ok = false;
            result.Error = error;
            return result;
        }

        private static BasisModelImportResult Cancel(BasisModelImportResult result)
        {
            result.Ok = false;
            result.Cancelled = true;
            result.Error = "the import was abandoned";
            return result;
        }

        private static bool IsErrorType(LogType type)
        {
            return type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
        }

        private static string FirstProblem(CollectingLogger logger, bool remote)
        {
            // Items is null until something is logged.
            if (logger.Count == 0)
                return "no details";
            foreach (LogItem item in logger.Items)
            {
                if (item != null && IsErrorType(item.Type))
                    return remote ? SanitizeRemoteText(item.ToString()) : item.ToString();
            }
            return "no details";
        }

        /// <summary>
        /// Local imports: up to <see cref="MaxForwardedLocalLogItems"/> items at their own severity. Remote imports:
        /// one warning per import, the first error (else the first warning) capped and with tags defused, since glTFast
        /// messages can echo file content and a remote sender should not be able to raise error reports.
        /// </summary>
        private static void ForwardLog(CollectingLogger logger, BasisModelImportTicket ticket, bool remote)
        {
            int count = logger.Count;
            if (count == 0)
                return;
            string prefix = "Model import (" + (ticket != null ? ticket.Label : "model") + "): ";

            if (remote)
            {
                LogItem chosen = null;
                foreach (LogItem item in logger.Items)
                {
                    if (item == null)
                        continue;
                    if (IsErrorType(item.Type))
                    {
                        chosen = item;
                        break;
                    }
                    if (chosen == null && item.Type == LogType.Warning)
                        chosen = item;
                }
                if (chosen == null)
                    return;
                string line = prefix + SanitizeRemoteText(chosen.ToString());
                if (count > 1)
                    line += " (" + (count - 1) + " more glTFast messages)";
                BasisDebug.LogWarning(line, LogTag);
                return;
            }

            int forwarded = 0;
            foreach (LogItem item in logger.Items)
            {
                if (item == null)
                    continue;
                if (forwarded == MaxForwardedLocalLogItems)
                {
                    BasisDebug.LogWarning(prefix + (count - forwarded) + " more glTFast messages were not shown.", LogTag);
                    return;
                }
                forwarded++;
                string text = prefix + item;
                if (IsErrorType(item.Type))
                    BasisDebug.LogError(text, LogTag);
                else if (item.Type == LogType.Warning)
                    BasisDebug.LogWarning(text, LogTag);
                else
                    BasisDebug.Log(text, LogTag);
            }
        }

        /// <summary>At most <see cref="MaxRemoteLogCharacters"/> characters, with '&lt;' replaced so no rich-text tag survives.</summary>
        public static string SanitizeRemoteText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (text.Length > MaxRemoteLogCharacters)
                text = text.Substring(0, MaxRemoteLogCharacters);
            return text.Replace('<', '‹');
        }
    }
}
