using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
public static class BasisBundleLoadAsset
{
    public static async Task<GameObject> LoadFromWrapper(GameObject DisabledGameobject,BasisTrackedBundleWrapper BasisLoadableBundle, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent = null, bool DestroyColliders = false,bool ChangeColidersToCorrectLayer = false, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null)
    {
        if (BasisLoadableBundle.AssetBundle != null || BasisLoadableBundle.HasGltfTemplate)
        {
            BasisLoadableBundle output = BasisLoadableBundle.LoadableBundle;
            if (output.BasisBundleConnector.GetPlatform(out BasisBundleGenerated Generated))
            {
                switch (Generated.AssetMode)
                {
                    case BasisBundleConnector.GameObjectAssetMode:
                        {
                            string ReplacedName = Generated.AssetToLoadName.Replace(".bundle", ".prefab");

                            AssetBundleRequest Request = BasisLoadableBundle.AssetBundle.LoadAssetAsync<GameObject>(ReplacedName);
                            await Request;
                            GameObject loadedObject = Request.asset as GameObject;
                            if (loadedObject == null)
                            {
                                BasisDebug.LogError("Unable to proceed, null Gameobject for request " + Generated.AssetToLoadName);

                                string[] assetNames = BasisLoadableBundle.AssetBundle.GetAllAssetNames();
                                BasisDebug.LogError("All assets in bundle: \n" + string.Join("\n", assetNames));

                                BasisLoadableBundle.DidErrorOccur = true;
                                await BasisLoadableBundle.AssetBundle.UnloadAsync(true);
                                return null;
                            }
                            // GraphicsStateCollection resolves Shader references at load time, so
                            // parse the sidecar only after this prefab and its shaders are resident.
                            await BasisLoadableBundle.EnsureEmbeddedGraphicsStatesLoaded();
                            return await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, loadedObject, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
                        }
                    case BasisBundleConnector.GltfAssetMode:
                        {
                            // Generic (glTF) fallback: the wrapper holds an inactive template
                            // instead of an AssetBundle; clone it like a prefab.
                            GameObject template = BasisLoadableBundle.GltfTemplateAvatarRoot;
                            if (template == null)
                            {
                                BasisDebug.LogError("Generic (glTF) template missing on wrapper for " + Generated.AssetToLoadName);
                                BasisLoadableBundle.DidErrorOccur = true;
                                return null;
                            }
                            return await InstantiateContentControlled(DisabledGameobject, BasisLoadableBundle, template, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
                        }
                    default:
                        BasisDebug.LogError("Requested type " + Generated.AssetMode + " has no handler");
                        return null;
                }
            }
            else
            {
                BasisDebug.LogError("Missing Platform Bundle! can't find : " + Application.platform);
            }
        }
        else
        {
            BasisDebug.LogError("Missing Bundle!");
        }
        BasisDebug.LogError("Returning unable to load gameobject!");
        return null;
    }

    private static async Task<GameObject> InstantiateContentControlled(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, GameObject loadedObject, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent, bool DestroyColliders, bool ChangeColidersToCorrectLayer, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop)
    {
        using (IDisposable admission = await BasisPsoLoadAdmission.EnterAsync())
        {
            return await InstantiateContentControlledCore(DisabledGameobject, BasisLoadableBundle, loadedObject, UseContentRemoval, Position, Rotation, ModifyScale, Scale, Selector, Parent, DestroyColliders, ChangeColidersToCorrectLayer, HarvestedHeadChop);
        }
    }

    private static async Task<GameObject> InstantiateContentControlledCore(GameObject DisabledGameobject, BasisTrackedBundleWrapper BasisLoadableBundle, GameObject loadedObject, bool UseContentRemoval, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, Selector Selector, Transform Parent, bool DestroyColliders, bool ChangeColidersToCorrectLayer, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop)
    {
        ChecksRequired ChecksRequired = new ChecksRequired();
        if (loadedObject.TryGetComponent<BasisAvatar>(out BasisAvatar BasisAvatar))
        {
            ChecksRequired.DisableAnimatorEvents = true;
        }
        ChecksRequired.UseContentRemoval = UseContentRemoval;
        ChecksRequired.RemoveColliders = DestroyColliders;
        ChecksRequired.ChangeCollidersToCorrectLayer = ChangeColidersToCorrectLayer;
        ChecksRequired.ScrubPersistentUnityEvents = true;
        BasisContentHarvest harvest = BasisAvatar != null ? new BasisContentHarvest() : null;
        // Instantiate (phase one) and the component strip/scrub walk (phase two) are
        // each a multi-ms main-thread cost; running both in one frame is the load hitch.
        // BeginContentControl parks the clone inactive after Instantiate; yield a frame
        // before FinishContentControl runs the walk + activate so the two never share a
        // frame. The budget gate still spreads concurrent loads across frames on top of that.
        await BasisLoadFrameBudget.WaitForBudgetAsync();
        double instantiateStart = BasisLoadFrameBudget.BeginStep();
        ContentPoliceControl.ContentControlState scrubState = ContentPoliceControl.BeginContentControl(DisabledGameobject, loadedObject, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent, LayerMask.NameToLayer("IgnoredByInteractable"), HarvestedHeadChop, harvest, BasisLoadableBundle.EmbeddedGraphicsStates);
        BasisLoadFrameBudget.EndStep(instantiateStart);
        GameObject CreatedCopy;
        if (scrubState.RemovalWalkPending)
        {
            await Task.Yield();
            await BasisLoadFrameBudget.WaitForBudgetAsync();
            double walkStart = BasisLoadFrameBudget.BeginStep();
            CreatedCopy = ContentPoliceControl.PrepareContentControl(scrubState, out BasisGraphicsStatePrewarm.WarmupRequest warmup);
            BasisLoadFrameBudget.EndStep(walkStart);
            while (!warmup.IsCompleted)
            {
                await Task.Yield();
            }
            warmup.CompleteAndDispose();
            ContentPoliceControl.ActivateContentControl(scrubState, CreatedCopy);
        }
        else
        {
            CreatedCopy = ContentPoliceControl.PrepareContentControl(scrubState, out BasisGraphicsStatePrewarm.WarmupRequest warmup);
            while (!warmup.IsCompleted)
            {
                await Task.Yield();
            }
            warmup.CompleteAndDispose();
            ContentPoliceControl.ActivateContentControl(scrubState, CreatedCopy);
        }
        if (CreatedCopy == null)
        {
            BasisDebug.LogError("ContentControl returned null; clone was destroyed during the frame-split load.");
            return null;
        }
        if (harvest != null && CreatedCopy != null && CreatedCopy.TryGetComponent(out Basis.Scripts.BasisSdk.BasisAvatar createdAvatar))
        {
            createdAvatar.Harvest = harvest;
        }
        // The worn-instance reservation is taken by the LOAD entry points (BasisLoadHandler)
        // BEFORE their first await — incrementing here, after the multi-frame budgeted
        // instantiate, left a window where the unload grace re-check saw zero holders and
        // Unload(true) destroyed the assets under this very clone.
        string InstanceID = BasisGenerateUniqueID.GenerateUniqueID();
        CreatedCopy.name = InstanceID;
        return CreatedCopy;
    }
    public static async Task<Scene> LoadSceneFromBundleAsync(BasisTrackedBundleWrapper bundle, bool MakeActiveScene, BasisProgressReport progressCallback)
    {
        string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        bool AssignedIncrement = false;
        string[] scenePaths = bundle.AssetBundle.GetAllScenePaths();
        if (scenePaths.Length == 0)
        {
            BasisDebug.LogError("No scenes found in AssetBundle.");
            return new Scene();
        }
        if (scenePaths.Length > 1)
        {
            BasisDebug.LogError("More then one scene was found in The Asset Bundle, Please Correct!");
            return new Scene();
        }

        if (!string.IsNullOrEmpty(scenePaths[0]))
        {
            using (IDisposable admission = await BasisPsoLoadAdmission.EnterSceneAsync())
            {
                string scenePath = scenePaths[0];
                string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
                List<GameObject> roots = null;
                bool[] activeRoots = null;

                // sceneLoaded runs before Start and before the first rendered frame. Disable roots
                // there instead of waiting for LoadSceneAsync.isDone, which is too late to ensure
                // an uncached world never reaches the renderer.
                void HideSceneRoots(Scene scene, LoadSceneMode mode)
                {
                    if (!string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    roots = new List<GameObject>();
                    scene.GetRootGameObjects(roots);
                    activeRoots = new bool[roots.Count];
                    for (int i = 0; i < roots.Count; i++)
                    {
                        activeRoots[i] = roots[i].activeSelf;
                        roots[i].SetActive(false);
                    }
                }

                SceneManager.sceneLoaded += HideSceneRoots;
                try
                {
                    AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Additive);
                    if (asyncLoad == null)
                    {
                        BasisDebug.LogError("Failed to start loading scene " + scenePath);
                        return new Scene();
                    }
                    while (!asyncLoad.isDone)
                    {
                        progressCallback.ReportProgress(UniqueID, Mathf.Min(asyncLoad.progress, 0.99f) * 100, $"Activating scene {sceneName}");
                        await Task.Yield();
                    }
                }
                finally
                {
                    SceneManager.sceneLoaded -= HideSceneRoots;
                }

                BasisDebug.Log("Scene loaded successfully from AssetBundle.");
                Scene loadedScene = SceneManager.GetSceneByPath(scenePath);
                bundle.MetaLink = loadedScene.path;
                if (loadedScene.IsValid())
                {
                    // Defensive fallback if Unity did not deliver sceneLoaded for this path.
                    if (roots == null)
                    {
                        roots = new List<GameObject>();
                        loadedScene.GetRootGameObjects(roots);
                        activeRoots = new bool[roots.Count];
                        for (int i = 0; i < roots.Count; i++)
                        {
                            activeRoots[i] = roots[i].activeSelf;
                            roots[i].SetActive(false);
                        }
                    }

                    ChecksRequired ChecksRequired = new ChecksRequired();
                    ChecksRequired.UseContentRemoval = true;
                    ChecksRequired.ScrubPersistentUnityEvents = true;
                    // Scene shaders become resident as part of additive scene load. Loading the
                    // collection here lets Unity resolve them before we select and warm its PSOs.
                    await bundle.EnsureEmbeddedGraphicsStatesLoaded(progressCallback);
                    BasisGraphicsStatePrewarm.WarmupRequest warmup = ContentPoliceControl.ContentControl(ChecksRequired, Selector.World, loadedScene, true, bundle.EmbeddedGraphicsStates);
                    while (!warmup.IsCompleted)
                    {
                        await Task.Yield();
                    }
                    warmup.CompleteAndDispose();
                    for (int i = 0; i < roots.Count; i++)
                    {
                        if (roots[i] != null)
                        {
                            roots[i].SetActive(activeRoots[i]);
                        }
                    }
                    AssignedIncrement = bundle.Increment();
                    if (MakeActiveScene)
                    {
                        SceneManager.SetActiveScene(loadedScene);
                        BasisDebug.Log("Scene set as active: " + loadedScene.name);
                    }
                    BasisDebug.Log("Scene loaded: " + loadedScene.name + " (MakeActive=" + MakeActiveScene + ", Incremented=" + AssignedIncrement + ")");
#if UNITY_BUNDLEUNLOAD
                    bundle.ReleaseBundleBackingStore();
#endif
                    progressCallback.ReportProgress(UniqueID, 100, $"Loaded scene {sceneName}");
                    return loadedScene;
                }
                else
                {
                    BasisDebug.LogError("Failed to get loaded scene.");
                }
            }
        }
        else
        {
            BasisDebug.LogError("Path was null or empty! this should not be happening!");
        }
        return new Scene();
    }
}

// Bounds inactive instantiated content waiting for exact PSO warm-up. This is admission control,
// not a throughput throttle: DX11 and other backends take the no-op lease, while DX12/Vulkan keep
// a mass join from retaining thousands of fully-instantiated avatars/props/worlds at once.
public static class BasisPsoLoadAdmission
{
    public const int MaxConcurrentLoads = 8;
    private static readonly SemaphoreSlim Slots = new SemaphoreSlim(MaxConcurrentLoads, MaxConcurrentLoads);
    private static readonly SemaphoreSlim SceneSlot = new SemaphoreSlim(1, 1);
    private static readonly IDisposable Noop = new NoopLease();

    public static async Task<IDisposable> EnterAsync()
    {
        if (!BasisGraphicsStatePrewarm.Enabled || !BasisGraphicsStatePrewarm.BackendBenefits())
        {
            return Noop;
        }
        await Slots.WaitAsync();
        return new SlotLease();
    }

    public static async Task<IDisposable> EnterSceneAsync()
    {
        if (!BasisGraphicsStatePrewarm.Enabled || !BasisGraphicsStatePrewarm.BackendBenefits())
        {
            return Noop;
        }
        await SceneSlot.WaitAsync();
        return new SemaphoreLease(SceneSlot);
    }

    private sealed class SlotLease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Slots.Release();
            }
        }
    }

    private sealed class SemaphoreLease : IDisposable
    {
        private SemaphoreSlim _semaphore;
        public SemaphoreLease(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose() { }
    }
}

// Cooperative per-frame budget for the synchronous Instantiate + ContentPolice scrub tail of a
// bundle load (that work can't leave the main thread, so concurrent loads otherwise stack their
// tails into one hitch). Loads call WaitForBudgetAsync before the heavy step; once a frame has
// spent FrameBudgetMs on load tails, further loads defer to the next frame. Light frames still
// run many back-to-back, so mass joins aren't throttled. Main-thread only (no locks needed).
public static class BasisLoadFrameBudget
{
    // <= 0 disables the gate (original stack-in-one-frame behavior).
    public static double FrameBudgetMs = 4.0;

    private static int _budgetFrame = -1;
    private static double _spentThisFrameMs;

    public static async Task WaitForBudgetAsync()
    {
        if (FrameBudgetMs <= 0) return;
        ResetIfNewFrame();
        while (_spentThisFrameMs >= FrameBudgetMs)
        {
            await Task.Yield();
            ResetIfNewFrame();
        }
    }

    public static double BeginStep() => Time.realtimeSinceStartupAsDouble * 1000.0;

    public static void EndStep(double startMs)
    {
        if (FrameBudgetMs <= 0) return;
        ResetIfNewFrame();
        _spentThisFrameMs += Time.realtimeSinceStartupAsDouble * 1000.0 - startMs;
    }

    private static void ResetIfNewFrame()
    {
        int frame = Time.frameCount;
        if (frame != _budgetFrame)
        {
            _budgetFrame = frame;
            _spentThisFrameMs = 0;
        }
    }
}
