using System;
using System.IO;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.ModelPickup.Validation;
using Basis.Scripts.Device_Management.Devices;
using GLTFast;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.ModelPickup
{
    /// <summary>What a model pickup asks of whoever manages it. A pickup built without a host keeps its requests to itself.</summary>
    public interface IBasisModelPickupHost
    {
        /// <summary>The pickup's GameObject is going away, by any path. Called before the pickup releases its model.</summary>
        void OnPickupDestroyed(BasisModelPickupObject pickup);

        /// <summary>The user confirmed Delete on the back panel.</summary>
        void RequestDespawn(Guid id);

        /// <summary>The local player grabbed a pickup someone else controls.</summary>
        void ClaimControl(Guid id);
    }

    /// <summary>
    /// A spawned 3D model pickup. The root carries the trigger box, the body and the interactable, and its scale is
    /// the user's gesture scale; the model's own size (the base scale) lives on the "Model" child glTFast built, which
    /// is offset so the model's validated bounds centre sits on the root origin. Until the import lands, a placeholder
    /// box stands in at the size the bounds will have. Shape always comes from the validator's bounds, never from
    /// renderer bounds. Any client can grab it; grabbing claims movement authority.
    /// </summary>
    public class BasisModelPickupObject : MonoBehaviour, IBasisModelBackPanelTarget, IBasisModelBackPanelHost
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;

        public const float UnknownShapeEdgeMeters = 0.25f;
        public const float BackPanelWorldHeight = 0.3f;
        public const float BackPanelGapMeters = 0.04f;
        public const float TransferLabelDropMeters = 0.06f;
        public const float DeleteConfirmSeconds = 3f;
        // BasisModelSizing.MinUserScale and MaxUserScale as percentages of the pinned gesture reference of 1.
        public const float MinUserScalePercent = 10f;
        public const float MaxUserScalePercent = 1000f;

        private const string HideKey = "modelPickup.panel.hide";
        private const string ShowKey = "modelPickup.panel.show";
        private const string DeleteKey = "modelPickup.panel.delete";
        private const string ConfirmKey = "modelPickup.panel.confirm";

        private static readonly Color PlaceholderColor = new Color(0.16f, 0.17f, 0.20f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [NonSerialized] public Guid ModelId;
        public ushort OwnerId;
        public string OwnerName;
        public bool IsOwner;

        /// <summary>The user has grabbed it; automatic placement leaves it alone from then on.</summary>
        public bool MovedByUser;

        public TextMeshProUGUI HideLabel;
        public TextMeshProUGUI DeleteLabel;

        /// <summary>Movement authority and the follower's target. The manager decides when to send through it.</summary>
        public readonly BasisModelTransformSync Sync = new BasisModelTransformSync();

        // Runtime state below is [NonSerialized]: public for callers, never for Unity's serializer (the GLB alone
        // can be 32 MB, and none of it means anything outside the session that built it).

        /// <summary>The claims this pickup counts against budgets: header claims while inbound, the sender's own once validated.</summary>
        [NonSerialized] public BasisGlbClaims Claims;

        /// <summary>Canonical GLB length, for budget totals.</summary>
        [NonSerialized] public int TotalBytes;

        /// <summary>This pickup's own transform, read once in <see cref="Build"/> instead of through <c>transform</c> on every use.</summary>
        [NonSerialized] public Transform Root;

        /// <summary>Its slot in the manager's dense model array and follow pass; -1 while untracked. The manager's to write.</summary>
        [NonSerialized] public int ManagerIndex = -1;

        /// <summary>Half the shape's height in root-local metres: the box, or the unknown-shape cube. Kept by <see cref="ApplyShapeSize"/>.</summary>
        [NonSerialized] public float HalfHeight = UnknownShapeEdgeMeters * 0.5f;

        /// <summary>The user hid the model with the back panel. Changed by <see cref="OnHidePressed"/>.</summary>
        [NonSerialized] public bool Hidden;

        /// <summary>The shape comes from bounds rather than the unknown-shape cube. Changed by <see cref="SetShape"/> and <see cref="SetUnknownShape"/>.</summary>
        [NonSerialized] public bool HasKnownShape;

        /// <summary>The model's own size on the holder; the root carries the user's scale. Changed by <see cref="SetShape"/>.</summary>
        [NonSerialized] public float BaseScale = 1f;

        /// <summary>The canonical GLB kept for Save (and for re-sending, on the owner). Null when not retained. Set by <see cref="TryAdoptModel"/>.</summary>
        [NonSerialized] public byte[] CanonicalGlb;

        /// <summary>Validated glTF-space bounds behind the current shape. Default while the shape is unknown.</summary>
        [NonSerialized] public BasisGlbAabb Bounds;

        [NonSerialized] public BasisModelPickupInteractable Interactable;
        [NonSerialized] public Rigidbody Body;
        [NonSerialized] public BoxCollider TriggerBox;

        /// <summary>The loading box; null once a model is adopted.</summary>
        [NonSerialized] public GameObject Placeholder;

        /// <summary>The adopted model's holder; null while loading. Owned by this pickup, destroyed with it.</summary>
        [NonSerialized] public GameObject Holder;

        // Kept private: these carry ownership (the host's callbacks, the import still in flight, the glTFast import
        // that must be disposed after the holder) or a timer's state.
        private IBasisModelPickupHost _host;
        private BasisModelImportTicket _ticket;
        private GltfImport _import;
        private GameObject _backPanel;
        private bool _deleteArmed;
        private bool _saveInFlight;

        private static Mesh s_CubeMesh;
        private static Material s_PlaceholderMaterial;
        private static bool s_PlaceholderMaterialWarned;

        public bool IsController => Sync.IsController;

        /// <summary>True until an imported model is adopted: the placeholder is showing.</summary>
        public bool IsLoading => Holder == null;

        public bool BackPanelVisible => _backPanel != null && _backPanel.activeSelf;

        /// <summary>
        /// Whether the back panel offers Save. Mobile receivers drop the received GLB after import, so they hide it
        /// from the start. Change it through <see cref="SetShowSave"/>, which rebuilds a panel that is already built.
        /// </summary>
        [NonSerialized] public bool ShowSave = true;

        /// <summary>Sets <see cref="ShowSave"/> and rebuilds the back panel if it is already built.</summary>
        public void SetShowSave(bool value)
        {
            if (ShowSave == value)
                return;
            ShowSave = value;
            if (_backPanel == null)
                return;
            bool visible = _backPanel.activeSelf;
            CancelInvoke(nameof(DisarmDelete));
            DisarmDelete();
            _backPanel.SetActive(false);
            BasisModelGltfLoader.DestroyUnityObject(_backPanel);
            _backPanel = null;
            if (visible)
                SetBackPanelVisible(true);
        }

        /// <summary>Where the transfer readout sits: just under the shape, following the user's scale.</summary>
        public Vector3 TransferLabelAnchor
        {
            get
            {
                Transform root = Root;
                root.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                return position - (rotation * Vector3.up) * ((HalfHeight + TransferLabelDropMeters) * root.localScale.y);
            }
        }

        /// <summary>
        /// Builds a pickup in its loading state: an unknown-shape placeholder until <see cref="SetShape"/> and
        /// <see cref="TryAdoptModel"/>. The root is a scene root marked DontDestroyOnLoad (as image pickups are), so a
        /// pickup raised during a join is not unloaded with the loading scene; the manager owns its lifetime.
        /// </summary>
        public static BasisModelPickupObject Build(
            Guid id,
            ushort ownerId,
            string ownerName,
            bool isOwner,
            Vector3 position,
            Quaternion rotation,
            IBasisModelPickupHost host
        )
        {
            var root = new GameObject("BasisModelPickup_" + id.ToString("N").Substring(0, 8));
            Transform rootTransform = root.transform;
            rootTransform.SetPositionAndRotation(position, rotation);
            if (Application.isPlaying)
                DontDestroyOnLoad(root);
            int interactableLayer = LayerMask.NameToLayer("Interactable");
            if (interactableLayer >= 0)
                root.layer = interactableLayer;

            var pickup = root.AddComponent<BasisModelPickupObject>();
            pickup.Root = rootTransform;
            pickup._host = host;
            pickup.ModelId = id;
            pickup.OwnerId = ownerId;
            pickup.OwnerName = string.IsNullOrEmpty(ownerName) ? "Unknown" : ownerName;
            pickup.IsOwner = isOwner;
            if (isOwner)
                pickup.Sync.Promote();

            // Renderer only: a collider on a child would join the set the interactable resolves below.
            var placeholder = new GameObject("Placeholder");
            placeholder.layer = root.layer;
            placeholder.transform.SetParent(rootTransform, false);
            placeholder.AddComponent<MeshFilter>().sharedMesh = GetCubeMesh();
            var placeholderRenderer = placeholder.AddComponent<MeshRenderer>();
            Material material = GetPlaceholderMaterial();
            if (material != null)
                placeholderRenderer.sharedMaterial = material;
            placeholderRenderer.shadowCastingMode = ShadowCastingMode.Off;
            placeholderRenderer.receiveShadows = false;
            pickup.Placeholder = placeholder;

            // Before the interactable: its Awake resolves the collider set once, and later colliders (the back
            // panel's canvas) must never join the grab and highlight shapes.
            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = Vector3.zero;
            pickup.TriggerBox = box;

            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = !isOwner;
            body.useGravity = false;
            body.linearDamping = 1.5f;
            body.angularDamping = 2.5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            pickup.Body = body;

            var interactable = root.AddComponent<BasisModelPickupInteractable>();
            interactable.RigidRef = body;
            interactable.GenerateColliderMesh = true;
            interactable.enableScaleWithGesture = true;
            interactable.minScalePercent = MinUserScalePercent;
            interactable.maxScalePercent = MaxUserScalePercent;
            interactable.OnInteractStartEvent.AddListener(pickup.OnLocalGrabbed);
            pickup.Interactable = interactable;

            pickup.SetUnknownShape();
            return pickup;
        }

        /// <summary>A 0.25 m cube: placeholder and box, before the header claims or the local validation say more.</summary>
        public void SetUnknownShape()
        {
            HasKnownShape = false;
            Bounds = default;
            BaseScale = 1f;
            ApplyShapeSize(new Vector3(UnknownShapeEdgeMeters, UnknownShapeEdgeMeters, UnknownShapeEdgeMeters));
            PlaceBackPanel();
        }

        /// <summary>
        /// Sizes the placeholder and the box to the validated bounds times <paramref name="baseScale"/> (each axis at
        /// least <see cref="BasisModelSizing.MinShapeAxisMeters"/>, so flat models stay grabbable), rescales an
        /// adopted model, and moves the back panel. Callers pass a base scale already clamped by
        /// <see cref="BasisModelSizing"/>.
        /// </summary>
        public void SetShape(in BasisGlbAabb bounds, float baseScale)
        {
            Bounds = bounds;
            BaseScale = baseScale;
            HasKnownShape = true;
            BasisModelSizing.ShapeSize(bounds, baseScale, out float x, out float y, out float z);
            ApplyShapeSize(new Vector3(x, y, z));
            if (Holder != null)
                PlaceHolder();
            PlaceBackPanel();
        }

        /// <summary>
        /// Raises the pickup so its shape clears the ground, unless the user has already moved it. A follower that
        /// had settled eases again, as it always did, back to where its controller has it.
        /// </summary>
        public void LiftAboveGround(float groundY, float clearance)
        {
            if (MovedByUser)
                return;
            Transform root = Root;
            Vector3 position = root.position;
            float minimumY = groundY + clearance + HalfHeight * root.localScale.y;
            if (position.y < minimumY)
            {
                position.y = minimumY;
                root.position = position;
                Sync.Settled = false;
            }
        }

        /// <summary>The import in flight for this pickup; abandoned if the pickup is destroyed first.</summary>
        public void AttachTicket(BasisModelImportTicket ticket)
        {
            _ticket = ticket;
        }

        /// <summary>
        /// Takes ownership of a successful import's holder and glTFast import (the result keeps neither), swaps the
        /// placeholder for the model, and keeps <paramref name="canonicalGlb"/> for Save; null keeps nothing and hides
        /// Save (Mobile receivers). Pose, user scale and movement authority are untouched. False, with nothing
        /// taken, when the result is not a live model or a model is already adopted.
        /// </summary>
        public bool TryAdoptModel(BasisModelImportResult result, byte[] canonicalGlb)
        {
            if (result == null || !result.Ok || result.Holder == null || Holder != null)
                return false;

            if (Interactable != null)
                Interactable.HighlightObject(false);
            GameObject holder = result.Holder;
            Holder = holder;
            _import = result.Import;
            result.Holder = null;
            result.Import = null;
            _ticket = null;

            holder.transform.SetParent(Root, false);
            PlaceHolder();
            if (Placeholder != null)
            {
                // Immediately, like the framework's own highlight clone: the highlight rebuild below collects
                // MeshRenderers, and a Destroy()ed placeholder would still be found this frame.
                DestroyImmediate(Placeholder);
                Placeholder = null;
            }
            holder.SetActive(!Hidden);
            if (Interactable != null)
                Interactable.RebuildHighlight();

            CanonicalGlb = canonicalGlb;
            if (canonicalGlb == null)
                SetShowSave(false);
            return true;
        }

        /// <summary>False: someone else took it. A local hold is dropped, velocities zeroed and the body made kinematic.</summary>
        public void SetController(bool value)
        {
            if (value)
            {
                Sync.Promote();
                return;
            }
            Sync.Demote(Root, Body, Interactable);
        }

        /// <summary>The pose to follow, already validated by the caller. The first call snaps.</summary>
        public void SetRemoteTarget(Vector3 position, Quaternion rotation, float userScale)
        {
            Sync.SetRemoteTarget(Root, position, rotation, userScale);
        }

        /// <summary>
        /// Shows or hides the back panel, following the main menu. Built on first show and then only toggled; hiding
        /// disarms a pending Delete.
        /// </summary>
        public void SetBackPanelVisible(bool visible)
        {
            if (!visible)
            {
                if (_backPanel == null)
                    return;
                CancelInvoke(nameof(DisarmDelete));
                DisarmDelete();
                _backPanel.SetActive(false);
                return;
            }

            if (_backPanel == null)
            {
                _backPanel = BasisModelBackPanel.Build(Root, this, CurrentPanelLayout(), PanelLabels());
                return;
            }
            _backPanel.SetActive(true);
        }

        public void OnHidePressed()
        {
            Hidden = !Hidden;
            if (Holder != null)
                Holder.SetActive(!Hidden);
            else if (Placeholder != null)
                Placeholder.SetActive(!Hidden);
            if (HideLabel != null)
                HideLabel.text = BasisLocalization.Get(Hidden ? ShowKey : HideKey);
        }

        /// <summary>
        /// Writes the canonical GLB (the validated, re-emitted file, never the dropped source) to the save folder. The
        /// path is resolved on the main thread; only the write runs on a worker.
        /// </summary>
        public async void OnSavePressed()
        {
            byte[] bytes = CanonicalGlb;
            if (bytes == null || bytes.Length == 0 || _saveInFlight)
                return;
            _saveInFlight = true;
            try
            {
                string folder = SaveFolder();
                string path = Path.Combine(folder, BasisModelFileRules.GenerateSaveFileName(Guid.NewGuid()));
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(path, bytes);
                });
                BasisDebug.Log("Model pickup saved to " + path, LogTag);
            }
            catch (Exception e)
            {
                BasisDebug.LogError("Model pickup save failed: " + e.Message, LogTag);
            }
            finally
            {
                _saveInFlight = false;
            }
        }

        /// <summary>Two-step: the first press arms Delete for a few seconds, the second asks the host to despawn.</summary>
        public void OnDeletePressed()
        {
            if (!_deleteArmed)
            {
                _deleteArmed = true;
                if (DeleteLabel != null)
                    DeleteLabel.text = BasisLocalization.Get(ConfirmKey);
                CancelInvoke(nameof(DisarmDelete));
                Invoke(nameof(DisarmDelete), DeleteConfirmSeconds);
                return;
            }

            CancelInvoke(nameof(DisarmDelete));
            _deleteArmed = false;
            _host?.RequestDespawn(ModelId);
        }

        /// <summary>Documents\Basis\Models on Windows; Basis/Models under persistent data elsewhere (inside the migrated "Basis" folder).</summary>
        public static string SaveFolder()
        {
#if UNITY_STANDALONE_WIN
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Basis", "Models");
#else
            return Path.Combine(Application.persistentDataPath, "Basis", "Models");
#endif
        }

        private void DisarmDelete()
        {
            _deleteArmed = false;
            if (DeleteLabel != null)
                DeleteLabel.text = BasisLocalization.Get(DeleteKey);
        }

        private void OnLocalGrabbed(BasisInput input)
        {
            // Released, it should stay where it was thrown rather than fall back to the follower's kinematic state.
            if (Interactable != null)
                Interactable._previousKinematicValue = false;
            MovedByUser = true;
            if (!Sync.IsController)
                _host?.ClaimControl(ModelId);
        }

        private void ApplyShapeSize(Vector3 size)
        {
            if (TriggerBox != null)
            {
                TriggerBox.center = Vector3.zero;
                TriggerBox.size = size;
                HalfHeight = size.y * 0.5f;
            }
            if (Placeholder != null)
                Placeholder.transform.localScale = size;
        }

        /// <summary>Base scale on the holder, and the holder offset so the bounds centre lands on the root origin.</summary>
        private void PlaceHolder()
        {
            BasisModelSizing.HolderLocalPosition(Bounds, BaseScale, out float x, out float y, out float z);
            Transform holder = Holder.transform;
            holder.SetLocalPositionAndRotation(new Vector3(x, y, z), Quaternion.identity);
            holder.localScale = new Vector3(BaseScale, BaseScale, BaseScale);
        }

        /// <summary>Above the shape, facing the root's +Z: the model's front, which faces the dropper at spawn.</summary>
        private BasisModelBackPanelLayout CurrentPanelLayout()
        {
            return new BasisModelBackPanelLayout
            {
                LocalPosition = new Vector3(0f, HalfHeight + BackPanelGapMeters + BackPanelWorldHeight * 0.5f, 0f),
                LocalRotation = Quaternion.Euler(0f, 180f, 0f),
                WorldHeight = BackPanelWorldHeight,
                ShowSave = ShowSave,
            };
        }

        private void PlaceBackPanel()
        {
            if (_backPanel != null)
                BasisModelBackPanel.Place(_backPanel, CurrentPanelLayout());
        }

        private static BasisModelBackPanelLabels PanelLabels()
        {
            return new BasisModelBackPanelLabels
            {
                SpawnedLocally = BasisLocalization.Get("modelPickup.panel.spawnedLocally"),
                SpawnedByFormat = BasisLocalization.Get("modelPickup.panel.spawnedBy"),
                Hide = BasisLocalization.Get(HideKey),
                Show = BasisLocalization.Get(ShowKey),
                Save = BasisLocalization.Get("modelPickup.panel.save"),
                Delete = BasisLocalization.Get(DeleteKey),
            };
        }

        /// <summary>Unity's built-in cube, which outlives the temporary primitive it is read from. Never destroyed.</summary>
        private static Mesh GetCubeMesh()
        {
            if (s_CubeMesh != null)
                return s_CubeMesh;
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (temp.TryGetComponent(out MeshFilter filter))
                s_CubeMesh = filter.sharedMesh;
            DestroyImmediate(temp);
            return s_CubeMesh;
        }

        /// <summary>Shared by every placeholder and never destroyed. None in edit mode, where the bundled shaders are absent.</summary>
        private static Material GetPlaceholderMaterial()
        {
            if (s_PlaceholderMaterial != null)
                return s_PlaceholderMaterial;
            BundledContentHolder content = BundledContentHolder.Instance;
            if (content == null || content.UnlitUrpShader == null)
            {
                BasisDebug.LogWarningOnce(ref s_PlaceholderMaterialWarned, "Model pickup placeholders have no material: the bundled unlit shader is not loaded.", LogTag);
                return null;
            }
            s_PlaceholderMaterial = new Material(content.UnlitUrpShader) { name = "BasisModelPickupPlaceholder" };
            if (s_PlaceholderMaterial.HasProperty(BaseColorId))
                s_PlaceholderMaterial.SetColor(BaseColorId, PlaceholderColor);
            else
                s_PlaceholderMaterial.color = PlaceholderColor;
            return s_PlaceholderMaterial;
        }

        /// <summary>
        /// Tells the host first, then abandons an import still in flight (the loader disposes it when it lands), then
        /// releases an adopted model: the holder before the import, as glTFast's ownership requires.
        /// </summary>
        private void OnDestroy()
        {
            IBasisModelPickupHost host = _host;
            _host = null;
            try
            {
                host?.OnPickupDestroyed(this);
            }
            finally
            {
                if (_ticket != null)
                {
                    _ticket.Abandoned = true;
                    _ticket = null;
                }
                GameObject holder = Holder;
                GltfImport import = _import;
                Holder = null;
                _import = null;
                CanonicalGlb = null;
                BasisModelGltfLoader.DestroyModel(holder, import);
            }
        }

        bool IBasisModelBackPanelTarget.IsOwner => IsOwner;
        string IBasisModelBackPanelTarget.OwnerName => OwnerName;
        bool IBasisModelBackPanelTarget.IsHidden => Hidden;

        void IBasisModelBackPanelTarget.OnHidePressed()
        {
            OnHidePressed();
        }

        void IBasisModelBackPanelTarget.OnSavePressed()
        {
            OnSavePressed();
        }

        void IBasisModelBackPanelTarget.OnDeletePressed()
        {
            OnDeletePressed();
        }

        void IBasisModelBackPanelTarget.BindBackPanelLabels(TextMeshProUGUI hideLabel, TextMeshProUGUI deleteLabel)
        {
            HideLabel = hideLabel;
            DeleteLabel = deleteLabel;
        }
    }
}
