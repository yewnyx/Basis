using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// Record-then-replay PSO cache layered on top of <see cref="BasisShaderPrewarm"/>.
/// ShaderVariantCollection.WarmUp only compiles the shader variant; on the explicit-pipeline
/// backends used by Basis (D3D12 / Vulkan) the first-draw hitch is the full Pipeline State Object —
/// variant + vertex layout + blend/depth/stencil + render-target formats — which the legacy
/// path can't know ahead of time. GraphicsStateCollection traces the real PSOs that render and
/// persists them, so a later session can pre-create them.
///
/// This is complementary, not a replacement: BasisShaderPrewarm stays the proactive first-load
/// warm for never-before-seen content; this drains a chunk of last session's traced PSOs at each
/// content-load point, where the matching shaders are now resident. The reliable win is the base
/// app plus any content the player re-loads across sessions.
///
/// Two instances of the same file are kept because Unity forbids warming a collection that is
/// actively tracing: <see cref="_warm"/> is loaded read-only and warmed at content loads, while
/// <see cref="_trace"/> is loaded and traced all session, then evicted-to-cap and saved at flush.
///
/// Growth is bounded by a disk-size budget (<see cref="MaxCacheBytes"/>), checked against the
/// actual saved file after each flush, with best-effort eviction when over. True per-variant LRU
/// isn't possible here: the API exposes no usage timestamp, warming pre-creates a PSO so the trace
/// never re-observes its reuse, and a variant whose shader has unloaded has no stable identity to
/// track. Eviction drops not-currently-resident variants first, then the most recently traced
/// (protecting the long-lived base-app set). The intent is that once an avatar has been drawn once
/// on this device, it should never need to pay the PSO-creation cost again — the budget is sized
/// to make that true for a very long time, not to keep the cache small.
/// </summary>
public static class BasisGraphicsStatePrewarm
{
    // Developer toggle, default off. Set from BasisSettingsDefaults.EnableGraphicsStatePrewarm.
    // Gated at init so flipping it off keeps the subsystem dormant (no trace, no warm, no file).
    private static bool _enabled;
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) ReleaseScopedWarmupWaiters();
        }
    }

    // PSO caches are graphics-API + engine-version specific; a file traced on one is meaningless
    // on another. Encoding both in the filename means a driver/API/Unity change simply finds no
    // matching file and starts clean, instead of replaying stale or invalid state.
    private static bool _initialized;
    private static bool _supported;
    private static bool _tracing;
    private static string _filePath;
    private static GraphicsStateCollection _warm;
    private static GraphicsStateCollection _seed;
    private static GraphicsStateCollection _trace;
    private static JobHandle _warmHandle;
    private static float _initTime;
    private static float _lastFlushTime;
    private static int _variantsAtLastFlush;
    private static bool _drainLogged;

    public static bool Active => _initialized && _supported;
    public static int SeedVariantCount { get; private set; }
    public static int UserVariantCount { get; private set; }
    public static int PrunedVariantCount { get; private set; }

    // Warming creates GPU pipeline objects; cap per call so a large collection can't schedule an
    // unbounded burst on one content load. Each load drains another chunk.
    public static int MaxWarmupPerCall = 256;
    public static int MaxWarmupPerPump = 16;

    // Ceiling on the on-disk cache, checked by actual file size (not variant count — a variant's
    // serialized size varies a lot with keyword count, so only the real file tells the truth).
    // Intentionally generous: the goal is "an avatar drawn once on this device is never cold
    // again," for as long as that's realistic disk-wise, not a small footprint. One aggressive
    // day of testing measured ~11 MB, so 10 GB is roughly three orders of magnitude of headroom.
    public static long MaxCacheBytes = 10L * 1024 * 1024 * 1024;

    // Short enough that a quick dev-iteration session (launch, test, quit) still persists its trace
    // instead of losing everything to the next cold start when EndTrace only runs at Flush().
    public static float FlushIntervalSeconds = 45f;
    public static int FlushVariantDelta = 20;
    private const string SeedResourcePath = "BasisPso/basis_pso_seed";

    // Only the explicit-PSO backends benefit. On GL / D3D11 the driver builds pipeline state
    // lazily and a precompiled cache buys nothing, so stay off and don't touch disk there.
    // Public: BasisAvatarPsoReveal (com.basis.framework) shares this as the one source of truth
    // for "does this backend pay a synchronous first-draw PSO cost at all".
    public static bool BackendBenefits()
    {
        if (!SystemInfo.supportsParallelPSOCreation)
        {
            return false;
        }
        switch (SystemInfo.graphicsDeviceType)
        {
            case GraphicsDeviceType.Direct3D12:
            case GraphicsDeviceType.Vulkan:
                return true;
            default:
                return false;
        }
    }

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        // Don't latch _initialized while disabled, so a later enable can still bring it up.
        if (!Enabled)
        {
            return;
        }
        _initialized = true;
        _supported = BackendBenefits();
        if (!_supported)
        {
            return;
        }

        try
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "GraphicsState");
            System.IO.Directory.CreateDirectory(dir);
            _filePath = System.IO.Path.Combine(dir, $"basis_pso.{SystemInfo.graphicsDeviceType}.{Application.unityVersion}.gpsc");

            bool onDisk = System.IO.File.Exists(_filePath);

            // Warm source: last session's PSOs, never traced so it stays warmable.
            _warm = LoadCollection(onDisk);
            int loaded = _warm != null ? _warm.variantCount : 0;
            PrunedVariantCount = PruneUnresolved(_warm);

            // Trace sink: starts from the same on-disk set and appends this session's real PSOs.
            _trace = LoadCollection(onDisk);
            PruneUnresolved(_trace);
            _trace.BeginTrace();
            _tracing = _trace.isTracing;

            _seed = LoadSeed();
            SeedVariantCount = _seed != null ? _seed.variantCount : 0;
            UserVariantCount = _warm != null ? _warm.variantCount : 0;
            _initTime = Time.realtimeSinceStartup;
            _lastFlushTime = _initTime;
            _variantsAtLastFlush = _trace.variantCount;
            BasisDebug.Log($"BasisGraphicsStatePrewarm: {SystemInfo.graphicsDeviceType} seed {SeedVariantCount} variant(s), user cache {UserVariantCount} of {loaded} replayable ({PrunedVariantCount} unresolvable pruned)", BasisDebug.LogTag.Event);
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: init failed, disabling ({e.Message})", BasisDebug.LogTag.Event);
            _supported = false;
            _warm = null;
            _seed = null;
            _trace = null;
        }
    }

    private static GraphicsStateCollection LoadCollection(bool onDisk)
    {
        GraphicsStateCollection collection = new GraphicsStateCollection();
        if (onDisk)
        {
            // A corrupt or version-mismatched file throws or loads nothing; either way fall back to
            // an empty collection so this session still functions.
            try
            {
                collection.LoadFromFile(_filePath);
            }
            catch (System.Exception load)
            {
                BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: load failed, starting fresh ({load.Message})", BasisDebug.LogTag.Event);
                collection = new GraphicsStateCollection();
            }
        }
        return collection;
    }

    private static int PruneUnresolved(GraphicsStateCollection collection)
    {
        if (collection == null || collection.variantCount == 0)
        {
            return 0;
        }
        int before = collection.variantCount;
        List<GraphicsStateCollection.ShaderVariant> variants = new List<GraphicsStateCollection.ShaderVariant>();
        collection.GetVariants(variants);
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i].shader != null)
            {
                continue;
            }
            try
            {
                collection.RemoveVariant(variants[i].shader, variants[i].passId, variants[i].keywords);
            }
            catch (System.Exception)
            {
            }
        }
        return before - collection.variantCount;
    }

    public static string SeedResourceNameFor(GraphicsDeviceType api)
    {
        return $"{SeedResourcePath}_{api}";
    }

    private static GraphicsStateCollection LoadSeed()
    {
        TextAsset asset = Resources.Load<TextAsset>(SeedResourceNameFor(SystemInfo.graphicsDeviceType));
        if (asset == null)
        {
            return null;
        }
        try
        {
            string staged = System.IO.Path.Combine(Application.temporaryCachePath, $"basis_pso_seed.{SystemInfo.graphicsDeviceType}.gpsc");
            System.IO.File.WriteAllBytes(staged, asset.bytes);
            GraphicsStateCollection collection = new GraphicsStateCollection();
            collection.LoadFromFile(staged);
            return collection.variantCount > 0 ? collection : null;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: seed load failed, ignoring ({e.Message})", BasisDebug.LogTag.Event);
            return null;
        }
        finally
        {
            Resources.UnloadAsset(asset);
        }
    }

    /// <summary>
    /// Warms a bounded chunk of the persisted PSOs. Called from the same loading-screen point as
    /// <see cref="BasisShaderPrewarm.Warm"/>, where the shaders for the content just loaded are
    /// resident. Job-based, so it amortizes rather than stalling; no-ops once the source is drained.
    /// </summary>
    public static void WarmResident(string label)
    {
        if (!Enabled)
        {
            return;
        }
        EnsureInitialized();
        if (!_supported)
        {
            return;
        }
        Drain(MaxWarmupPerCall, label);
    }

    /// <summary>
    /// A content-scoped asynchronous warm-up. The caller must keep the content hidden until
    /// <see cref="CompleteAndDispose"/> has been called; doing so is what prevents first draw from
    /// racing DX12/Vulkan pipeline creation. Requests without cached states enter the bounded cold
    /// reveal queue so a mass join cannot trigger thousands of first-draw compiles in one frame.
    /// </summary>
    public sealed class WarmupRequest
    {
        internal readonly HashSet<Shader> Shaders;
        internal readonly IList<Renderer> Renderers;
        internal readonly string Label;
        internal bool Completed;

        internal WarmupRequest(HashSet<Shader> shaders, IList<Renderer> renderers, string label, bool completed)
        {
            Shaders = shaders;
            Renderers = renderers;
            Label = label;
            Completed = completed;
        }

        public bool IsCompleted => Completed;

        public void CompleteAndDispose()
        {
            if (Completed)
            {
                return;
            }
            CompleteScopedWarmupsSynchronously();
        }
    }

    private sealed class WarmupBatch
    {
        public GraphicsStateCollection Collection;
        public JobHandle Handle;
        public readonly List<WarmupRequest> Requests = new List<WarmupRequest>();
        public readonly HashSet<Shader> MatchedShaders = new HashSet<Shader>();
    }

    // Thousands of concurrent avatar loads must not each read the same cache and submit duplicate
    // PSO jobs. Requests arriving during a frame are coalesced into one shader set, one cache read,
    // and one warm-up job. A single active batch also bounds driver-side pipeline creation pressure.
    private static readonly List<WarmupRequest> _pendingScopedWarmups = new List<WarmupRequest>();
    private static readonly HashSet<EntityId> _scopedWarmedShaderIds = new HashSet<EntityId>();
    private static readonly Queue<WarmupRequest> _coldRevealQueue = new Queue<WarmupRequest>();
    private static WarmupBatch _activeScopedBatch;
    // Never expose a mass join's uncached avatars in one frame: each could independently trigger a
    // large first-draw compile. Cached content is still released as a batch after async warm-up.
    public static int MaxColdRevealsPerFrame = 1;

    /// <summary>
    /// Copies cached graphics states for the shaders used by <paramref name="renderers"/> into a
    /// short-lived collection and warms all of them asynchronously. Reloading the user cache here
    /// is intentional: AssetBundle shaders are resident now, whereas the boot-time load can leave
    /// their serialized shader references unresolved.
    /// </summary>
    public static WarmupRequest ScheduleResident(IList<Renderer> renderers, string label)
    {
        if (!Enabled || renderers == null || renderers.Count == 0)
        {
            return new WarmupRequest(null, null, label, true);
        }
        EnsureInitialized();
        if (!_supported)
        {
            return new WarmupRequest(null, null, label, true);
        }

        HashSet<Shader> shaders = CollectShaders(renderers);
        shaders.RemoveWhere(shader => shader == null || _scopedWarmedShaderIds.Contains(shader.GetEntityId()));
        if (shaders.Count == 0)
        {
            return new WarmupRequest(null, null, label, true);
        }

        WarmupRequest request = new WarmupRequest(shaders, renderers, label, false);
        _pendingScopedWarmups.Add(request);
        return request;
    }

    private static bool PumpScopedWarmups(bool completeSynchronously)
    {
        int coldBudget = completeSynchronously ? int.MaxValue : System.Math.Max(1, MaxColdRevealsPerFrame);
        while (coldBudget-- > 0 && _coldRevealQueue.Count > 0)
        {
            WarmupRequest cold = _coldRevealQueue.Dequeue();
            WarmColdShaders(cold);
            cold.Completed = true;
        }

        if (_activeScopedBatch != null)
        {
            if (!completeSynchronously && !_activeScopedBatch.Handle.IsCompleted)
            {
                return true;
            }
            _activeScopedBatch.Handle.Complete();
            for (int i = 0; i < _activeScopedBatch.Requests.Count; i++)
            {
                CompleteOrThrottleCold(_activeScopedBatch.Requests[i], _activeScopedBatch.MatchedShaders, completeSynchronously);
            }
            foreach (Shader shader in _activeScopedBatch.MatchedShaders)
            {
                if (shader != null) _scopedWarmedShaderIds.Add(shader.GetEntityId());
            }
            Object.Destroy(_activeScopedBatch.Collection);
            _activeScopedBatch = null;
        }

        if (_pendingScopedWarmups.Count == 0)
        {
            return false;
        }

        WarmupBatch batch = new WarmupBatch();
        HashSet<Shader> shaders = new HashSet<Shader>();
        for (int i = 0; i < _pendingScopedWarmups.Count; i++)
        {
            WarmupRequest request = _pendingScopedWarmups[i];
            request.Shaders.RemoveWhere(shader => shader == null || _scopedWarmedShaderIds.Contains(shader.GetEntityId()));
            if (request.Shaders.Count == 0)
            {
                request.Completed = true;
                continue;
            }
            batch.Requests.Add(request);
            shaders.UnionWith(request.Shaders);
        }
        _pendingScopedWarmups.Clear();

        if (batch.Requests.Count == 0)
        {
            return false;
        }

        GraphicsStateCollection residentUserCache = null;
        try
        {
            batch.Collection = new GraphicsStateCollection();
            CopyMatchingStates(_seed, batch.Collection, shaders, batch.MatchedShaders);
            if (_filePath != null && System.IO.File.Exists(_filePath))
            {
                // One reload per coalesced batch, after its AssetBundle shaders are resident.
                residentUserCache = LoadCollection(true);
                CopyMatchingStates(residentUserCache, batch.Collection, shaders, batch.MatchedShaders);
            }
            else
            {
                CopyMatchingStates(_warm, batch.Collection, shaders, batch.MatchedShaders);
            }

            if (batch.Collection.totalGraphicsStateCount == 0)
            {
                // This is first-seen content, not a failure. Let one cold draw trace the real PSO;
                // later loads can use it after the trace has been persisted.
                Object.Destroy(batch.Collection);
                for (int i = 0; i < batch.Requests.Count; i++)
                {
                    CompleteOrThrottleCold(batch.Requests[i], batch.MatchedShaders, completeSynchronously);
                }
                return false;
            }

            batch.Handle = batch.Collection.WarmUp(default);
            _activeScopedBatch = batch;
            if (VerboseWarmupLogging)
            {
                BasisDebug.Log($"BasisGraphicsStatePrewarm: coalesced {batch.Requests.Count} load(s), {shaders.Count} shader(s), and {batch.Collection.totalGraphicsStateCount} graphics state(s)", BasisDebug.LogTag.Event);
            }
            if (completeSynchronously)
            {
                return PumpScopedWarmups(true);
            }
            return true;
        }
        catch (System.Exception e)
        {
            if (batch.Collection != null) Object.Destroy(batch.Collection);
            for (int i = 0; i < batch.Requests.Count; i++) batch.Requests[i].Completed = true;
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: coalesced warm-up failed ({e.Message})", BasisDebug.LogTag.Event);
            return false;
        }
        finally
        {
            if (residentUserCache != null) Object.Destroy(residentUserCache);
        }
    }

    private static void CompleteScopedWarmupsSynchronously()
    {
        while (PumpScopedWarmups(true))
        {
        }
    }

    private static void ReleaseScopedWarmupWaiters()
    {
        if (_activeScopedBatch != null)
        {
            _activeScopedBatch.Handle.Complete();
            for (int i = 0; i < _activeScopedBatch.Requests.Count; i++)
            {
                _activeScopedBatch.Requests[i].Completed = true;
            }
            Object.Destroy(_activeScopedBatch.Collection);
            _activeScopedBatch = null;
        }
        for (int i = 0; i < _pendingScopedWarmups.Count; i++)
        {
            _pendingScopedWarmups[i].Completed = true;
        }
        _pendingScopedWarmups.Clear();
        while (_coldRevealQueue.Count > 0)
        {
            _coldRevealQueue.Dequeue().Completed = true;
        }
    }

    private static void CompleteOrThrottleCold(WarmupRequest request, HashSet<Shader> matchedShaders, bool completeSynchronously)
    {
        bool hasColdShader = false;
        foreach (Shader shader in request.Shaders)
        {
            if (shader != null && !matchedShaders.Contains(shader))
            {
                hasColdShader = true;
                break;
            }
        }

        if (!hasColdShader || completeSynchronously)
        {
            if (hasColdShader)
            {
                WarmColdShaders(request);
            }
            request.Completed = true;
        }
        else
        {
            _coldRevealQueue.Enqueue(request);
        }
    }

    private static void WarmColdShaders(WarmupRequest request)
    {
        // ShaderVariantCollection has no asynchronous API. Running it here keeps the object hidden
        // and, critically, serializes never-seen UGC so a mass join cannot make many compilers peak
        // memory at once. The remaining first-draw graphics state is then captured by the trace.
        try
        {
            BasisShaderPrewarm.Warm(request.Renderers, request.Label);
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: cold shader warm '{request.Label}' failed ({e.Message})", BasisDebug.LogTag.Event);
        }
    }

    public static bool VerboseWarmupLogging = false;

    private static HashSet<Shader> CollectShaders(IList<Renderer> renderers)
    {
        HashSet<Shader> shaders = new HashSet<Shader>();
        for (int i = 0; i < renderers.Count; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null) continue;
            Material[] materials = renderer.sharedMaterials;
            for (int j = 0; j < materials.Length; j++)
            {
                Material material = materials[j];
                if (material != null && material.shader != null)
                {
                    shaders.Add(material.shader);
                }
            }
        }
        return shaders;
    }

    private static void CopyMatchingStates(GraphicsStateCollection source, GraphicsStateCollection destination, HashSet<Shader> shaders, HashSet<Shader> matchedShaders)
    {
        if (source == null || source.variantCount == 0)
        {
            return;
        }

        List<GraphicsStateCollection.ShaderVariant> variants = new List<GraphicsStateCollection.ShaderVariant>();
        List<GraphicsStateCollection.GraphicsState> states = new List<GraphicsStateCollection.GraphicsState>();
        source.GetVariants(variants);
        for (int i = 0; i < variants.Count; i++)
        {
            GraphicsStateCollection.ShaderVariant variant = variants[i];
            if (variant.shader == null || !shaders.Contains(variant.shader))
            {
                continue;
            }

            states.Clear();
            source.GetGraphicsStatesForVariant(variant, states);
            if (states.Count == 0)
            {
                continue;
            }
            destination.AddVariant(variant.shader, variant.passId, variant.keywords);
            matchedShaders.Add(variant.shader);
            for (int stateIndex = 0; stateIndex < states.Count; stateIndex++)
            {
                destination.AddGraphicsStateForVariant(variant, states[stateIndex]);
            }
        }
    }

    public static void Pump()
    {
        if (!Enabled)
        {
            return;
        }
        EnsureInitialized();
        if (!_supported)
        {
            return;
        }
        bool scopedBusy = PumpScopedWarmups(false);
        if (!scopedBusy)
        {
            Drain(MaxWarmupPerPump, null);
        }
        MaybeFlush();
    }

    private static void Drain(int budget, string label)
    {
        try
        {
            if (TryWarm(_seed, budget) || TryWarm(_warm, budget))
            {
                return;
            }
            if (!_drainLogged && SeedVariantCount + UserVariantCount > 0)
            {
                _drainLogged = true;
                BasisDebug.Log($"BasisGraphicsStatePrewarm: {SeedVariantCount + UserVariantCount} cached variant(s) warmed in {Time.realtimeSinceStartup - _initTime:F1}s", BasisDebug.LogTag.Event);
            }
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: warm '{label ?? "pump"}' failed ({e.Message})", BasisDebug.LogTag.Event);
        }
    }

    private static bool TryWarm(GraphicsStateCollection collection, int budget)
    {
        if (collection == null || budget <= 0 || collection.variantCount == 0 || collection.isWarmedUp)
        {
            return false;
        }
        // Chain on the previous warm so successive loads queue instead of racing.
        _warmHandle = collection.WarmUpProgressively(budget, _warmHandle);
        return true;
    }

    /// <summary>
    /// Stops tracing, persists the trace sink, and evicts down to the disk budget if the save came
    /// in over it. Call on app quit / play-mode exit so the PSOs traced this session survive to warm
    /// the next one.
    /// </summary>
    public static void Flush()
    {
        Persist(false);
    }

    private static void MaybeFlush()
    {
        if (!_tracing || _trace == null)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (now - _lastFlushTime < FlushIntervalSeconds)
        {
            return;
        }
        _lastFlushTime = now;
        if (_trace.variantCount - _variantsAtLastFlush < FlushVariantDelta)
        {
            return;
        }
        Persist(true);
    }

    private static void Persist(bool resumeTracing)
    {
        if (!_initialized || !_supported || _trace == null)
        {
            return;
        }
        try
        {
            _warmHandle.Complete();

            if (_tracing)
            {
                _trace.EndTrace();
                _tracing = false;
            }

            if (_filePath != null && _trace.variantCount > 0)
            {
                _trace.SaveToFile(_filePath);
                EvictToDiskBudget();
            }
            _variantsAtLastFlush = _trace.variantCount;

            if (resumeTracing)
            {
                _trace.BeginTrace();
                _tracing = _trace.isTracing;
            }
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: flush failed ({e.Message})", BasisDebug.LogTag.Event);
        }
    }

    // Checked against the file *just saved*, not variant count — a variant's serialized size
    // varies a lot with keyword count, so only the real file size tells the truth. Estimates
    // bytes/variant from that same file to size the trim, then re-saves and re-measures rather
    // than trusting the estimate, since a shader-heavy trim pass can under- or overshoot it.
    // Runs rarely: see MaxCacheBytes for why this shouldn't trigger under normal use.
    private static void EvictToDiskBudget()
    {
        if (_filePath == null || !System.IO.File.Exists(_filePath))
        {
            return;
        }

        long size = new System.IO.FileInfo(_filePath).Length;
        if (size <= MaxCacheBytes)
        {
            return;
        }

        for (int pass = 0; pass < 5 && size > MaxCacheBytes; pass++)
        {
            int variantCount = _trace.variantCount;
            if (variantCount <= 0)
            {
                break;
            }

            double bytesPerVariant = (double)size / variantCount;
            long targetBytes = MaxCacheBytes - MaxCacheBytes / 20; // trim a bit past the line so this doesn't re-trigger next flush
            long excessBytes = size - targetBytes;
            int toRemove = (int)System.Math.Min(variantCount, System.Math.Max(1, System.Math.Ceiling(excessBytes / System.Math.Max(1.0, bytesPerVariant))));

            if (!RemoveLowestPriority(toRemove))
            {
                break;
            }

            _trace.SaveToFile(_filePath);
            size = new System.IO.FileInfo(_filePath).Length;
        }

        if (size > MaxCacheBytes)
        {
            BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: cache still {size / (1024 * 1024)} MB after eviction, over the {MaxCacheBytes / (1024 * 1024)} MB budget", BasisDebug.LogTag.Event);
        }
    }

    // Ranks and removes up to `toRemove` variants. Priority signal, best available from this API:
    // a variant whose shader is no longer resident (its content isn't loaded) goes first; ties
    // break by trace order, newest-first, so the long-lived base-app variants traced early in the
    // session are the last to be evicted. Returns false if nothing could be removed.
    private static bool RemoveLowestPriority(int toRemove)
    {
        List<GraphicsStateCollection.ShaderVariant> variants = new List<GraphicsStateCollection.ShaderVariant>();
        _trace.GetVariants(variants);
        int count = variants.Count;
        if (count == 0)
        {
            return false;
        }
        toRemove = System.Math.Min(toRemove, count);

        bool[] resident = new bool[count];
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            resident[i] = variants[i].shader != null;
            order[i] = i;
        }
        System.Array.Sort(order, (a, b) =>
        {
            if (resident[a] != resident[b])
            {
                return resident[a] ? 1 : -1; // non-resident (false) sorts first => evicted first
            }
            return b.CompareTo(a);           // newest trace index first within the same residency
        });

        int removed = 0;
        for (int r = 0; r < toRemove; r++)
        {
            GraphicsStateCollection.ShaderVariant victim = variants[order[r]];
            try
            {
                _trace.RemoveVariant(victim.shader, victim.passId, victim.keywords);
                removed++;
            }
            catch (System.Exception removeEx)
            {
                // best-effort; a refused removal just leaves it cached this round
                BasisDebug.LogWarning($"BasisGraphicsStatePrewarm: variant eviction refused ({removeEx.Message})", BasisDebug.LogTag.Event);
            }
        }
        return removed > 0;
    }
}
