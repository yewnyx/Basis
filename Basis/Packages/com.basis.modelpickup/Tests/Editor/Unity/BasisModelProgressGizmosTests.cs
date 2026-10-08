using System;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelProgressGizmosTests
    {
        private BasisModelProgressGizmos _first;
        private BasisModelProgressGizmos _second;

        [SetUp]
        public void SetUp()
        {
            _first = new BasisModelProgressGizmos("TestFirst_Progress_");
            _second = new BasisModelProgressGizmos("TestSecond_Progress_");
        }

        [TearDown]
        public void TearDown()
        {
            _first.Shutdown();
            _second.Shutdown();
            DestroyGizmoParent();
        }

        /// <summary>
        /// <c>BasisGizmoManager.DestroyAll</c> uses play-mode <c>Destroy</c>, which edit mode refuses with an
        /// error, so the labels this fixture created (now back in the gizmo pool) are torn down here instead.
        /// </summary>
        private static void DestroyGizmoParent()
        {
            GameObject parent = BasisGizmoManager.Parent;
            if (parent == null)
                return;
            BasisTextGizmos[] labels = parent.GetComponentsInChildren<BasisTextGizmos>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i].MaterialInstance != null)
                    UnityEngine.Object.DestroyImmediate(labels[i].MaterialInstance);
            }
            UnityEngine.Object.DestroyImmediate(parent);
            BasisGizmoManager.Parent = null;
        }

        private static bool LabelNamed(string name)
        {
            GameObject parent = BasisGizmoManager.Parent;
            if (parent == null)
                return false;
            Transform root = parent.transform;
            int childCount = root.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.gameObject.activeSelf && child.name == name)
                    return true;
            }
            return false;
        }

        [Test]
        public void UnreportedLabelsAreRemovedAtEndFrame()
        {
            Guid kept = BasisModelShareTestIds.Make(1);
            Guid finished = BasisModelShareTestIds.Make(2);
            _first.BeginFrame();
            _first.Report(kept, Vector3.zero, 0.1f, 10f, false);
            _first.Report(finished, Vector3.zero, 0.9f, 10f, false);
            _first.EndFrame();
            Assert.That(_first.ActiveCount, Is.EqualTo(2));

            _first.BeginFrame();
            _first.Report(kept, Vector3.zero, 0.2f, 10f, false);
            _first.EndFrame();

            Assert.That(_first.ActiveCount, Is.EqualTo(1));
            Assert.That(LabelNamed("TestFirst_Progress_" + finished.ToString("N")), Is.False);

            _first.Remove(kept);
            Assert.That(_first.ActiveCount, Is.EqualTo(0));
        }

        [Test]
        public void LabelNamesUseThePrefix()
        {
            Guid id = BasisModelShareTestIds.Make(3);
            _second.BeginFrame();
            _second.Report(id, Vector3.zero, 1f, 0f, true);

            Assert.That(LabelNamed("TestSecond_Progress_" + id.ToString("N")), Is.True);
            Assert.That(LabelNamed("TestFirst_Progress_" + id.ToString("N")), Is.False);
        }
    }
}
