using System;
using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelBackPanelTests
    {
        private sealed class FakeTarget : IBasisModelBackPanelTarget
        {
            public bool Owner;
            public string Name = "Someone";
            public bool Hidden;
            public int HidePresses;
            public int SavePresses;
            public int DeletePresses;
            public TextMeshProUGUI BoundHide;
            public TextMeshProUGUI BoundDelete;

            public bool IsOwner => Owner;
            public string OwnerName => Name;
            public bool IsHidden => Hidden;

            public void OnHidePressed()
            {
                HidePresses++;
            }

            public void OnSavePressed()
            {
                SavePresses++;
            }

            public void OnDeletePressed()
            {
                DeletePresses++;
            }

            public void BindBackPanelLabels(TextMeshProUGUI hideLabel, TextMeshProUGUI deleteLabel)
            {
                BoundHide = hideLabel;
                BoundDelete = deleteLabel;
            }
        }

        private static readonly BasisModelBackPanelLabels Labels = new BasisModelBackPanelLabels
        {
            SpawnedLocally = "L-local",
            SpawnedByFormat = "L-by[{0}]",
            Hide = "L-hide",
            Show = "L-show",
            Save = "L-save",
            Delete = "L-delete",
        };

        /// <summary>Above a pickup, facing back toward its front, as the model lays its panel out.</summary>
        private static BasisModelBackPanelLayout Layout()
        {
            return new BasisModelBackPanelLayout
            {
                LocalPosition = new Vector3(0f, 0.2f, 0f),
                LocalRotation = Quaternion.Euler(0f, 180f, 0f),
                WorldHeight = 0.5f,
                ShowSave = true,
            };
        }

        private readonly List<GameObject> _cleanup = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _cleanup.Count; i++)
            {
                if (_cleanup[i] != null)
                    UnityEngine.Object.DestroyImmediate(_cleanup[i]);
            }
            _cleanup.Clear();
        }

        private GameObject Host()
        {
            var host = new GameObject("BackPanelTestHost");
            _cleanup.Add(host);
            return host;
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

        private static TextMeshProUGUI SpawnerLabel(GameObject panel)
        {
            // The spawner label is the only label parented straight to the canvas; the rest sit in buttons.
            TextMeshProUGUI[] labels = panel.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i].transform.parent == panel.transform)
                    return labels[i];
            }
            return null;
        }

        [Test]
        public void BuildsTheCanvasAtTheLayoutPoseAndScale()
        {
            GameObject host = Host();
            BasisModelBackPanelLayout layout = Layout();

            GameObject panel = BasisModelBackPanel.Build(host.transform, new FakeTarget(), layout, Labels);

            Assert.That(panel.transform.parent, Is.EqualTo(host.transform));
            Assert.That(panel.activeSelf, Is.True);
            Assert.That(panel.transform.localPosition, Is.EqualTo(new Vector3(0f, 0.2f, 0f)));
            Assert.That(Quaternion.Angle(panel.transform.localRotation, Quaternion.Euler(0f, 180f, 0f)), Is.LessThan(0.01f));
            Assert.That(panel.transform.localScale.x, Is.EqualTo(0.5f / BasisModelBackPanel.PanelPixels).Within(1e-7f));
            Assert.That(panel.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.WorldSpace));
            Assert.That(((RectTransform)panel.transform).sizeDelta, Is.EqualTo(new Vector2(BasisModelBackPanel.PanelPixels, BasisModelBackPanel.PanelPixels)));

            var moved = new BasisModelBackPanelLayout
            {
                LocalPosition = new Vector3(0f, 0.4f, 0f),
                LocalRotation = Quaternion.identity,
                WorldHeight = 0.3f,
            };
            BasisModelBackPanel.Place(panel, moved);
            Assert.That(panel.transform.localPosition, Is.EqualTo(new Vector3(0f, 0.4f, 0f)));
            Assert.That(panel.transform.localScale.y, Is.EqualTo(0.3f / BasisModelBackPanel.PanelPixels).Within(1e-7f));
        }

        [Test]
        public void BindsHideAndDeleteLabels()
        {
            var target = new FakeTarget { Hidden = true };
            GameObject panel = BasisModelBackPanel.Build(Host().transform, target, Layout(), Labels);

            Assert.That(target.BoundHide, Is.Not.Null);
            Assert.That(target.BoundDelete, Is.Not.Null);
            Assert.That(target.BoundHide.text, Is.EqualTo("L-show"), "a hidden pickup offers Show");
            Assert.That(target.BoundDelete.text, Is.EqualTo("L-delete"));
            Assert.That(target.BoundHide.transform.parent.name, Is.EqualTo("HideButton"));
            Assert.That(target.BoundDelete.transform.parent.name, Is.EqualTo("DeleteButton"));

            ButtonNamed(panel, "HideButton").onClick.Invoke();
            ButtonNamed(panel, "SaveButton").onClick.Invoke();
            ButtonNamed(panel, "DeleteButton").onClick.Invoke();
            Assert.That(target.HidePresses, Is.EqualTo(1));
            Assert.That(target.SavePresses, Is.EqualTo(1));
            Assert.That(target.DeletePresses, Is.EqualTo(1));
        }

        [Test]
        public void ShowSaveFalseOmitsTheSaveButton()
        {
            BasisModelBackPanelLayout layout = Layout();
            layout.ShowSave = false;
            GameObject panel = BasisModelBackPanel.Build(Host().transform, new FakeTarget(), layout, Labels);

            Assert.That(ButtonNamed(panel, "SaveButton"), Is.Null);
            Assert.That(ButtonNamed(panel, "HideButton"), Is.Not.Null);
            Assert.That(ButtonNamed(panel, "DeleteButton"), Is.Not.Null);
        }

        [Test]
        public void LabelsAreUsedForEveryControl()
        {
            GameObject remote = BasisModelBackPanel.Build(
                Host().transform,
                new FakeTarget { Name = "Alice" },
                Layout(),
                Labels
            );
            Assert.That(SpawnerLabel(remote).text, Is.EqualTo("L-by[Alice]"));
            Assert.That(ButtonNamed(remote, "SaveButton").GetComponentInChildren<TextMeshProUGUI>().text, Is.EqualTo("L-save"));
            Assert.That(ButtonNamed(remote, "HideButton").GetComponentInChildren<TextMeshProUGUI>().text, Is.EqualTo("L-hide"));

            GameObject local = BasisModelBackPanel.Build(
                Host().transform,
                new FakeTarget { Owner = true, Name = "Unknown" },
                Layout(),
                Labels
            );
            Assert.That(SpawnerLabel(local).text, Is.EqualTo("L-local"));
        }

        [Test]
        public void OwnerLabelDisablesRichText()
        {
            const string hostile = "<color=red><size=400>Admin</size></color>";
            GameObject panel = BasisModelBackPanel.Build(
                Host().transform,
                new FakeTarget { Name = hostile },
                Layout(),
                Labels
            );

            TextMeshProUGUI label = SpawnerLabel(panel);
            Assert.That(label.richText, Is.False);
            Assert.That(label.text, Is.EqualTo("L-by[" + hostile + "]"), "shown literally, not escaped or interpreted");
        }

        [Test]
        public void ABrokenTranslationStillBuildsThePanel()
        {
            BasisModelBackPanelLabels broken = Labels;
            broken.SpawnedByFormat = "by {1}";
            GameObject panel = BasisModelBackPanel.Build(
                Host().transform,
                new FakeTarget { Name = "Carol" },
                Layout(),
                broken
            );

            StringAssert.Contains("Carol", SpawnerLabel(panel).text);
        }

        private sealed class FakeHost : MonoBehaviour, IBasisModelBackPanelHost
        {
            public bool Visible;
            public int Changes;

            public bool BackPanelVisible => Visible;

            public void SetBackPanelVisible(bool visible)
            {
                Visible = visible;
                Changes++;
            }
        }

        [Test]
        public void BackPanelSyncConvergesAtTheBudgetPerFrame()
        {
            // Spare capacity past the live count, as the manager's dense array has: the sync reads only `count`.
            var items = new FakeHost[8];
            const int count = 5;
            for (int i = 0; i < count; i++)
                items[i] = Host().AddComponent<FakeHost>();
            var sync = new BasisModelBackPanelSync();

            sync.Update(items, count, 2, true);
            Assert.That(CountVisible(items), Is.EqualTo(2));
            Assert.That(sync.Pending, Is.True);
            sync.Update(items, count, 2, true);
            sync.Update(items, count, 2, true);
            Assert.That(CountVisible(items), Is.EqualTo(5));
            Assert.That(sync.Pending, Is.False, "the scan stops once every pickup agrees");

            sync.Update(items, count, 2, true);
            int changes = 0;
            for (int i = 0; i < count; i++)
                changes += items[i].Changes;
            Assert.That(changes, Is.EqualTo(5), "a settled set is not touched again");

            sync.Update(items, count, 10, false);
            Assert.That(CountVisible(items), Is.EqualTo(0));

            sync.MarkPending();
            Assert.That(sync.Pending, Is.True);
            sync.Reset();
            Assert.That(sync.Pending, Is.False);
            Assert.That(sync.Visible, Is.False);
        }

        private static int CountVisible(FakeHost[] items)
        {
            int count = 0;
            foreach (FakeHost host in items)
            {
                if (host != null && host.Visible)
                    count++;
            }
            return count;
        }
    }
}
