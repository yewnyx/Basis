using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Basis.ModelPickup.Tests.Validation;
using Basis.ModelPickup.Validation;
using Basis.Scripts.BasisSdk.Interactions;
using GLTFast;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using static Basis.ModelPickup.Tests.BasisModelUnityTestSupport;
using Object = UnityEngine.Object;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelPickupObjectTests
    {
        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<BasisModelImportResult> _results = new List<BasisModelImportResult>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _results.Count; i++)
                _results[i]?.Dispose();
            _results.Clear();
            for (int i = 0; i < _cleanup.Count; i++)
            {
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);
            }
            _cleanup.Clear();
        }

        private BasisModelPickupObject Build(bool isOwner, FakeHost host = null, Vector3 position = default, Quaternion? rotation = null)
        {
            BasisModelPickupObject pickup = BasisModelPickupObject.Build(
                Guid.NewGuid(),
                7,
                "Owner",
                isOwner,
                position,
                rotation ?? Quaternion.identity,
                host
            );
            _cleanup.Add(pickup.gameObject);
            return pickup;
        }

        private BasisModelImportResult Fake(float rendererSize = 10f)
        {
            BasisModelImportResult result = FakeImport(rendererSize);
            _cleanup.Add(result.Holder);
            _results.Add(result);
            return result;
        }

        private static void InvokeOnDestroy(BasisModelPickupObject pickup)
        {
            // Edit mode never sends OnDestroy to a plain MonoBehaviour, so the tests send it themselves.
            MethodInfo onDestroy = typeof(BasisModelPickupObject).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onDestroy, Is.Not.Null);
            onDestroy.Invoke(pickup, null);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, string message = null)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-5f), message + " expected " + expected + " but was " + actual);
        }

        private static Button ButtonNamed(GameObject panel, string name)
        {
            Button[] buttons = panel.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].name == name)
                    return buttons[i];
            }
            return null;
        }

        private static GameObject PanelOf(BasisModelPickupObject pickup)
        {
            Transform root = pickup.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == "BackPanel")
                    return child.gameObject;
            }
            return null;
        }

        [Test]
        public void TriggerBoxIsAddedBeforeTheInteractable()
        {
            BasisModelPickupObject pickup = Build(true);
            Component[] components = pickup.GetComponents<Component>();
            int box = Array.FindIndex(components, c => c is BoxCollider);
            int interactable = Array.FindIndex(components, c => c is BasisModelPickupInteractable);
            Assert.That(box, Is.GreaterThanOrEqualTo(0));
            Assert.That(interactable, Is.GreaterThan(box), "the interactable resolves its colliders once, in Awake");
            Assert.That(pickup.TriggerBox.isTrigger, Is.True);
            Assert.That(pickup.Interactable.RigidRef, Is.SameAs(pickup.Body));
            Assert.That(pickup.Interactable.GenerateColliderMesh, Is.True);
            Assert.That(pickup.Body.useGravity, Is.False);
            Assert.That(pickup.Body.isKinematic, Is.False, "the owner simulates its own body");
            Assert.That(Build(false).Body.isKinematic, Is.True, "a remote copy follows the controller");
        }

        [Test]
        public void PlaceholderHasARendererButNoCollider()
        {
            BasisModelPickupObject pickup = Build(true);
            GameObject placeholder = pickup.Placeholder;
            Assert.That(placeholder, Is.Not.Null);
            Assert.That(placeholder.transform.parent, Is.EqualTo(pickup.transform));
            Assert.That(placeholder.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(0));
            Assert.That(placeholder.TryGetComponent(out MeshRenderer renderer), Is.True);
            Assert.That(renderer.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
            Assert.That(pickup.IsLoading, Is.True);
        }

        [Test]
        public void UnknownShapeIsAQuarterMetreCube()
        {
            BasisModelPickupObject pickup = Build(true);
            Assert.That(pickup.HasKnownShape, Is.False);
            AssertVector(pickup.TriggerBox.size, Vector3.one * 0.25f);
            AssertVector(pickup.TriggerBox.center, Vector3.zero);
            AssertVector(pickup.Placeholder.transform.localScale, Vector3.one * 0.25f);
        }

        [Test]
        public void ShapeSizesTheBoxFromTheBoundsTimesTheBaseScale()
        {
            BasisModelPickupObject pickup = Build(true);
            pickup.SetShape(Box(0f, 0f, 0f, 2f, 1f, 4f), 0.125f);
            Assert.That(pickup.HasKnownShape, Is.True);
            Assert.That(pickup.BaseScale, Is.EqualTo(0.125f));
            AssertVector(pickup.TriggerBox.size, new Vector3(0.25f, 0.125f, 0.5f));
            AssertVector(pickup.TriggerBox.center, Vector3.zero);
            AssertVector(pickup.Placeholder.transform.localScale, new Vector3(0.25f, 0.125f, 0.5f));
            AssertVector(pickup.transform.localScale, Vector3.one, "the base scale never touches the root");
        }

        // A poster or decal is valid and has a zero axis; a zero-thickness box cannot be grabbed reliably.
        [Test]
        public void FlatBoundsStillGiveTheShapeItsMinimumThickness()
        {
            BasisModelPickupObject pickup = Build(true);
            pickup.SetShape(Box(0f, 0f, 0f, 1f, 0f, 1f), 0.5f);
            Vector3 size = pickup.TriggerBox.size;
            Assert.That(size.x, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(size.y, Is.GreaterThanOrEqualTo(BasisModelSizing.MinShapeAxisMeters));
            Assert.That(size.z, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(pickup.Placeholder.transform.localScale.y, Is.GreaterThanOrEqualTo(BasisModelSizing.MinShapeAxisMeters));
        }

        // The task's "ApplyLoadedModel keeps pose and controller state": adoption is a model swap, not a respawn.
        [Test]
        public void AdoptModelKeepsPoseAndControllerState()
        {
            var host = new FakeHost();
            Quaternion rotation = Quaternion.Euler(0f, 37f, 0f);
            BasisModelPickupObject owner = Build(true, host, new Vector3(1f, 2f, 3f), rotation);
            owner.transform.localScale = Vector3.one * 1.7f;
            owner.SetShape(Box(-1f, 0f, -1f, 1f, 2f, 1f), 0.25f);
            bool kinematicBefore = owner.Body.isKinematic;
            byte[] glb = { 1, 2, 3 };
            BasisModelImportResult result = Fake();
            GameObject holder = result.Holder;

            Assert.That(owner.TryAdoptModel(result, glb), Is.True);

            AssertVector(owner.transform.position, new Vector3(1f, 2f, 3f));
            Assert.That(Quaternion.Angle(owner.transform.rotation, rotation), Is.LessThan(0.01f));
            AssertVector(owner.transform.localScale, Vector3.one * 1.7f, "the user's scale is kept");
            Assert.That(owner.IsController, Is.True);
            Assert.That(owner.Body.isKinematic, Is.EqualTo(kinematicBefore));
            Assert.That(owner.IsLoading, Is.False);
            Assert.That(owner.Placeholder, Is.Null);
            Assert.That(owner.Holder, Is.SameAs(holder));
            Assert.That(holder.transform.parent, Is.EqualTo(owner.transform));
            Assert.That(holder.activeSelf, Is.True);
            Assert.That(owner.CanonicalGlb, Is.SameAs(glb));
            Assert.That(result.Holder, Is.Null, "ownership moved to the pickup");
            Assert.That(result.Import, Is.Null);
            Assert.That(owner.ShowSave, Is.True);

            BasisModelPickupObject remote = Build(false, host);
            remote.SetRemoteTarget(new Vector3(4f, 5f, 6f), rotation, 2f);
            remote.SetShape(Box(0f, 0f, 0f, 1f, 1f, 1f), 1f);
            Assert.That(remote.TryAdoptModel(Fake(), glb), Is.True);
            Assert.That(remote.IsController, Is.False);
            Assert.That(remote.Sync.HasRemoteTarget, Is.True);
            AssertVector(remote.transform.position, new Vector3(4f, 5f, 6f));
            AssertVector(remote.transform.localScale, Vector3.one * 2f);
            Assert.That(remote.Body.isKinematic, Is.True);
            Assert.That(host.Claims.Count, Is.EqualTo(0));
        }

        [Test]
        public void AdoptRefusesAFailedResultOrASecondModel()
        {
            BasisModelPickupObject pickup = Build(true);
            Assert.That(pickup.TryAdoptModel(null, null), Is.False);
            Assert.That(pickup.TryAdoptModel(new BasisModelImportResult { Ok = false }, null), Is.False);
            Assert.That(pickup.TryAdoptModel(Fake(), new byte[1]), Is.True);
            BasisModelImportResult second = Fake();
            Assert.That(pickup.TryAdoptModel(second, new byte[1]), Is.False);
            Assert.That(second.Holder, Is.Not.Null, "a refused result keeps its holder for the caller to dispose");
        }

        // The model's size lives on the holder; the holder offset puts the bounds centre on the root origin, in
        // Unity space (glTFast negates X), so the box and the model line up.
        [Test]
        public void BaseScaleAndCentringLiveOnTheHolder()
        {
            BasisModelPickupObject pickup = Build(true);
            pickup.SetShape(Box(0f, 1f, 2f, 2f, 3f, 4f), 2f);
            BasisModelImportResult result = Fake();
            GameObject holder = result.Holder;
            Assert.That(pickup.TryAdoptModel(result, null), Is.True);

            AssertVector(holder.transform.localScale, Vector3.one * 2f);
            AssertVector(holder.transform.localPosition, new Vector3(2f, -4f, -6f));
            AssertVector(pickup.transform.localScale, Vector3.one);

            // A later size decision rescales the adopted model too.
            pickup.SetShape(Box(0f, 1f, 2f, 2f, 3f, 4f), 0.5f);
            AssertVector(holder.transform.localScale, Vector3.one * 0.5f);
            AssertVector(holder.transform.localPosition, new Vector3(0.5f, -1f, -1.5f));
            AssertVector(pickup.TriggerBox.size, new Vector3(1f, 1f, 1f));
        }

        [Test]
        public void ColliderUsesTheValidatedBoundsNotRendererBounds()
        {
            BasisModelPickupObject pickup = Build(true);
            pickup.SetShape(Box(-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f), 0.4f);
            Assert.That(pickup.TryAdoptModel(Fake(rendererSize: 25f), null), Is.True);
            AssertVector(pickup.TriggerBox.size, Vector3.one * 0.4f);
            Assert.That(pickup.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(1), "the trigger box is the only collider");
        }

        [Test]
        public void BackPanelSitsAboveTheShapeFacingTheFront()
        {
            BasisModelPickupObject pickup = Build(true);
            pickup.SetShape(Box(0f, 0f, 0f, 1f, 1f, 1f), 0.5f);
            ((IBasisModelBackPanelHost)pickup).SetBackPanelVisible(true);
            GameObject panel = PanelOf(pickup);
            Assert.That(panel, Is.Not.Null);
            Assert.That(pickup.BackPanelVisible, Is.True);
            float expectedY = 0.25f + BasisModelPickupObject.BackPanelGapMeters + BasisModelPickupObject.BackPanelWorldHeight * 0.5f;
            AssertVector(panel.transform.localPosition, new Vector3(0f, expectedY, 0f));
            Assert.That(Quaternion.Angle(panel.transform.localRotation, Quaternion.Euler(0f, 180f, 0f)), Is.LessThan(0.01f));
            Assert.That(pickup.HideLabel, Is.Not.Null);
            Assert.That(pickup.DeleteLabel, Is.Not.Null);
            Assert.That(ButtonNamed(panel, "SaveButton"), Is.Not.Null);

            pickup.SetShape(Box(0f, 0f, 0f, 1f, 2f, 1f), 0.5f);
            expectedY = 0.5f + BasisModelPickupObject.BackPanelGapMeters + BasisModelPickupObject.BackPanelWorldHeight * 0.5f;
            AssertVector(panel.transform.localPosition, new Vector3(0f, expectedY, 0f), "the panel follows the shape");

            pickup.SetBackPanelVisible(false);
            Assert.That(pickup.BackPanelVisible, Is.False);
            Assert.That(PanelOf(pickup), Is.SameAs(panel), "hidden, not destroyed");
        }

        // Mobile receivers drop the received GLB, so Save has nothing to write.
        [Test]
        public void ShowSaveFalseHidesSave()
        {
            BasisModelPickupObject pickup = Build(false);
            pickup.SetBackPanelVisible(true);
            Assert.That(ButtonNamed(PanelOf(pickup), "SaveButton"), Is.Not.Null);

            pickup.SetShowSave(false);
            GameObject rebuilt = PanelOf(pickup);
            Assert.That(rebuilt, Is.Not.Null);
            Assert.That(pickup.BackPanelVisible, Is.True, "a visible panel is rebuilt visible");
            Assert.That(ButtonNamed(rebuilt, "SaveButton"), Is.Null);
            Assert.That(ButtonNamed(rebuilt, "HideButton"), Is.Not.Null);
            Assert.That(ButtonNamed(rebuilt, "DeleteButton"), Is.Not.Null);
            Assert.That(pickup.HideLabel.transform.IsChildOf(rebuilt.transform), Is.True, "labels bound to the new panel");

            BasisModelPickupObject dropped = Build(false);
            Assert.That(dropped.TryAdoptModel(Fake(), null), Is.True);
            Assert.That(dropped.ShowSave, Is.False, "adopting without a retained GLB hides Save");
            Assert.That(dropped.CanonicalGlb, Is.Null);
            dropped.SetBackPanelVisible(true);
            Assert.That(ButtonNamed(PanelOf(dropped), "SaveButton"), Is.Null);
        }

        // The host hears first, while the pickup still holds its model; then the holder goes, then the import.
        [UnityTest]
        public IEnumerator OnDestroyNotifiesTheHostThenDisposesTheModel()
        {
            yield return WarmUp();
            var host = new FakeHost();
            // Built first: the placeholder's shared cube mesh is loaded once and never released.
            BasisModelPickupObject pickup = Build(true, host);
            ObjectCounts before = CountObjects();
            BasisGlbValidationResult validated = Canonical(BasisGlbTestBuilder.IndexedQuad16());
            Task<BasisModelImportResult> task = BasisModelGltfLoader.ImportAsync(
                validated.CleanGlb,
                validated.Stats,
                Options,
                new UninterruptedDeferAgent(),
                new BasisModelImportTicket("destroy order", true)
            );
            yield return WaitFor(task);
            BasisModelImportResult result = task.Result;
            _results.Add(result);
            Assert.That(result.Ok, Is.True, result.Error);
            GltfImport import = result.Import;
            GameObject holder = result.Holder;
            Material shared = holder.GetComponentInChildren<MeshRenderer>(true).sharedMaterial;
            Assert.That(shared, Is.Not.Null, "glTFast's default material for a primitive without one");

            bool modelAliveWhenNotified = false;
            host.OnDestroyedProbe = p => modelAliveWhenNotified = p.Holder != null && import.Meshes != null;
            pickup.SetShape(validated.Stats.Bounds, 0.5f);
            Assert.That(pickup.TryAdoptModel(result, validated.CleanGlb), Is.True);

            InvokeOnDestroy(pickup);

            Assert.That(host.Destroyed.Count, Is.EqualTo(1));
            Assert.That(host.Destroyed[0], Is.SameAs(pickup));
            Assert.That(modelAliveWhenNotified, Is.True, "the host was told before anything was released");
            Assert.That(holder == null, Is.True, "holder destroyed");
            Assert.That(import.Meshes, Is.Null, "import disposed");
            Assert.That(import.MaterialCount, Is.EqualTo(0));
            Assert.That(shared != null, Is.True, "glTFast's shared default material is never destroyed");
            Assert.That(pickup.IsLoading, Is.True);
            Assert.That(pickup.CanonicalGlb, Is.Null);

            InvokeOnDestroy(pickup);
            Assert.That(host.Destroyed.Count, Is.EqualTo(1), "the host hears about a pickup once");

            ObjectCounts after = CountObjects();
            Assert.That(after.Meshes, Is.EqualTo(before.Meshes), "meshes released");
            Assert.That(after.Textures, Is.EqualTo(before.Textures), "textures released");
            Assert.That(after.Materials, Is.EqualTo(before.Materials), "materials released");
        }

        [Test]
        public void DestroyWhileLoadingAbandonsTheImport()
        {
            var host = new FakeHost();
            BasisModelPickupObject pickup = Build(false, host);
            var ticket = new BasisModelImportTicket("in flight", true);
            pickup.AttachTicket(ticket);
            InvokeOnDestroy(pickup);
            Assert.That(ticket.Abandoned, Is.True);
            Assert.That(host.Destroyed.Count, Is.EqualTo(1));

            var adopted = new BasisModelImportTicket("adopted", true);
            BasisModelPickupObject loaded = Build(false);
            loaded.AttachTicket(adopted);
            Assert.That(loaded.TryAdoptModel(Fake(), null), Is.True);
            InvokeOnDestroy(loaded);
            Assert.That(adopted.Abandoned, Is.False, "an adopted import is the pickup's own, not abandoned");
        }

        [Test]
        public void DemotionGoesKinematic()
        {
            BasisModelPickupObject pickup = Build(true);
            Assert.That(pickup.IsController, Is.True);
            Assert.That(pickup.Body.isKinematic, Is.False);
            pickup.SetController(false);
            Assert.That(pickup.IsController, Is.False);
            Assert.That(pickup.Body.isKinematic, Is.True);
            Assert.That(pickup.Body.linearVelocity, Is.EqualTo(Vector3.zero));
            pickup.SetController(true);
            Assert.That(pickup.IsController, Is.True);
        }

        [Test]
        public void GestureRangeIsAbsolute()
        {
            BasisModelPickupObject pickup = Build(true);
            PropertyInfo reference = typeof(BasisPickupInteractable).GetProperty("GestureScaleReference", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(reference, Is.Not.Null);
            pickup.transform.localScale = Vector3.one * 3f;
            Assert.That((float)reference.GetValue(pickup.Interactable), Is.EqualTo(1f));
            Assert.That(pickup.Interactable.enableScaleWithGesture, Is.True);
            Assert.That(pickup.Interactable.minScalePercent / 100f, Is.EqualTo(BasisModelSizing.MinUserScale).Within(1e-6f));
            Assert.That(pickup.Interactable.maxScalePercent / 100f, Is.EqualTo(BasisModelSizing.MaxUserScale).Within(1e-6f));
        }

        [Test]
        public void LiftAboveGroundRaisesTheShapeUntilTheUserMovesIt()
        {
            BasisModelPickupObject pickup = Build(true, null, new Vector3(0f, 0.1f, 0f));
            pickup.SetShape(Box(0f, 0f, 0f, 1f, 1f, 1f), 0.5f);
            pickup.LiftAboveGround(0f, 0.05f);
            Assert.That(pickup.transform.position.y, Is.EqualTo(0.3f).Within(1e-5f));
            pickup.LiftAboveGround(-10f, 0.05f);
            Assert.That(pickup.transform.position.y, Is.EqualTo(0.3f).Within(1e-5f), "never lowered");

            pickup.MovedByUser = true;
            pickup.LiftAboveGround(5f, 0.05f);
            Assert.That(pickup.transform.position.y, Is.EqualTo(0.3f).Within(1e-5f));
        }

        [Test]
        public void TransferLabelSitsUnderTheShape()
        {
            BasisModelPickupObject pickup = Build(true, null, new Vector3(0f, 2f, 0f));
            pickup.SetShape(Box(0f, 0f, 0f, 1f, 1f, 1f), 0.5f);
            pickup.transform.localScale = Vector3.one * 2f;
            float expected = 2f - (0.25f + BasisModelPickupObject.TransferLabelDropMeters) * 2f;
            AssertVector(pickup.TransferLabelAnchor, new Vector3(0f, expected, 0f));
        }

        [Test]
        public void HideTogglesTheModelOrThePlaceholder()
        {
            BasisModelPickupObject pickup = Build(true);
            pickup.OnHidePressed();
            Assert.That(pickup.Hidden, Is.True);
            Assert.That(pickup.Placeholder.activeSelf, Is.False, "while loading, the placeholder hides");

            BasisModelImportResult result = Fake();
            GameObject holder = result.Holder;
            Assert.That(pickup.TryAdoptModel(result, null), Is.True);
            Assert.That(holder.activeSelf, Is.False, "a hidden pickup adopts its model hidden");
            pickup.OnHidePressed();
            Assert.That(pickup.Hidden, Is.False);
            Assert.That(holder.activeSelf, Is.True);
        }

        [Test]
        public void DeleteNeedsTwoPressesAndGoesThroughTheHost()
        {
            var host = new FakeHost();
            BasisModelPickupObject pickup = Build(true, host);
            pickup.OnDeletePressed();
            Assert.That(host.Despawns.Count, Is.EqualTo(0));
            pickup.OnDeletePressed();
            Assert.That(host.Despawns, Is.EqualTo(new[] { pickup.ModelId }));
        }
    }
}
